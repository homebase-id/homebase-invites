using System.Net;
using System.Net.Http.Json;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// Every endpoint over HTTP, in process: who may call it, what it answers, and its 400s and 404s.
/// </summary>
public class InvitationCodeApiTests
{
    private const string Root = InvitationCodeServer.Root;

    private InvitationCodeServer _server = null!;
    private HttpClient _signup = null!;
    private HttpClient _admin = null!;

    [SetUp]
    public void SetUp()
    {
        _server = new InvitationCodeServer();
        _signup = _server.ClientWithKey(InvitationCodeServer.SignupKey);
        _admin = _server.ClientWithKey(InvitationCodeServer.CreateKey);
    }

    [TearDown]
    public void TearDown()
    {
        _signup.Dispose();
        _admin.Dispose();
        _server.Dispose();
    }

    //
    // Keys
    //

    private static IEnumerable<TestCaseData> EveryEndpoint()
    {
        var code = new { code = "K7QXM2PD9FHW" };
        yield return new TestCaseData(HttpMethod.Post, "/validate", code, InvitationCodeServer.SignupKey).SetName("validate");
        yield return new TestCaseData(HttpMethod.Post, "/use", code, InvitationCodeServer.SignupKey).SetName("use");
        yield return new TestCaseData(HttpMethod.Post, "/undo-use", code, InvitationCodeServer.SignupKey).SetName("undo-use");
        yield return new TestCaseData(HttpMethod.Post, "/", new { maxUses = 1, source = "x" }, InvitationCodeServer.CreateKey).SetName("create");
        yield return new TestCaseData(HttpMethod.Get, "/", null, InvitationCodeServer.CreateKey).SetName("list");
        yield return new TestCaseData(HttpMethod.Get, "/stats", null, InvitationCodeServer.CreateKey).SetName("stats");
        yield return new TestCaseData(HttpMethod.Patch, "/K7QXM2PD9FHW", new { disabled = true }, InvitationCodeServer.CreateKey).SetName("edit");
    }

