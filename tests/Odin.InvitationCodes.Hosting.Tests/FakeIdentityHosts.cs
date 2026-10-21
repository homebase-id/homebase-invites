using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// Identity hosts' plan API (odin-core, /api/plans/v1 on the provisioning domain), faked in memory: which host
/// serves which identity on which plan, what each host answers instead, and every request made.
/// </summary>
public sealed class FakeIdentityHosts : HttpMessageHandler
{
    public const string PlanKey = "test-plan-key";

    // host -> identity -> plan id
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _identities = new();

    /// <summary>Plans each host has; null means any</summary>
    public ConcurrentDictionary<string, HashSet<string>> PlansOnHost { get; } = new();

    /// <summary>A status a host answers everything with instead, e.g. 503 while it is down</summary>
    public ConcurrentDictionary<string, HttpStatusCode> Broken { get; } = new();

    public ConcurrentQueue<(string Method, string Host, string Path, string? Body)> Requests { get; } = new();

    public void Serve(string host, string identity, string? planId = "free") =>
        _identities.GetOrAdd(host, _ => new())[identity] = planId;

    public void Forget(string host, string identity) => _identities.GetOrAdd(host, _ => new()).TryRemove(identity, out _);

    public string? PlanOn(string host, string identity) =>
        _identities.TryGetValue(host, out var identities) && identities.TryGetValue(identity, out var plan) ? plan : null;

    public List<string> HostsAsked() => Requests.Select(r => r.Host).ToList();

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

        if (!_identities.TryGetValue(host, out var identities) || !identities.ContainsKey(identity))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (setting && request.Method == HttpMethod.Put)
        {
            var planId = JsonDocument.Parse(body!).RootElement.GetProperty("planId").GetString()!;
            if (PlansOnHost.TryGetValue(host, out var plans) && !plans.Contains(planId))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    { Content = new StringContent($"'{planId}' is not a plan in Registry:Plans") };
            }
            identities[identity] = planId;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { domain = identity, planId = identities[identity] }) };
    }
}
