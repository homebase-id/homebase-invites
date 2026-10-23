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
            5368709120, false, false, 0, true, [new(1, 200), new(12, 2000)], 1, 0, 0)), "prices shortest term first");
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
        Assert.That((plan.ForSale, plan.Prices.Count, plan.Description, plan.SortOrder), Is.EqualTo((false, 0, "", 0)));
    }

    [Test]
    public async Task AnEditChangesOnlyWhatItGives()
    {
        var before = (await _admin.GetFromJsonAsync<Plan>(Root + "/veterans"))!;

        var response = await _admin.PatchAsJsonAsync(Root + "/VETERANS", new { forSale = false, sortOrder = 5 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = (await response.Content.ReadFromJsonAsync<Plan>())!;
        Assert.That(after with { UpdatedAt = 0 }, Is.EqualTo(before with { ForSale = false, SortOrder = 5, UpdatedAt = 0 }));
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
        Description: "Shiny", Prices: [new(12, 2000), new(1, 200)], SortOrder: 1);

    //
    // Prices
    //

    [Test]
    public async Task APriceIsAddedChangedAndRemoved()
    {
        var added = await _admin.PutAsJsonAsync(Root + "/Veterans/prices/36", new { amountCents = 12000 });
        Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await added.Content.ReadFromJsonAsync<Plan>())!.Prices.Select(p => p.Months),
            Is.EqualTo(new[] { 1, 12, 24, 36 }));

        var changed = (await (await _admin.PutAsJsonAsync(Root + "/veterans/prices/12", new { amountCents = 4500 }))
            .Content.ReadFromJsonAsync<Plan>())!;
        Assert.That(changed.PriceFor(12), Is.EqualTo(new PlanPrice(12, 4500)));

        var removed = await _admin.DeleteAsync(Root + "/veterans/prices/36");
        Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await removed.Content.ReadFromJsonAsync<Plan>())!.PriceFor(36), Is.Null);
        Assert.That((await _admin.DeleteAsync(Root + "/veterans/prices/36")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _admin.PutAsJsonAsync(Root + "/silver/prices/1", new { amountCents = 1 })).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task APriceSaysWhatItIsInWords()
    {
        var plan = (await _admin.GetFromJsonAsync<Plan>(Root + "/veterans"))!;
        var json = await _admin.GetStringAsync(Root + "/veterans");

        Assert.That(plan.Prices.Select(p => p.Text), Is.EqualTo(new[] { "$5 / month", "$50 / year", "$90 / 2 years" }));
        Assert.That(json, Does.Contain("\"text\":\"$50 / year\""));
    }

    [TestCase(0, 100, "between 1 and 120", TestName = "a price for 0 months")]
    [TestCase(121, 100, "between 1 and 120", TestName = "a price for too many months")]
    [TestCase(12, -1, "cannot be negative", TestName = "a negative price")]
    public async Task ABadPriceIsA400(int months, long amountCents, string message)
    {
        var response = await _admin.PutAsJsonAsync($"{Root}/veterans/prices/{months}", new { amountCents });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(message));
    }

    [Test]
    public async Task APlanForSaleNeedsAPrice()
    {
        var created = await _admin.PostAsJsonAsync(Root + "/", new PlanRequest("bare", "Bare", 1, true, ForSale: true));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await created.Content.ReadAsStringAsync(), Does.Contain("needs at least one price"));

        var offSale = await _admin.PatchAsJsonAsync(Root + "/tiny-test", new { forSale = true });
        Assert.That(offSale.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "tiny-test has no price");

        var lastPrice = await _admin.DeleteAsync(Root + "/registered/prices/12");
        Assert.That(lastPrice.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "registered's only price");
        Assert.That(await lastPrice.Content.ReadAsStringAsync(), Does.Contain("mark it not for sale first"));
    }

    [Test]
    public async Task TwoPricesForOneTermAreRefused()
    {
        var response = await _admin.PostAsJsonAsync(Root + "/",
            new PlanRequest("twice", "Twice", 1, true, Prices: [new(12, 100), new(12, 200)]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task DeletingAPlanDeletesItsPrices()
    {
        await _admin.PutAsJsonAsync(Root + "/tiny-test/prices/1", new { amountCents = 100 });
        Assert.That((await _admin.DeleteAsync(Root + "/tiny-test")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var again = await _admin.PostAsJsonAsync(Root + "/", new PlanRequest("tiny-test", "Tiny test", 1, false));

        Assert.That((await again.Content.ReadFromJsonAsync<Plan>())!.Prices, Is.Empty, "no price left behind");
    }
}
