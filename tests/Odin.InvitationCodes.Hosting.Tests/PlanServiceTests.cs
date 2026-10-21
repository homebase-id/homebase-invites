using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// Granting plans: finding the host that serves an identity, setting the plan there, and what a grant becomes on
/// each answer; retries and the daily reconcile. Against a temp database and <see cref="FakeIdentityHosts"/>.
/// </summary>
public class PlanServiceTests
{
    private const string One = "provisioning.one.example";
    private const string Two = "provisioning.two.example";
    private const string Three = "provisioning.three.example";
    private const string Frodo = "frodo.dotyou.cloud";

    private string _path = null!;
    private PlanGrantStore _store = null!;
    private FakeIdentityHosts _hosts = null!;
    private ManualTime _time = null!;
    private PlanService _plans = null!;

    [SetUp]
    public void SetUp()
    {
        _path = TempDatabase.NewPath();
        _store = new PlanGrantStore(_path);
        _hosts = new FakeIdentityHosts();
        _time = new ManualTime();
        _plans = new PlanService(_store, new PlanHosts([One, Two, Three], FakeIdentityHosts.PlanKey),
            new SingleHandlerFactory(_hosts), _time, NullLogger<PlanService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _hosts.Dispose();
        TempDatabase.Delete(_path);
    }

    //
    // Finding the host
    //

    [Test]
    public async Task TheHintIsAskedFirst()
    {
        _hosts.Serve(Three, Frodo);

        var located = await _plans.LocateAsync(Frodo, hint: Three);

        Assert.That(located.Host, Is.EqualTo(Three));
        Assert.That(_hosts.HostsAsked(), Is.EqualTo(new[] { Three }));
    }

    [Test]
    public async Task AnUnknownHintIsNeverCalled()
    {
        _hosts.Serve(Two, Frodo);

        var located = await _plans.LocateAsync(Frodo, hint: "attacker.example");

        Assert.That(located.Host, Is.EqualTo(Two));
        Assert.That(_hosts.HostsAsked(), Does.Not.Contain("attacker.example"));
        Assert.That(_hosts.HostsAsked(), Is.EqualTo(new[] { One, Two }));
    }

    [Test]
    public async Task EveryKnownHostIsAskedUntilOneHasIt()
    {
        _hosts.Serve(Three, Frodo, "veterans");

        var located = await _plans.LocateAsync(Frodo, hint: null);

        Assert.That(located, Is.EqualTo((Three, "veterans", (string?)null)));
        Assert.That(_hosts.HostsAsked(), Is.EqualTo(new[] { One, Two, Three }));
    }

    [Test]
    public async Task TheLastHostSeenIsAskedFirst()
    {
        _hosts.Serve(Three, Frodo);
        await _plans.GrantAsync(Frodo, "veterans", null, null, null);
        _hosts.Requests.Clear();

        await _plans.LocateAsync(Frodo, hint: null);

        Assert.That(_hosts.HostsAsked(), Is.EqualTo(new[] { Three }));
    }

    [Test]
    public async Task NoHostHavingItSaysSo()
    {
        var located = await _plans.LocateAsync(Frodo, hint: null);

        Assert.That(located.Host, Is.Null);
        Assert.That(located.Error, Is.EqualTo($"no known host serves {Frodo}"));
    }

    [Test]
    public async Task AHostThatIsDownIsNamedInTheReason()
    {
        _hosts.Broken[One] = HttpStatusCode.ServiceUnavailable;

        var located = await _plans.LocateAsync(Frodo, hint: null);

        Assert.That(located.Host, Is.Null);
        Assert.That(located.Error, Does.Contain($"{One}: 503"));
    }

    //
    // Applying
    //

    [Test]
    public async Task AGrantIsSetOnItsHostAndApplied()
    {
        _hosts.Serve(Two, Frodo, "free");

        var grant = await _plans.GrantAsync(Frodo, "veterans", "purchase", "pay_123", null);

        Assert.That(grant.Status, Is.EqualTo(PlanGrantStatus.Applied));
        Assert.That(grant.Host, Is.EqualTo(Two));
        Assert.That(grant.AppliedAt, Is.Not.Null);
        Assert.That(_hosts.PlanOn(Two, Frodo), Is.EqualTo("veterans"));
        var put = _hosts.Requests.Single(r => r.Method == "PUT");
        Assert.That(put.Path, Is.EqualTo($"/api/plans/v1/identities/{Frodo}/plan"));
        Assert.That(put.Body, Does.Contain("\"reference\":\"pay_123\""));
    }

    [Test]
    public async Task APlanTheHostDoesNotHaveFails()
    {
        _hosts.Serve(One, Frodo);
        _hosts.PlansOnHost[One] = ["free", "veterans"];

        var grant = await _plans.GrantAsync(Frodo, "gold", null, null, null);

        Assert.That(grant.Status, Is.EqualTo(PlanGrantStatus.Failed));
        Assert.That(grant.LastError, Does.Contain("refused the plan"));
        Assert.That(grant.NextAttemptAt, Is.Null, "not retried by itself");
    }

    [Test]
    public async Task AHostThatIsDownLeavesItPendingWithBackoff()
    {
        _hosts.Serve(One, Frodo);
        _hosts.Broken[One] = HttpStatusCode.ServiceUnavailable;

        var first = await _plans.GrantAsync(Frodo, "veterans", null, null, One);

        Assert.That(first.Status, Is.EqualTo(PlanGrantStatus.Pending));
        Assert.That(first.Attempts, Is.EqualTo(1));
        Assert.That(first.NextAttemptAt - _time.Ms, Is.EqualTo(60_000));

        _time.Advance(TimeSpan.FromMinutes(1));
        await _plans.ApplyDueAsync();
        var second = (await _store.GetAsync(first.Id))!;
        Assert.That(second.Attempts, Is.EqualTo(2));
        Assert.That(second.NextAttemptAt - _time.Ms, Is.EqualTo(120_000), "doubles");

        _hosts.Broken.Clear();
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.That(await _plans.ApplyDueAsync(), Is.EqualTo(1));
        Assert.That((await _store.GetAsync(first.Id))!.Status, Is.EqualTo(PlanGrantStatus.Applied));
    }

    [Test]
    public async Task TheBackoffStopsGrowingAtSixHours()
    {
        _hosts.Broken[One] = _hosts.Broken[Two] = _hosts.Broken[Three] = HttpStatusCode.BadGateway;
        var grant = await _plans.GrantAsync(Frodo, "veterans", null, null, null);
        for (var i = 0; i < 15; i++)
        {
            _time.Advance(TimeSpan.FromDays(1));
            await _plans.ApplyDueAsync();
        }

        grant = (await _store.GetAsync(grant.Id))!;
        Assert.That(grant.Status, Is.EqualTo(PlanGrantStatus.Pending), "a paid plan is never given up on");
        Assert.That(grant.NextAttemptAt - _time.Ms, Is.EqualTo((long)TimeSpan.FromHours(6).TotalMilliseconds));
    }

    [Test]
    public async Task AnIdentityOnNoHostStaysPending()
    {
        var grant = await _plans.GrantAsync(Frodo, "veterans", null, null, null);

        Assert.That(grant.Status, Is.EqualTo(PlanGrantStatus.Pending));
        Assert.That(grant.LastError, Is.EqualTo($"no known host serves {Frodo}"));
    }

    [Test]
    public async Task AnIdentityThatMovedIsFollowed()
    {
        _hosts.Serve(One, Frodo);
        await _plans.GrantAsync(Frodo, "free", null, null, null);
        _hosts.Forget(One, Frodo);
        _hosts.Serve(Three, Frodo);

        var grant = await _plans.GrantAsync(Frodo, "veterans", null, null, null);

        Assert.That(grant.Status, Is.EqualTo(PlanGrantStatus.Applied));
        Assert.That(grant.Host, Is.EqualTo(Three));
        Assert.That(await _store.GetKnownHostAsync(Frodo), Is.EqualTo(Three));
    }

    [Test]
    public async Task TheSameReferenceIsTheSameGrant()
    {
        _hosts.Serve(One, Frodo);

        var first = await _plans.GrantAsync(Frodo, "veterans", "purchase", "pay_1", null);
        var again = await _plans.GrantAsync(Frodo, "veterans", "purchase", "pay_1", null);

        Assert.That(again.Id, Is.EqualTo(first.Id));
        Assert.That(_hosts.Requests.Count(r => r.Method == "PUT"), Is.EqualTo(1), "not set twice");
        Assert.That(await _store.ListAsync(Frodo, null), Has.Count.EqualTo(1));
    }

    [Test]
    public void AReferenceReusedForAnotherPlanIsRefused()
    {
        _hosts.Serve(One, Frodo);

        Assert.ThrowsAsync<PlanGrantConflictException>(async () =>
        {
            await _plans.GrantAsync(Frodo, "veterans", null, "pay_1", null);
            await _plans.GrantAsync(Frodo, "registered", null, "pay_1", null);
        });
    }

    [Test]
    public async Task RetryTriesAFailedGrantAgain()
    {
        _hosts.Serve(One, Frodo);
        _hosts.PlansOnHost[One] = ["free"];
        var grant = await _plans.GrantAsync(Frodo, "veterans", null, null, null);
        Assert.That(grant.Status, Is.EqualTo(PlanGrantStatus.Failed));

        _hosts.PlansOnHost[One].Add("veterans");
        var retried = await _plans.RetryAsync(grant.Id);

        Assert.That(retried!.Status, Is.EqualTo(PlanGrantStatus.Applied));
        Assert.That(await _plans.RetryAsync(grant.Id), Is.Null, "an applied grant is not retried");
    }

    //
    // Reconcile
    //

    [Test]
    public async Task ReconcileSetsBackAPlanTheHostLost()
    {
        _hosts.Serve(Two, Frodo);
        _hosts.Serve(Two, "sam.dotyou.cloud");
        await _plans.GrantAsync(Frodo, "registered", null, null, null);
        await _plans.GrantAsync(Frodo, "veterans", null, null, null);
        await _plans.GrantAsync("sam.dotyou.cloud", "veterans", null, null, null);

        _hosts.Serve(Two, Frodo, "free"); // changed by hand on the host

        var corrected = await _plans.ReconcileAsync();

        Assert.That(corrected, Is.EqualTo(new[] { Frodo }));
        Assert.That(_hosts.PlanOn(Two, Frodo), Is.EqualTo("veterans"), "the latest applied grant, not an older one");
        Assert.That(_hosts.PlanOn(Two, "sam.dotyou.cloud"), Is.EqualTo("veterans"));
    }

    [Test]
    public async Task ReconcileLeavesMatchingPlansAlone()
    {
        _hosts.Serve(Two, Frodo);
        await _plans.GrantAsync(Frodo, "veterans", null, null, null);
        _hosts.Requests.Clear();

        Assert.That(await _plans.ReconcileAsync(), Is.Empty);
        Assert.That(_hosts.Requests.Count(r => r.Method == "PUT"), Is.Zero);
    }

    //
    // Hosts config
    //

    [Test]
    public void HostsMustBeDomainNamesAndNeedTheKey()
    {
        Assert.Throws<InvalidOperationException>(() => new PlanHosts(["https://provisioning.one.example"], "k"));
        Assert.Throws<InvalidOperationException>(() => new PlanHosts([One], ""));
        Assert.DoesNotThrow(() => new PlanHosts([], ""));
        Assert.That(new PlanHosts([" Provisioning.ONE.example ", One], "k").Hosts, Is.EqualTo(new[] { One }));
    }

    //

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public long Ms => _now.ToUnixTimeMilliseconds();

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
