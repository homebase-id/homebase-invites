using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// invites.sh, run with bash against the service on a real port. Needs bash, curl and jq; without jq the
/// tests are ignored locally, and fail under CI so a runner without it cannot pass them silently.
/// </summary>
[NonParallelizable]
[UnsupportedOSPlatform("windows")] // bash, and Unix file modes
public class InvitesCliTests
{
    private InvitationCodeServer _server = null!;
    private string _url = null!;
    private string _home = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!OnPath("jq"))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
            {
                Assert.Fail("jq is not installed; invites.sh needs it");
            }
            Assert.Ignore("jq is not installed");
        }

        _server = new InvitationCodeServer();
        _url = _server.StartOnKestrel();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _server?.Dispose();

    [SetUp]
    public void SetUp()
    {
        // A home of its own, so a real ~/.homebase-invites is never read
        _home = Directory.CreateTempSubdirectory("invites-cli-").FullName;
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_home, recursive: true);

    //

    [Test]
    public async Task HelpExplainsEveryCommand()
    {
        var result = await RunAsync("help");

        Assert.That(result.ExitCode, Is.EqualTo(0));
        foreach (var command in new[] { "setup", "ping", "create", "list", "stats", "edit", "disable", "enable", "validate", "use", "undo" })
        {
            Assert.That(result.Output, Does.Contain($"  {command}"), command);
        }
    }

    [Test]
    public async Task AnUnknownCommandPrintsHelpAndFails()
    {
        var result = await RunAsync("frobnicate");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output, Does.Contain("CREATE AND LIST CODES"));
    }

    [Test]
    public async Task AMissingKeySaysToRunSetup()
    {
        var result = await RunAsync(new Options(WithKeys: false), "list");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain("HOMEBASE_INVITES_CREATE_KEY is not set; run ./invites.sh setup"));
    }

    [Test]
    public async Task SetupSavesTheKeysForOnlyTheUserToRead()
    {
        var result = await RunAsync(new Options(WithKeys: false, Stdin: "the-create-key\nthe-signup-key\n"), "setup");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        var config = Path.Combine(_home, ".homebase-invites");
        Assert.That(await File.ReadAllTextAsync(config),
            Is.EqualTo("HOMEBASE_INVITES_CREATE_KEY=the-create-key\nHOMEBASE_INVITES_SIGNUP_KEY=the-signup-key\n"));
        Assert.That(File.GetUnixFileMode(config), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [Test]
    public async Task TheSavedKeysAreUsed()
    {
        await RunAsync(new Options(WithKeys: false, Stdin: $"{InvitationCodeServer.CreateKey}\n{InvitationCodeServer.SignupKey}\n"), "setup");

        var result = await RunAsync(new Options(WithKeys: false), "list");

        Assert.That(result.Status, Is.EqualTo(200), result.Output);
    }

    [Test]
    public async Task Ping()
    {
        var result = await RunAsync("ping");

        Assert.That(result.Output, Is.EqualTo("pong\n[200]\n"));
    }

    //

    [Test]
    public async Task CreateMakesACodeWithWhatItIsGiven()
    {
        var result = await RunAsync("create", "--uses", "4", "--source", "spring \"quoted\" campaign", "--owner",
            "frodo.dotyou.cloud", "--no-public-page");

        Assert.That(result.Status, Is.EqualTo(200), result.Output);
        var code = result.Json<InvitationCode>();
        Assert.That(code, Is.EqualTo(new InvitationCode(code.Code, "frodo.dotyou.cloud", "spring \"quoted\" campaign", false, 4, 4, false)));
    }

    [Test]
    public async Task CreateWithoutAnOwnerGivesAPublicPageByDefault()
    {
        var code = (await RunAsync("create", "--uses", "1", "--source", "booth")).Json<InvitationCode>();

        Assert.That(code.Owner, Is.Null);
        Assert.That(code.EnablePublicWebPresence, Is.True);
    }

    [TestCase(new[] { "--source", "x" }, "usage: ./invites.sh create", TestName = "create without --uses")]
    [TestCase(new[] { "--uses", "1" }, "usage: ./invites.sh create", TestName = "create without --source")]
    [TestCase(new[] { "--uses", "five", "--source", "x" }, "--uses must be a number", TestName = "create with a non-number")]
    [TestCase(new[] { "--uses", "1", "--source", "x", "--bogus" }, "unknown option: --bogus", TestName = "create with an unknown option")]
    public async Task CreateRefusesBadArguments(string[] arguments, string message)
    {
        var result = await RunAsync(["create", .. arguments]);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
    }

    [Test]
    public async Task TheServicesRefusalIsShownWithItsStatus()
    {
        var result = await RunAsync("create", "--uses", "1", "--source", "x", "--owner", "not-a-domain");

        Assert.That(result.Status, Is.EqualTo(400));
        Assert.That(result.Output, Does.Contain("Invalid owner"));
    }

    [Test]
    public async Task ListShowsCodesAllOrOneOwners()
    {
        var mine = await CreateAsync(owner: "list-owner.dotyou.cloud");

        var all = await RunAsync("list");
        var owners = await RunAsync("list", "--owner", "LIST-owner.dotyou.cloud");

        Assert.That(all.Json<List<InvitationCode>>(), Does.Contain(mine));
        Assert.That(owners.Json<List<InvitationCode>>(), Is.EqualTo(new[] { mine }));
        Assert.That((await RunAsync("list", "--owner")).ExitCode, Is.EqualTo(1));
    }

    [Test]
    public async Task EditChangesWhatItIsGiven()
    {
        var code = await CreateAsync(owner: "frodo.dotyou.cloud");

        var edited = (await RunAsync("edit", code.Code, "--uses", "9", "--source", "autumn", "--no-owner", "--no-public-page"))
            .Json<InvitationCode>();
        Assert.That(edited, Is.EqualTo(code with { MaxUses = 9, UsesLeft = 9, Source = "autumn", Owner = null, EnablePublicWebPresence = false }));

        var reowned = (await RunAsync("edit", code.Code.ToLowerInvariant(), "--owner", "sam.dotyou.cloud", "--public-page"))
            .Json<InvitationCode>();
        Assert.That(reowned, Is.EqualTo(edited with { Owner = "sam.dotyou.cloud", EnablePublicWebPresence = true }));
    }

    [Test]
    public async Task EditRefusesBadArguments()
    {
        var code = await CreateAsync();

        Assert.That((await RunAsync("edit", code.Code)).Error, Does.Contain("nothing to change"));
        Assert.That((await RunAsync("edit", code.Code, "--uses", "x")).Error, Does.Contain("--uses must be a number"));
        Assert.That((await RunAsync("edit", "K7QX/../stats", "--uses", "1")).Error, Does.Contain("usage: ./invites.sh edit"));
        Assert.That((await RunAsync("edit", "K7QX-M2PD-9FHW", "--uses", "1")).Status, Is.EqualTo(404));
    }

    [Test]
    public async Task DisableAndEnable()
    {
        var code = await CreateAsync();

        Assert.That((await RunAsync("disable", code.Code)).Json<InvitationCode>().Disabled, Is.True);
        Assert.That((await RunAsync("validate", code.Code)).Output, Does.StartWith("{\"valid\":false}"));

        Assert.That((await RunAsync("enable", code.Code)).Json<InvitationCode>().Disabled, Is.False);
        Assert.That((await RunAsync("validate", code.Code)).Output, Does.StartWith("{\"valid\":true}"));
    }

    [Test]
    public async Task ValidateUseAndUndo()
    {
        var code = await CreateAsync(maxUses: 1);

        Assert.That((await RunAsync("use", code.Code)).Json<UseResponse>(), Is.EqualTo(new UseResponse(true, code with { UsesLeft = 0 })));
        Assert.That((await RunAsync("validate", code.Code)).Json<ValidateResponse>().Valid, Is.False);

        Assert.That((await RunAsync("undo", code.Code)).Status, Is.EqualTo(204));
        Assert.That((await RunAsync("validate", code.Code)).Json<ValidateResponse>().Valid, Is.True);
    }

    //

    [Test]
    public async Task StatsPrintsAReport()
    {
        var code = await CreateAsync(maxUses: 3, owner: "stats-owner.dotyou.cloud", source: "stats-source");
        await RunAsync("use", code.Code);

        var result = await RunAsync("stats");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        Assert.That(result.Output, Does.StartWith("Signups of all time\n"));
        Assert.That(result.Output, Does.Match(@"\n  stats-source +1 +3 +1\n"));
        Assert.That(result.Output, Does.Match(@"\n  stats-owner\.dotyou\.cloud +1 +3 +1\n"));
        Assert.That(result.Output, Does.Match(@"\nsignups per week, from Monday\n  \d{4}-\d{2}-\d{2} +\d+  #+\n"));
        Assert.That(result.Output, Does.Match(@"\ncodes: \d+ never used, \d+ used up, \d+ disabled\n$"));
    }

    [Test]
    public async Task StatsSinceSaysFromWhen()
    {
        var result = await RunAsync("stats", "--since", "30");

        var expected = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        Assert.That(result.Output, Does.StartWith($"Signups since {expected}\n").Or.StartWith(
            $"Signups since {DateTime.UtcNow.AddDays(-30).AddMinutes(-1):yyyy-MM-dd}\n"));
    }

    [Test]
    public async Task StatsJsonIsTheServicesAnswer()
    {
        var result = await RunAsync("stats", "--json", "--since", "7");

        Assert.That(result.Status, Is.EqualTo(200));
        Assert.That(result.Json<InvitationCodeStats>().Since, Is.Not.Null);
    }

    [TestCase(new[] { "--since", "0" }, "--since must be a number of days", TestName = "stats since zero days")]
    [TestCase(new[] { "--since", "week" }, "--since must be a number of days", TestName = "stats since a non-number")]
    [TestCase(new[] { "--bogus" }, "unknown option: --bogus", TestName = "stats with an unknown option")]
    public async Task StatsRefusesBadArguments(string[] arguments, string message)
    {
        var result = await RunAsync(["stats", .. arguments]);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
    }

    [Test]
    public async Task StatsWithAWrongKeyShowsTheRefusal()
    {
        var result = await RunAsync(new Options(CreateKey: "wrong"), "stats");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output, Does.EndWith("[401]\n"));
    }

    //

    private async Task<InvitationCode> CreateAsync(int maxUses = 2, string? owner = null, string source = "cli-test")
    {
        List<string> arguments = ["create", "--uses", maxUses.ToString(), "--source", source];
        if (owner != null)
        {
            arguments.AddRange(["--owner", owner]);
        }
        var result = await RunAsync(arguments.ToArray());
        Assert.That(result.Status, Is.EqualTo(200), result.Output + result.Error);
        return result.Json<InvitationCode>();
    }

    /// <param name="WithKeys">Pass the right keys in the environment</param>
    /// <param name="Stdin">What to type</param>
    /// <param name="CreateKey">A create key of its own, instead of the right one</param>
    private record Options(bool WithKeys = true, string? Stdin = null, string? CreateKey = null);

    private Task<CliResult> RunAsync(params string[] arguments) => RunAsync(new Options(), arguments);

    private async Task<CliResult> RunAsync(Options options, params string[] arguments)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Script);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["HOME"] = _home;
        start.Environment["HOMEBASE_INVITES_URL"] = _url;
        start.Environment.Remove("HOMEBASE_INVITES_CREATE_KEY");
        start.Environment.Remove("HOMEBASE_INVITES_SIGNUP_KEY");
        if (options.WithKeys)
        {
            start.Environment["HOMEBASE_INVITES_CREATE_KEY"] = InvitationCodeServer.CreateKey;
            start.Environment["HOMEBASE_INVITES_SIGNUP_KEY"] = InvitationCodeServer.SignupKey;
        }
        if (options.CreateKey != null)
        {
            start.Environment["HOMEBASE_INVITES_CREATE_KEY"] = options.CreateKey;
        }

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(options.Stdin ?? "");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static readonly string Script = FindScript();

    private static string FindScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var script = Path.Combine(directory.FullName, "invites.sh");
            if (File.Exists(script))
            {
                return script;
            }
        }
        throw new FileNotFoundException("invites.sh not found above " + AppContext.BaseDirectory);
    }

    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(directory => File.Exists(Path.Combine(directory, program)));

    /// <summary>
    /// What a run printed. Commands that call the service print its answer, then the HTTP status as [200].
    /// </summary>
    private record CliResult(int ExitCode, string Output, string Error)
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public int? Status =>
            Output.TrimEnd().Split('\n').Last() is var last && last.StartsWith('[') && last.EndsWith(']')
                ? int.Parse(last[1..^1])
                : null;

        public T Json<T>()
        {
            Assert.That(Status, Is.EqualTo(200), Output + Error);
            return JsonSerializer.Deserialize<T>(Output.TrimEnd().Split('\n')[0], Web)!;
        }
    }
}
