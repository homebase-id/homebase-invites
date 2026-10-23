using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Raven.Hosting;

/// <summary>
/// The identity hosts the service may set plans on: their provisioning domains, and the one plan key they all take
/// (odin-core <c>Registry:AccountService:ApiKey</c>). A host not listed here is never called, whatever a request says.
/// </summary>
public class PlanHosts
{
    public PlanHosts(IEnumerable<string> hosts, string apiKey)
    {
        Hosts = hosts.Select(h => DomainNames.Normalize(h) ??
                                  throw new InvalidOperationException($"RavenHosting:IdentityHosts: '{h}' is not a domain name"))
            .Distinct()
            .ToList();
        ApiKey = apiKey.Trim();
        if (Hosts.Count > 0 && ApiKey == "")
        {
            throw new InvalidOperationException("RavenHosting:AccountServiceApiKey is not configured, but RavenHosting:IdentityHosts is");
        }
    }

    /// <summary>Provisioning domains, e.g. provisioning.demo.dominion.id; empty when no host is configured</summary>
    public IReadOnlyList<string> Hosts { get; }

    public string ApiKey { get; }

    public bool IsKnown(string? host) => host != null && Hosts.Contains(host);

    public static PlanHosts FromConfiguration(IConfiguration configuration) =>
        new(configuration.GetSection("RavenHosting:IdentityHosts").Get<string[]>() ?? [],
            configuration["RavenHosting:AccountServiceApiKey"] ?? "");
}

