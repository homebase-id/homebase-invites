using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Odin.InvitationCodes.Hosting;

// The invitation-code service (#1908): holds the codes and counts their uses. The identity hosts call it with
// the signup key to check and use a code at signup; admins call it with the create key to create and list.
// It also grants plans: it sets the plan an identity is owed on the identity host that serves it, with the plan
// key (README, "Plan grants"). Owners buy bigger plans on the upgrade page, after signing in with YouAuth (README,
// "Upgrade page").

var builder = WebApplication.CreateBuilder(args);

var databasePath = Required("InvitationCodes:DatabasePath");
var signupApiKey = Required("InvitationCodes:SignupApiKey");
var createApiKey = Required("InvitationCodes:CreateApiKey");

var requestsPerMinute = builder.Configuration.GetValue("InvitationCodes:RequestsPerMinutePerIp", 300);

var store = new InvitationCodeStore(databasePath);

var planHosts = PlanHosts.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(planHosts);
builder.Services.AddSingleton(new PlanGrantStore(databasePath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlanService>();
builder.Services.AddHostedService<PlanGrantWorker>();
builder.Services.AddHttpClient(PlanService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));

// The upgrade page: YouAuth logins in progress and the keys sealing them and its session cookie, in the database
var loginStore = new YouAuthLoginStore(databasePath);
builder.Services.AddSingleton(loginStore);
builder.Services.AddSingleton(UpgradeOptions.FromConfiguration(builder.Configuration));
builder.Services.AddDataProtection().SetApplicationName("homebase-invites");
builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = loginStore);
builder.Services.AddSingleton<YouAuthLogin>();
builder.Services.AddSingleton<UpgradeSession>();
builder.Services.AddHttpClient(YouAuthLogin.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "homebase_upgrade_antiforgery";
    options.Cookie.Path = UpgradePage.Root;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Every real request is well under 1 KB
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = InvitationCodeApi.MaxRequestBodySize);

// Caddy, on a private address, says who the client is; nobody else is believed
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    foreach (var network in new[] { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7" })
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress ?? IPAddress.None,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseRateLimiter();
app.Use(InvitationCodeApi.RequireApiKey);
app.UseAntiforgery();

app.MapGet("/ping", () => "pong");

UpgradePage.Map(app);

var root = app.MapGroup(InvitationCodeApi.Root).AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid);

//
// Signup key: the identity hosts
//

var signup = root.MapGroup("").WithMetadata(new ApiKey(signupApiKey));

signup.MapPost("/validate", async (CodeRequest request) =>
{
    var code = await store.GetAsync(request.Code);
    return new ValidateResponse(code is { UsesLeft: > 0, Disabled: false });
});

signup.MapPost("/use", async (CodeRequest request) =>
{
    var code = await store.TryUseAsync(request.Code);
    return new UseResponse(code != null, code);
});

signup.MapPost("/undo-use", async (CodeRequest request) =>
{
    await store.UndoUseAsync(request.Code);
    return Results.NoContent();
});

//
// Create key: admins
//

var admin = root.MapGroup("").WithMetadata(new ApiKey(createApiKey));

admin.MapPost("/", async (CreateRequest request) =>
    await store.CreateAsync(request.Owner, request.MaxUses, request.Source, request.EnablePublicWebPresence ?? true,
        request.PlanId));

admin.MapGet("/", async (string? owner) => await store.ListAsync(owner));

// Counts codes and signups; sinceDays limits the signups counted to the last that many days
admin.MapGet("/stats", async (int? sinceDays) =>
{
    if (sinceDays is < 1)
    {
        throw new InvalidInvitationCodeRequestException("sinceDays must be 1 or more");
    }

    var since = sinceDays == null ? (long?)null : DateTimeOffset.UtcNow.AddDays(-sinceDays.Value).ToUnixTimeMilliseconds();
    return await store.StatsAsync(since);
});

// Change a code, or disable or enable it. Fields left out stay as they are; "owner": "" removes the owner.
admin.MapPatch("/{code}", async (string code, InvitationCodeChange change) =>
    await store.UpdateAsync(code, change) is { } updated ? Results.Ok(updated) : Results.NotFound());

//
// Plan grants, create key: admins now, the payment flow later (as purchase grants)
//

var grants = app.MapGroup(PlanGrantApi.Root)
    .AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid)
    .WithMetadata(new ApiKey(createApiKey));

// Owe an identity a plan and set it on its host now; the same reference again is the same grant
grants.MapPost("/", async (GrantRequest request, PlanService plans) =>
{
    try
    {
        return Results.Ok(await plans.GrantAsync(request.Identity, request.PlanId, request.Source, request.Reference,
            request.Host));
    }
    catch (PlanGrantConflictException e)
    {
        return Results.Conflict(e.Message);
    }
});

grants.MapGet("/", async (string? identity, string? status, PlanGrantStore grantStore) =>
    await grantStore.ListAsync(identity, status));

// Try a pending or failed grant again now; 404 when there is none, 409 when it is already applied
grants.MapPost("/{id:long}/retry", async (long id, PlanService plans, PlanGrantStore grantStore) =>
    await plans.RetryAsync(id) is { } grant ? Results.Ok(grant)
    : await grantStore.GetAsync(id) is null ? Results.NotFound()
    : Results.Conflict("The grant is already applied"));

app.Run();

string Required(string key)
{
    var value = builder.Configuration[key];
    return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{key} is not configured") : value;
}

public record CodeRequest(string? Code);
// EnablePublicWebPresence is optional and on by default: identity hosts from odin-core #1974 ignore it and take
// public web presence from the plan. It is still stored and sent for hosts from before that.
public record CreateRequest(string? Owner, int MaxUses, string? Source, bool? EnablePublicWebPresence,
    string? PlanId);
public record ValidateResponse(bool Valid);
/// <param name="Source">code, purchase or admin; admin when left out</param>
/// <param name="Reference">The caller's id for it, e.g. a payment id; made up when left out</param>
/// <param name="Host">The provisioning domain of the host the identity is on, if known; a hint, tried first</param>
public record GrantRequest(string? Identity, string? PlanId, string? Source, string? Reference, string? Host);
public record UseResponse(bool Used, InvitationCode? Code);

public partial class Program;

/// <summary>
/// The key an endpoint needs in the <see cref="InvitationCodeApi.ApiKeyHeader"/> header
/// </summary>
internal sealed class ApiKey(string key)
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(key);

    public bool Matches(string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected);
}

internal static class PlanGrantApi
{
    public const string Root = "/api/v1/plans/grants";
}

internal static class InvitationCodeApi
{
    public const string Root = "/api/v1/invitation-codes";
    public const string ApiKeyHeader = "Odin-Api-Key";
    public const long MaxRequestBodySize = 4 * 1024;

    /// <summary>
    /// Refuses a request without its endpoint's <see cref="ApiKey"/> before anything reads the body
    /// </summary>
    public static async Task RequireApiKey(HttpContext context, RequestDelegate next)
    {
        var apiKey = context.GetEndpoint()?.Metadata.GetMetadata<ApiKey>();
        if (apiKey != null && !apiKey.Matches(context.Request.Headers[ApiKeyHeader].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(context);
    }

    public static async ValueTask<object?> BadRequestOnInvalid(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidInvitationCodeRequestException e)
        {
            return Results.BadRequest(e.Message);
        }
    }
}
