using System.Net;
using System.Net.Http.Json;

namespace Raven.Hosting.Tests;

/// <summary>
/// The plan endpoints over HTTP, in process: who may call them, what they answer, and their 400s, 404s and 409s
/// </summary>
public class PlanApiTests
{
    private const string Root = "/api/v1/plans";

    private InvitationCodeServer _server = null!;
    private HttpClient _admin = null!;

    [SetUp]
    public void SetUp()
    {
        _server = new InvitationCodeServer();
        _admin = _server.ClientWithKey(InvitationCodeServer.CreateKey);
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _server.Dispose();
    }

    private static IEnumerable<TestCaseData> EveryEndpoint()
    {
        yield return new TestCaseData(HttpMethod.Post, "/", Gold).SetName("plan create");
        yield return new TestCaseData(HttpMethod.Get, "/", null).SetName("plans");
        yield return new TestCaseData(HttpMethod.Get, "/veterans", null).SetName("plan");
        yield return new TestCaseData(HttpMethod.Patch, "/veterans", new { forSale = false }).SetName("plan edit");
        yield return new TestCaseData(HttpMethod.Delete, "/tiny-test", null).SetName("plan delete");
    }

    [TestCaseSource(nameof(EveryEndpoint))]
    public async Task EveryEndpointNeedsTheCreateKey(HttpMethod method, string path, object? body)
    {
        foreach (var key in new[] { null, "wrong", InvitationCodeServer.SignupKey, FakeIdentityHosts.PlanKey })
        {
            using var client = _server.ClientWithKey(key);
            var request = new HttpRequestMessage(method, Root + path);
            if (body != null)
            {
                request.Content = JsonContent.Create(body);
            }
            var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"key: {key ?? "(none)"}");
        }
        Assert.That((await _admin.GetFromJsonAsync<Plan>(Root + "/veterans"))!.ForSale, Is.True, "nothing changed");
    }

    [Test]
    public async Task APlanIsCreatedListedAndFoundInAnyCase()
    {
        var response = await _admin.PostAsJsonAsync(Root + "/", Gold);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var created = (await response.Content.ReadFromJsonAsync<Plan>())!;
        Assert.That(created with { CreatedAt = 0, UpdatedAt = 0 }, Is.EqualTo(new Plan("Gold", "Gold plan", "Shiny",
            5368709120, false, true, "$2 a month", 1, 0, 0)));
        Assert.That(created.Storage, Is.EqualTo("5 GB"));
        Assert.That(await _admin.GetFromJsonAsync<Plan>(Root + "/gold"), Is.EqualTo(created));

        var plans = (await _admin.GetFromJsonAsync<List<Plan>>(Root + "/"))!;
        Assert.That(plans.Select(p => p.Id), Is.EqualTo(new[] { "free", "pro-100gb", "tiny-test", "Gold", "registered", "veterans" }),
            "by sort order, then id");
        Assert.That((await _admin.GetAsync(Root + "/silver")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task APlanStartsNotForSaleWithNoPriceOrDescription()
    {
        var response = await _admin.PostAsJsonAsync(Root + "/",
            new { id = "plain", name = "Plain", quotaBytes = 1, publicWebPresence = true });

        var plan = (await response.Content.ReadFromJsonAsync<Plan>())!;
        Assert.That((plan.ForSale, plan.Price, plan.Description, plan.SortOrder), Is.EqualTo((false, "", "", 0)));
    }

    [Test]
    public async Task AnEditChangesOnlyWhatItGives()
    {
        var before = (await _admin.GetFromJsonAsync<Plan>(Root + "/veterans"))!;

        var response = await _admin.PatchAsJsonAsync(Root + "/VETERANS", new { forSale = false, price = "" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = (await response.Content.ReadFromJsonAsync<Plan>())!;
        Assert.That(after with { UpdatedAt = 0 }, Is.EqualTo(before with { ForSale = false, Price = "", UpdatedAt = 0 }));
        Assert.That(after.UpdatedAt, Is.GreaterThanOrEqualTo(before.UpdatedAt));
        Assert.That((await _admin.PatchAsJsonAsync(Root + "/silver", new { forSale = false })).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase("{}", "Nothing to change", TestName = "an edit of nothing")]
    [TestCase("""{"quotaBytes":0}""", "more than 0", TestName = "an edit to a quota of 0")]
    [TestCase("""{"quotaBytes":-1}""", "more than 0", TestName = "an edit to a negative quota")]
    [TestCase("""{"name":" "}""", "name cannot be empty", TestName = "an edit to an empty name")]
    public async Task ABadEditIsA400(string change, string message)
    {
        var response = await _admin.PatchAsync(Root + "/veterans",
            new StringContent(change, System.Text.Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(message));
    }

    [TestCase(null, "Gold", 1L, true, "id is required", TestName = "a plan without an id")]
    [TestCase("has space", "Gold", 1L, true, "Invalid id", TestName = "a plan with a bad id")]
    [TestCase("GRANTS", "Gold", 1L, true, "cannot be a plan id", TestName = "a plan called grants")]
    [TestCase("gold", " ", 1L, true, "name is required", TestName = "a plan without a name")]
    [TestCase("gold", "Gold", 0L, true, "more than 0", TestName = "a plan with a quota of 0")]
    [TestCase("gold", "Gold", -5L, true, "more than 0", TestName = "a plan with a negative quota")]
    [TestCase("gold", "Gold", null, true, "quotaBytes is required", TestName = "a plan without a quota")]
    [TestCase("gold", "Gold", 1L, null, "publicWebPresence is required", TestName = "a plan without a public page")]
    public async Task ABadPlanIsA400(string? id, string? name, long? quotaBytes, bool? publicWebPresence, string message)
    {
        var response = await _admin.PostAsJsonAsync(Root + "/", new PlanRequest(id, name, quotaBytes, publicWebPresence));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(message));
    }

    [Test]
    public async Task ATakenIdIsAConflictWhateverItsCase()
    {
        var response = await _admin.PostAsJsonAsync(Root + "/", new PlanRequest("Veterans", "Again", 1, true));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await _admin.GetFromJsonAsync<Plan>(Root + "/veterans"))!.Name, Is.EqualTo("Veterans"));
    }

    [Test]
    public async Task APlanInUseCannotBeDeleted()
    {
        await _server.CreateCodeAsync(planId: "pro-100gb");
        var grant = await _admin.PostAsJsonAsync("/api/v1/plans/grants/", new { identity = "frodo.dotyou.cloud", planId = "Registered" });
        Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        foreach (var used in new[] { "pro-100gb", "REGISTERED" })
        {
            var response = await _admin.DeleteAsync(Root + "/" + used);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), used);
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("mark it not for sale instead"));
            Assert.That((await _admin.GetAsync(Root + "/" + used)).StatusCode, Is.EqualTo(HttpStatusCode.OK), "still there");
        }
    }

    [Test]
    public async Task APlanNothingUsesCanBeDeleted()
    {
        Assert.That((await _admin.DeleteAsync(Root + "/Tiny-Test")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _admin.GetAsync(Root + "/tiny-test")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _admin.DeleteAsync(Root + "/tiny-test")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ThereAreNoBuiltInPlans()
    {
        using var server = new InvitationCodeServer(seedPlans: false);
        using var admin = server.ClientWithKey(InvitationCodeServer.CreateKey);

        Assert.That(await admin.GetFromJsonAsync<List<Plan>>(Root + "/"), Is.Empty);
    }

    [Test]
    public async Task TheGrantsAreStillAtTheirOwnRoute()
    {
        var response = await _admin.GetAsync(Root + "/grants");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadFromJsonAsync<List<PlanGrant>>(), Is.Empty);
    }

    //

    private static readonly PlanRequest Gold = new("Gold", "Gold plan", 5368709120, false, ForSale: true,
        Description: "Shiny", Price: "$2 a month", SortOrder: 1);
}