/// <summary>
/// Sets the plans grants owe on the identity hosts. Finds the host that serves an identity by asking the known
/// hosts (the hint and the last host seen first), sets the plan there with the grant's reference, and keeps a grant
/// pending, with backoff, until its host has it. The host API (odin-core #1994, on the provisioning domain):
/// <c>GET /api/plans/v1/identities/{identity}</c> answers <c>{domain, planId, quotaBytes, publicWebPresence}</c>, and
/// <c>PUT .../{identity}/plan {planId, quotaBytes, publicWebPresence, reference}</c> sets them; values left out stay as
/// they are. Both are 404 for an identity the host does not serve.
/// </summary>
public class PlanService(
    PlanGrantStore store,
    PlanStore planStore,
    PlanHosts hosts,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<PlanService> logger)
{
    public const string HttpClientName = "plan-hosts";
    public const string ApiKeyHeader = "Odin-Api-Key";

    // Pending grants wait 1, 2, 4... minutes between attempts, at most 6 hours: a paid plan is never given up on
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromHours(6);

    // One grant at a time, so the worker and an API call never set the same plan at once
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    //

    /// <summary>
    /// Makes the grant (or finds the one with this reference) and tries to set it on its host straight away. The
    /// grant records what the plan gives now, and for a purchase the term bought and its price now; a plan that does
    /// not exist, or a term it has no price for, is refused.
    /// </summary>
    /// <param name="months">The billing term bought, for a purchase; null for none</param>
    public async Task<PlanGrant> GrantAsync(string? identity, string? planId, string? source, string? reference,
        string? host, int? months = null)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            throw new InvalidPlanGrantRequestException("planId is required");
        }
        var plan = await planStore.GetAsync(planId) ??
                   throw new InvalidPlanGrantRequestException($"There is no plan '{planId.Trim()}'");

        var price = months == null
            ? null
            : plan.PriceFor(months) ??
              throw new InvalidPlanGrantRequestException($"Plan {plan.Id} has no price for {months} months");

        var (grant, created) = await store.CreateAsync(identity, plan, price, source, reference, host, Now);
        return created || grant.Status == PlanGrantStatus.Pending ? await ApplyAsync(grant) : grant;
    }

    /// <summary>
    /// Tries a pending or failed grant again now; null when there is no such grant or it is already applied
    /// </summary>
    public async Task<PlanGrant?> RetryAsync(long id)
    {
        var grant = await store.MakeDueAsync(id, Now);
        return grant == null ? null : await ApplyAsync(grant);
    }

    /// <summary>
    /// Tries every pending grant that is due; returns how many it tried
    /// </summary>
    public async Task<int> ApplyDueAsync(int limit = 100)
    {
        var due = await store.DueAsync(Now, limit);
        foreach (var grant in due)
        {
            await ApplyAsync(grant);
        }
        return due.Count;
    }

    //

    /// <summary>
    /// Sets the grant's plan on the host that serves its identity. Applied when the host says so; failed when the
    /// host refuses it (400: an empty plan Id or a quota that is not more than 0); otherwise pending, to try again
    /// later.
    /// </summary>
    public async Task<PlanGrant> ApplyAsync(PlanGrant grant)
    {
        await _applyLock.WaitAsync();
        try
        {
            var current = await store.GetAsync(grant.Id) ?? grant;
            if (current.Status != PlanGrantStatus.Pending)
            {
                return current;
            }

            var located = await LocateAsync(current.Identity, current.HostHint);
            if (located.Host == null)
            {
                return await PendingAsync(current, located.Error!);
            }

            var outcome = await SetPlanAsync(located.Host, current);
            if (outcome.Status == HttpStatusCode.NotFound)
            {
                // It moved since it was found: look again, without the cache, and try the host that has it now
                await store.ForgetKnownHostAsync(current.Identity);
                located = await LocateAsync(current.Identity, hint: null, skip: located.Host);
                if (located.Host == null)
                {
                    return await PendingAsync(current, located.Error!);
                }
                outcome = await SetPlanAsync(located.Host, current);
            }

            switch (outcome.Status)
            {
                case HttpStatusCode.OK:
                    await store.SetKnownHostAsync(current.Identity, located.Host, Now);
                    logger.LogInformation("Set {identity} to plan {planId} on {host} (grant {id}, reference {reference})",
                        current.Identity, current.PlanId, located.Host, current.Id, current.Reference);
                    return (await store.MarkAppliedAsync(current.Id, located.Host, Now))!;

                case HttpStatusCode.BadRequest:
                    logger.LogWarning("{host} refused plan {planId} for {identity} (grant {id}): {error}",
                        located.Host, current.PlanId, current.Identity, current.Id, outcome.Error);
                    return (await store.MarkFailedAsync(current.Id, located.Host,
                        $"{located.Host} refused the plan: {outcome.Error}", Now))!;

                default:
                    return await PendingAsync(current, $"{located.Host}: {outcome.Error}");
            }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    //

    /// <summary>
    /// Re-sets the plan each identity's latest applied grant gave it, where its host says it has another plan Id,
    /// quota or public web presence: changed by hand, or lost. Only what both sides have is compared: a grant from
    /// before plans were kept here by its plan Id alone, and a host that does not say its quota or public web
    /// presence (from before odin-core #1994) not on those. Returns the identities it corrected.
    /// </summary>
    public async Task<List<string>> ReconcileAsync()
    {
        var corrected = new List<string>();
        foreach (var grant in await store.LatestAppliedAsync())
        {
            await _applyLock.WaitAsync();
            try
            {
                var located = await LocateIdentityAsync(grant.Identity, grant.Host, skip: null);
                var drift = located.Identity == null ? null : Drift(grant, located.Identity);
                if (located.Host == null || drift == null)
                {
                    continue;
                }

                var outcome = await SetPlanAsync(located.Host, grant);
                if (outcome.Status == HttpStatusCode.OK)
                {
                    await store.SetKnownHostAsync(grant.Identity, located.Host, Now);
                    corrected.Add(grant.Identity);
                    logger.LogWarning("Reconcile: {identity} had {drift} on {host}, set back to plan {planId} (grant {id})",
                        grant.Identity, drift, located.Host, grant.PlanId, grant.Id);
                }
                else
                {
                    logger.LogWarning("Reconcile: could not set {identity} back to {planId} on {host}: {error}",
                        grant.Identity, grant.PlanId, located.Host, outcome.Error);
                }
            }
            finally
            {
                _applyLock.Release();
            }
        }
        return corrected;
    }

    //

    /// <summary>
    /// Which host serves the identity, and the plan it has there. Asks the hint first if it is a known host, then the
    /// host last seen, then every other known host; the first to answer 200 has it. Host is null when none did, with
    /// the reason in Error.
    /// </summary>
    public async Task<(string? Host, string? PlanId, string? Error)> LocateAsync(string identity, string? hint,
        string? skip = null)
    {
        var located = await LocateIdentityAsync(identity, hint, skip);
        return (located.Host, located.Identity?.PlanId, located.Error);
    }

    // As LocateAsync, with all the host said about the identity
    private async Task<(string? Host, HostIdentity? Identity, string? Error)> LocateIdentityAsync(string identity,
        string? hint, string? skip)
    {
        if (hosts.Hosts.Count == 0)
        {
            return (null, null, "no identity hosts are configured (RavenHosting:IdentityHosts)");
        }

        var order = new List<string>();
        foreach (var candidate in new[] { DomainNames.Normalize(hint), await store.GetKnownHostAsync(identity) }
                     .Concat(hosts.Hosts))
        {
            if (hosts.IsKnown(candidate) && candidate != skip && !order.Contains(candidate!))
            {
                order.Add(candidate!);
            }
        }

        var unreachable = new List<string>();
        foreach (var host in order)
        {
            var answer = await GetPlanAsync(host, identity);
            if (answer.Status == HttpStatusCode.OK)
            {
                return (host, answer.Identity, null);
            }
            if (answer.Status != HttpStatusCode.NotFound)
            {
                unreachable.Add($"{host}: {answer.Error}");
            }
        }

        return (null, null, unreachable.Count == 0
            ? $"no known host serves {identity}"
            : $"no known host said it serves {identity}; " + string.Join("; ", unreachable));
    }

    //

    private async Task<PlanGrant> PendingAsync(PlanGrant grant, string error)
    {
        // The attempt this records is attempt grant.Attempts + 1; wait 1, 2, 4... minutes after it
        var wait = TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks,
            FirstRetry.Ticks * (1L << Math.Min(grant.Attempts, 20))));
        logger.LogInformation("Grant {id} for {identity} still pending, retry in {wait}: {error}",
            grant.Id, grant.Identity, wait, error);
        return (await store.MarkPendingAsync(grant.Id, error, Now + (long)wait.TotalMilliseconds, Now))!;
    }

    // What differs between the grant and the identity on its host, e.g. "quota 1048576"; null when nothing does
    private static string? Drift(PlanGrant grant, HostIdentity identity)
    {
        var drift = new List<string>();
        if (!string.Equals(identity.PlanId, grant.PlanId, StringComparison.OrdinalIgnoreCase))
        {
            drift.Add($"plan {identity.PlanId ?? "none"}");
        }
        if (grant.QuotaBytes != null && identity.QuotaBytes != null && grant.QuotaBytes != identity.QuotaBytes)
        {
            drift.Add($"quota {identity.QuotaBytes}");
        }
        if (grant.PublicWebPresence != null && identity.PublicWebPresence != null &&
            grant.PublicWebPresence != identity.PublicWebPresence)
        {
            drift.Add($"public web presence {identity.PublicWebPresence}");
        }
        return drift.Count == 0 ? null : string.Join(", ", drift);
    }

    private async Task<(HttpStatusCode? Status, HostIdentity? Identity, string? Error)> GetPlanAsync(string host,
        string identity)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, IdentityUrl(host, identity));
            request.Headers.Add(ApiKeyHeader, hosts.ApiKey);
            using var response = await Client().SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (response.StatusCode, null, await DescribeAsync(response));
            }
            var body = await response.Content.ReadFromJsonAsync<HostIdentity>();
            return (HttpStatusCode.OK, body ?? new HostIdentity(identity, null, null, null), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, null, e.Message);
        }
    }

    private async Task<(HttpStatusCode? Status, string? Error)> SetPlanAsync(string host, PlanGrant grant)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, IdentityUrl(host, grant.Identity) + "/plan");
            request.Headers.Add(ApiKeyHeader, hosts.ApiKey);
            request.Content = JsonContent.Create(
                new HostSetPlan(grant.PlanId, grant.QuotaBytes, grant.PublicWebPresence, grant.Reference));
            using var response = await Client().SendAsync(request);
            return (response.StatusCode, response.IsSuccessStatusCode ? null : await DescribeAsync(response));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, e.Message);
        }
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    private static string IdentityUrl(string host, string identity) =>
        $"https://{host}/api/plans/v1/identities/{Uri.EscapeDataString(identity)}";

    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var status = $"{(int)response.StatusCode} {response.StatusCode}";
        return string.IsNullOrWhiteSpace(body) ? status : $"{status}: {(body.Length > 200 ? body[..200] : body)}";
    }

    // QuotaBytes and PublicWebPresence are null from hosts before odin-core #1994, which do not say them
    private record HostIdentity(string? Domain, string? PlanId, long? QuotaBytes, bool? PublicWebPresence);

    // A grant from before plans were kept here leaves out the quota and public web presence: the host keeps its own
    private record HostSetPlan(
        string PlanId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? QuotaBytes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PublicWebPresence,
        string Reference);
}

/// <summary>
/// Tries pending grants as they fall due, and once a day sets back any applied plan a host no longer has as granted
/// </summary>
public class PlanGrantWorker(PlanService plans, IConfiguration configuration, ILogger<PlanGrantWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("RavenHosting:GrantRetryIntervalSeconds", 60));
        var reconcileEvery = TimeSpan.FromHours(configuration.GetValue("RavenHosting:ReconcileIntervalHours", 24));
        var nextReconcile = DateTimeOffset.UtcNow + reconcileEvery;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await plans.ApplyDueAsync();
                if (DateTimeOffset.UtcNow >= nextReconcile)
                {
                    nextReconcile = DateTimeOffset.UtcNow + reconcileEvery;
                    var corrected = await plans.ReconcileAsync();
                    logger.LogInformation("Reconcile corrected {count} plan(s)", corrected.Count);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Plan grant worker failed; trying again next round");
            }
        }
    }
}
