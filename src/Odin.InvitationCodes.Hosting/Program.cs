using System.Security.Cryptography;
using System.Text;
using Odin.InvitationCodes.Hosting;

// The invitation-code service (#1908): holds the codes and counts their uses. The identity hosts call it with
// the signup key to check and use a code at signup; admins call it with the create key to create and list.

var builder = WebApplication.CreateBuilder(args);

var databasePath = Required("InvitationCodes:DatabasePath");
var signupApiKey = Required("InvitationCodes:SignupApiKey");
var createApiKey = Required("InvitationCodes:CreateApiKey");

builder.Services.AddSingleton(new InvitationCodeStore(databasePath));

var app = builder.Build();

app.MapGet("/ping", () => "pong");

var store = app.Services.GetRequiredService<InvitationCodeStore>();
var root = app.MapGroup(InvitationCodeApi.Root).AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid);

//
// Signup key: the identity hosts
//

var signup = root.MapGroup("").AddEndpointFilter(InvitationCodeApi.RequireApiKey(signupApiKey));

signup.MapPost("/validate", async (CodeRequest request) =>
{
    var code = await store.GetAsync(request.Code);
    return new ValidateResponse(code is { UsesLeft: > 0 });
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

var admin = root.MapGroup("").AddEndpointFilter(InvitationCodeApi.RequireApiKey(createApiKey));

admin.MapPost("/", async (CreateRequest request) =>
    await store.CreateAsync(request.Owner, request.MaxUses, request.Source, request.EnablePublicWebPresence));

admin.MapGet("/", async (string? owner) => await store.ListAsync(owner));

app.Run();

string Required(string key)
{
    var value = builder.Configuration[key];
    return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{key} is not configured") : value;
}

public record CodeRequest(string? Code);
public record CreateRequest(string? Owner, int MaxUses, string? Source, bool EnablePublicWebPresence);
public record ValidateResponse(bool Valid);
public record UseResponse(bool Used, InvitationCode? Code);

public partial class Program;

internal static class InvitationCodeApi
{
    public const string Root = "/api/v1/invitation-codes";
    public const string ApiKeyHeader = "Odin-Api-Key";

    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireApiKey(
        string apiKey)
    {
        var expected = Encoding.UTF8.GetBytes(apiKey);
        return async (context, next) =>
        {
            var presented = Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers[ApiKeyHeader].ToString());
            return CryptographicOperations.FixedTimeEquals(presented, expected)
                ? await next(context)
                : Results.Unauthorized();
        };
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
