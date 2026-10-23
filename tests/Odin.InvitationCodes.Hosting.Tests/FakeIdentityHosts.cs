using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// Identity hosts' plan API (odin-core #1994, /api/plans/v1 on the provisioning domain), faked in memory: which host
/// serves which identity with which plan, quota and public web presence, what each host answers instead, and every
/// request made.
/// </summary>
public sealed class FakeIdentityHosts : HttpMessageHandler
{
    public const string PlanKey = "test-plan-key";

    /// <summary>odin-core's HostingProvider:DefaultQuotaBytes: the quota of an identity with none stored</summary>
    public const long DefaultQuotaBytes = 64424509440;

    /// <summary>What a host has for an identity. QuotaBytes is the quota in effect, as the host reports it.</summary>
    public record Identity(string? PlanId, long QuotaBytes = DefaultQuotaBytes, bool PublicWebPresence = true);

    // host -> identity -> what it has
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Identity>> _identities = new();

    /// <summary>A status a host answers everything with instead, e.g. 503 while it is down</summary>
    public ConcurrentDictionary<string, HttpStatusCode> Broken { get; } = new();

    /// <summary>Hosts that refuse to set any plan (400), as odin-core does for an empty plan or a bad quota</summary>
    public ConcurrentDictionary<string, string> RefusesPlans { get; } = new();

    /// <summary>Hosts from before odin-core #1994: they answer only {domain, planId} and ignore what plans give</summary>
    public HashSet<string> Old { get; } = [];

    public ConcurrentQueue<(string Method, string Host, string Path, string? Body)> Requests { get; } = new();

    public void Serve(string host, string identity, string? planId = "free", long quotaBytes = DefaultQuotaBytes,
        bool publicWebPresence = true) =>
        _identities.GetOrAdd(host, _ => new())[identity] = new Identity(planId, quotaBytes, publicWebPresence);

    public void Forget(string host, string identity) => _identities.GetOrAdd(host, _ => new()).TryRemove(identity, out _);

    public Identity? On(string host, string identity) =>
        _identities.TryGetValue(host, out var identities) && identities.TryGetValue(identity, out var has) ? has : null;

    public string? PlanOn(string host, string identity) => On(host, identity)?.PlanId;

    public List<string> HostsAsked() => Requests.Select(r => r.Host).ToList();

    public JsonElement LastPut() => JsonDocument.Parse(Requests.Last(r => r.Method == "PUT").Body!).RootElement;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        var path = request.RequestUri.AbsolutePath;
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.Method.Method, host, path, body));

        if (Broken.TryGetValue(host, out var broken))
        {
            return new HttpResponseMessage(broken);
        }
        if (!request.Headers.TryGetValues("Odin-Api-Key", out var keys) || keys.Single() != PlanKey)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        const string prefix = "/api/plans/v1/identities/";
        if (!path.StartsWith(prefix))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var rest = Uri.UnescapeDataString(path[prefix.Length..]);
        var setting = rest.EndsWith("/plan");
        var identity = setting ? rest[..^"/plan".Length] : rest;

        if (!_identities.TryGetValue(host, out var identities) || !identities.TryGetValue(identity, out var has))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (setting && request.Method == HttpMethod.Put)
        {
            if (RefusesPlans.TryGetValue(host, out var refusal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(refusal) };
            }

            // Values left out keep what the identity has
            var set = JsonDocument.Parse(body!).RootElement;
            has = has with { PlanId = set.GetProperty("planId").GetString() };
            if (!Old.Contains(host))
            {
                if (set.TryGetProperty("quotaBytes", out var quota))
                {
                    has = has with { QuotaBytes = quota.GetInt64() };
                }
                if (set.TryGetProperty("publicWebPresence", out var publicWebPresence))
                {
                    has = has with { PublicWebPresence = publicWebPresence.GetBoolean() };
                }
            }
            identities[identity] = has;
        }

        object answer = Old.Contains(host)
            ? new { domain = identity, planId = has.PlanId }
            : new { domain = identity, planId = has.PlanId, quotaBytes = has.QuotaBytes, publicWebPresence = has.PublicWebPresence };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer) };
    }
}
