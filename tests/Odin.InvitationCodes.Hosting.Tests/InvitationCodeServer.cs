using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// The service with a database in a temp file and known keys, deleted on dispose. In process by default;
/// <see cref="StartOnKestrel"/> puts it on a real port for clients outside the process, like invites.sh.
/// </summary>
/// <param name="requestsPerMinutePerIp">High by default, so only the rate-limit tests meet the limit</param>
/// <param name="upgradePage">Whether the upgrade page is on (PlanService:PublicDomain set)</param>
public sealed class InvitationCodeServer(int requestsPerMinutePerIp = 100_000, bool upgradePage = true)
    : WebApplicationFactory<Program>
{
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("InvitationCodes:DatabasePath", _path);
        builder.UseSetting("InvitationCodes:SignupApiKey", SignupKey);
        builder.UseSetting("InvitationCodes:CreateApiKey", CreateKey);
        builder.UseSetting("InvitationCodes:RequestsPerMinutePerIp", requestsPerMinutePerIp.ToString());
        for (var i = 0; i < Hosts.Length; i++)
        {
            builder.UseSetting($"PlanService:Hosts:{i}", Hosts[i]);
        }
        builder.UseSetting("PlanService:ApiKey", FakeIdentityHosts.PlanKey);
        if (upgradePage)
        {
            builder.UseSetting("PlanService:PublicDomain", PublicDomain);
            builder.UseSetting("PlanService:DisplayName", "Test Hosting");
            builder.UseSetting("PlanService:PlansForSale:0:Id", "veterans");
            builder.UseSetting("PlanService:PlansForSale:0:Name", "Veterans");
            builder.UseSetting("PlanService:PlansForSale:0:Description", "1 TB and a public home page");
            builder.UseSetting("PlanService:PlansForSale:0:Price", "$5 a month");
            builder.UseSetting("PlanService:PlansForSale:1:Id", "registered");
            builder.UseSetting("PlanService:PlansForSale:1:Name", "Registered");
        }
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient(PlanService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => IdentityHosts);
            services.AddHttpClient(YouAuthLogin.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => YouAuthIdentity);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
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