    [TestCaseSource(nameof(EveryEndpoint))]
    public async Task EveryEndpointRefusesAMissingWrongOrOtherKey(HttpMethod method, string path, object? body, string rightKey)
    {
        var otherKey = rightKey == InvitationCodeServer.SignupKey ? InvitationCodeServer.CreateKey : InvitationCodeServer.SignupKey;

        foreach (var key in new[] { null, "wrong", otherKey })
        {
            using var client = _server.ClientWithKey(key);
            var response = await SendAsync(client, method, path, body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"key: {key ?? "(none)"}");
        }

        using var right = _server.ClientWithKey(rightKey);
        Assert.That((await SendAsync(right, method, path, body)).StatusCode, Is.Not.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task PingNeedsNoKey()
    {
        using var client = _server.ClientWithKey(null);
        Assert.That(await client.GetStringAsync("/ping"), Is.EqualTo("pong"));
    }

    //
    // Create and list
    //

    [Test]
    public async Task ACreatedCodeIsReturnedInDisplayFormWithAllItsUses()
    {
        var created = await _server.CreateCodeAsync(maxUses: 3, owner: "Frodo.DotYou.Cloud", source: " spring ",
            enablePublicWebPresence: false, planId: "pro-100gb");

        Assert.That(created.Code, Does.Match("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$"));
        Assert.That(created, Is.EqualTo(
            new InvitationCode(created.Code, "frodo.dotyou.cloud", "spring", false, "pro-100gb", 3, 3, false)));
    }

    [TestCase("not a domain", 1, null, TestName = "a bad owner")]
    [TestCase("frodo.dotyou.cloud", 0, null, TestName = "no uses")]
    [TestCase("frodo.dotyou.cloud", InvitationCodeStore.MaxUsesLimit + 1, null, TestName = "too many uses")]
    [TestCase("frodo.dotyou.cloud", 1, "pro 100gb", TestName = "a bad plan")]
    public async Task CreateRefuses(string owner, int maxUses, string? planId)
    {
        var response = await _admin.PostAsJsonAsync(Root + "/", new CreateRequest(owner, maxUses, "x", true, planId));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task ListShowsEveryCodeOrOneOwners()
    {
        var frodos = await _server.CreateCodeAsync(owner: "frodo.dotyou.cloud");
        var sams = await _server.CreateCodeAsync(owner: "sam.dotyou.cloud");
        var nobodys = await _server.CreateCodeAsync(owner: null);

        var all = await _admin.GetFromJsonAsync<List<InvitationCode>>(Root + "/");
        Assert.That(all!.Select(c => c.Code), Is.EqualTo(new[] { frodos.Code, sams.Code, nobodys.Code }));

        var onlySams = await _admin.GetFromJsonAsync<List<InvitationCode>>(Root + "/?owner=SAM.dotyou.cloud");
        Assert.That(onlySams, Is.EqualTo(new[] { sams }));
    }

    //
    // Signup: validate, use, undo
    //

    [Test]
    public async Task ACodeIsValidUntilItsUsesAreGone()
    {
        var code = await _server.CreateCodeAsync(maxUses: 1);

        Assert.That(await ValidateAsync(code.Code.ToLowerInvariant().Replace("-", "")), Is.True);
        Assert.That(await UseAsync(code.Code), Is.EqualTo(new UseResponse(true, code with { UsesLeft = 0 })));
        Assert.That(await ValidateAsync(code.Code), Is.False);
        Assert.That(await UseAsync(code.Code), Is.EqualTo(new UseResponse(false, null)));
    }

    [Test]
    public async Task AUseCarriesTheCodesPlan()
    {
        var code = await _server.CreateCodeAsync(planId: "pro-100gb");

        Assert.That((await UseAsync(code.Code)).Code!.PlanId, Is.EqualTo("pro-100gb"));
    }

    [Test]
    public async Task UndoGivesTheLatestUseBack()
    {
        var code = await _server.CreateCodeAsync(maxUses: 1);
        await UseAsync(code.Code);

        var response = await _signup.PostAsJsonAsync(Root + "/undo-use", new CodeRequest(code.Code));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await ValidateAsync(code.Code), Is.True);
    }

    [TestCase(null, TestName = "no code")]
    [TestCase("", TestName = "an empty code")]
    [TestCase("not-a-code", TestName = "something that cannot be a code")]
    [TestCase("K7QX-M2PD-9FHW", TestName = "a code that does not exist")]
    public async Task NothingButARealCodeIsValidOrUsable(string? input)
    {
        Assert.That(await ValidateAsync(input), Is.False);
        Assert.That(await UseAsync(input), Is.EqualTo(new UseResponse(false, null)));
        var undo = await _signup.PostAsJsonAsync(Root + "/undo-use", new CodeRequest(input));
        Assert.That(undo.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    //
    // Edit, disable, enable
    //

    [Test]
    public async Task EditChangesOnlyWhatItIsGiven()
    {
        var code = await _server.CreateCodeAsync(maxUses: 2, owner: "frodo.dotyou.cloud", source: "spring");

        var edited = await PatchAsync(code.Code, new { maxUses = 7, source = "autumn" });
        Assert.That(edited, Is.EqualTo(code with { MaxUses = 7, UsesLeft = 7, Source = "autumn" }));

        var ownerless = await PatchAsync(code.Code, new { owner = "", enablePublicWebPresence = false });
        Assert.That(ownerless, Is.EqualTo(edited with { Owner = null, EnablePublicWebPresence = false }));

        var reowned = await PatchAsync(code.Code, new { owner = "Sam.DotYou.Cloud" });
        Assert.That(reowned!.Owner, Is.EqualTo("sam.dotyou.cloud"));

        var planned = await PatchAsync(code.Code, new { planId = "pro-100gb" });
        Assert.That(planned, Is.EqualTo(reowned with { PlanId = "pro-100gb" }));

        var planless = await PatchAsync(code.Code, new { planId = "" });
        Assert.That(planless, Is.EqualTo(reowned));
    }

    [Test]
    public async Task ADisabledCodeCannotBeUsedUntilItIsEnabled()
    {
        var code = await _server.CreateCodeAsync(maxUses: 2);

        Assert.That((await PatchAsync(code.Code, new { disabled = true }))!.Disabled, Is.True);
        Assert.That(await ValidateAsync(code.Code), Is.False);
        Assert.That((await UseAsync(code.Code)).Used, Is.False);

        Assert.That((await PatchAsync(code.Code, new { disabled = false }))!.Disabled, Is.False);
        Assert.That((await UseAsync(code.Code)).Used, Is.True);
    }

    [Test]
    public async Task LoweringUsesBelowWhatIsTakenLeavesNone()
    {
        var code = await _server.CreateCodeAsync(maxUses: 3);
        await UseAsync(code.Code);
        await UseAsync(code.Code);

        var edited = await PatchAsync(code.Code, new { maxUses = 1 });

        Assert.That(edited!.UsesLeft, Is.EqualTo(0));
        Assert.That(await ValidateAsync(code.Code), Is.False);
    }

    [Test]
    public async Task EditAnswers404ForNoSuchCodeAnd400ForBadInput()
    {
        var code = await _server.CreateCodeAsync();

        Assert.That((await RawPatchAsync("K7QXM2PD9FHW", new { disabled = true })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await RawPatchAsync(code.Code, new { })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await RawPatchAsync(code.Code, new { maxUses = 0 })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await RawPatchAsync(code.Code, new { owner = "not a domain" })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    //
    // Stats
    //

    [Test]
    public async Task StatsCountCodesAndSignups()
    {
        var code = await _server.CreateCodeAsync(maxUses: 2, owner: "frodo.dotyou.cloud", source: "spring",
            planId: "pro-100gb");
        await _server.CreateCodeAsync(maxUses: 3, owner: null, source: "booth");
        await UseAsync(code.Code);

        var stats = await _admin.GetFromJsonAsync<InvitationCodeStats>(Root + "/stats");

        Assert.That(stats!.Since, Is.Null);
        Assert.That(stats.Totals, Is.EqualTo(new InvitationCodeStatsLine(null, 2, 5, 1)));
        Assert.That((stats.NeverUsed, stats.UsedUp, stats.Disabled), Is.EqualTo((1, 0, 0)));
        Assert.That(stats.BySource, Is.EqualTo(new[]
        {
            new InvitationCodeStatsLine("spring", 1, 2, 1),
            new InvitationCodeStatsLine("booth", 1, 3, 0),
        }));
        Assert.That(stats.ByOwner, Is.EqualTo(new[] { new InvitationCodeStatsLine("frodo.dotyou.cloud", 1, 2, 1) }));
        Assert.That(stats.ByPlan, Is.EqualTo(new[]
        {
            new InvitationCodeStatsLine("pro-100gb", 1, 2, 1),
            new InvitationCodeStatsLine(null, 1, 3, 0),
        }));
        Assert.That(stats.ByWeek.Single().Signups, Is.EqualTo(1));
    }

    [Test]
    public async Task SinceDaysCountsFromThatManyDaysAgo()
    {
        var before = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();

        var stats = await _admin.GetFromJsonAsync<InvitationCodeStats>(Root + "/stats?sinceDays=7");

        Assert.That(stats!.Since, Is.InRange(before, DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds()));
    }

    [TestCase("0")]
    [TestCase("-3")]
    public async Task SinceDaysMustBeOneOrMore(string sinceDays)
    {
        var response = await _admin.GetAsync(Root + "/stats?sinceDays=" + sinceDays);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    //

    private async Task<bool> ValidateAsync(string? code)
    {
        var response = await _signup.PostAsJsonAsync(Root + "/validate", new CodeRequest(code));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ValidateResponse>())!.Valid;
    }

    private async Task<UseResponse> UseAsync(string? code)
    {
        var response = await _signup.PostAsJsonAsync(Root + "/use", new CodeRequest(code));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UseResponse>())!;
    }

    private Task<HttpResponseMessage> RawPatchAsync(string code, object change) =>
        _admin.PatchAsJsonAsync(Root + "/" + code, change);

    private async Task<InvitationCode?> PatchAsync(string code, object change)
    {
        var response = await RawPatchAsync(code, change);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InvitationCode>();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, Root + path);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }
        return client.SendAsync(request);
    }
}
