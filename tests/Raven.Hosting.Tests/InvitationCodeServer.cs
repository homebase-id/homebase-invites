using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Raven.Hosting.Tests;

/// <summary>
/// The service with a database in a temp file and known keys, deleted on dispose. In process by default;
/// <see cref="StartOnKestrel"/> puts it on a real port for clients outside the process, like raven.sh.
/// </summary>
/// <param name="requestsPerMinutePerIp">High by default, so only the rate-limit tests meet the limit</param>
/// <param name="upgradePage">Whether the upgrade page is on (RavenHosting:PublicDomain set)</param>
/// <param name="seedPlans">Whether the database starts with <see cref="Plans"/>; without, it has no plans</param>
/// <param name="oldSettingNames">Configure it with the setting names from before the rename</param>
public sealed class InvitationCodeServer(int requestsPerMinutePerIp = 100_000, bool upgradePage = true,
    bool seedPlans = true, bool oldSettingNames = false)
    : WebApplicationFactory<Program>
{
    /// <summary>The plans the database starts with: two for sale (registered, then veterans), three not</summary>
    public static readonly PlanRequest[] Plans =
    [
        new("free", "Free", 64424509440, true),
        new("pro-100gb", "Pro", 107374182400, true),
        new("registered", "Registered", 10737418240, true, ForSale: true, SortOrder: 1),
        new("veterans", "Veterans", 1099511627776, true, ForSale: true, Description: "For those who served",
            Price: "$5 a month", SortOrder: 2),
        new("tiny-test", "Tiny test", 1048576, false),
    ];

    /// <summary>The identity hosts the service knows, faked: <see cref="IdentityHosts"/> answers for them</summary>
    public static readonly string[] Hosts = ["provisioning.one.example", "provisioning.two.example"];

    public const string SignupKey = "test-signup-key";
    public const string CreateKey = "test-create-key";
    public const string Root = "/api/v1/invitation-codes";

    /// <summary>The service's own domain: its YouAuth client id, and where the upgrade page is</summary>
    public const string PublicDomain = "invites.test.example";

    private readonly string _path = TempDatabase.NewPath();

    public FakeIdentityHosts IdentityHosts { get; } = new();

    /// <summary>The identities' YouAuth endpoints, faked</summary>
    public FakeYouAuthIdentity YouAuthIdentity { get; } = new();

    public MovableTime Time { get; } = new();

    /// <summary>The database file, for a test that puts in what the API would refuse, e.g. an older code</summary>
    public string DatabasePath => _path;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: appsettings.Development.json would add its own plan service settings (the upgrade
        // page on, a host list), and every setting a test needs is set here
        builder.UseEnvironment("Test");
        Set(builder, "RavenHosting:DatabasePath", _path);
        Set(builder, "RavenHosting:SignupApiKey", SignupKey);
        Set(builder, "RavenHosting:CreateApiKey", CreateKey);
        Set(builder, "RavenHosting:RequestsPerMinutePerIp", requestsPerMinutePerIp.ToString());
        for (var i = 0; i < Hosts.Length; i++)
        {
            Set(builder, $"RavenHosting:IdentityHosts:{i}", Hosts[i]);
        }
        Set(builder, "RavenHosting:AccountServiceApiKey", FakeIdentityHosts.PlanKey);
        if (upgradePage)
        {
            Set(builder, "RavenHosting:PublicDomain", PublicDomain);
            Set(builder, "RavenHosting:DisplayName", "Test Hosting");
        }
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient(PlanService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => IdentityHosts);
            services.AddHttpClient(YouAuthLogin.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => YouAuthIdentity);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    // Under the setting's name from before the rename, when the server is to use those
    private void Set(IWebHostBuilder builder, string key, string value)
    {
        if (oldSettingNames)
        {
            var (name, old) = RenamedSettings.Renamed.Single(r => key == r.Key || key.StartsWith(r.Key + ":"));
            key = old + key[name.Length..];
        }
        builder.UseSetting(key, value);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (seedPlans)
        {
            var plans = host.Services.GetRequiredService<PlanStore>();
            foreach (var plan in Plans)
            {
                plans.CreateAsync(plan).GetAwaiter().GetResult();
            }
        }
        return host;
    }

    /// <summary>
    /// Starts the service on a free local port and returns its base URL
    /// </summary>
    public string StartOnKestrel()
    {
        UseKestrel(0);
        StartServer();
        return ClientOptions.BaseAddress.ToString().TrimEnd('/');
    }

    /// <summary>
    /// A browser on the upgrade page: on the service's public domain over https, keeping cookies, not following
    /// redirects (a test follows them, so it can play the identity in between)
    /// </summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri($"https://{PublicDomain}"),
        AllowAutoRedirect = false,
    });

    /// <summary>
    /// A client that sends <paramref name="key"/> as the API key; none when it is null
    /// </summary>
    public HttpClient ClientWithKey(string? key)
    {
        var client = CreateClient();
        if (key != null)
        {
            client.DefaultRequestHeaders.Add("Odin-Api-Key", key);
        }
        return client;
    }

    public async Task<InvitationCode> CreateCodeAsync(int maxUses = 5, string? owner = "frodo.dotyou.cloud",
        string source = "test", bool enablePublicWebPresence = true, string planId = "free")
    {
        using var client = ClientWithKey(CreateKey);
        var response = await client.PostAsJsonAsync(Root + "/",
            new CreateRequest(owner, maxUses, source, enablePublicWebPresence, planId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvitationCode>())!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        TempDatabase.Delete(_path);
    }
}
