using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Odin.InvitationCodes.Hosting;

// The invitation-code service (#1908): holds the codes and counts their uses. The identity hosts call it with
// the signup key to check and use a code at signup; admins call it with the create key to create and list.

var builder = WebApplication.CreateBuilder(args);

var databasePath = Required("InvitationCodes:DatabasePath");
var signupApiKey = Required("InvitationCodes:SignupApiKey");
var createApiKey = Required("InvitationCodes:CreateApiKey");

var requestsPerMinute = builder.Configuration.GetValue("InvitationCodes:RequestsPerMinutePerIp", 300);

var store = new InvitationCodeStore(databasePath);

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

app.MapGet("/ping", () => "pong");

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
