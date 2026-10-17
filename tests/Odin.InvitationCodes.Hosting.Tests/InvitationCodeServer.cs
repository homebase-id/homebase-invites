using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// The service with a database in a temp file and known keys, deleted on dispose. In process by default;
/// <see cref="StartOnKestrel"/> puts it on a real port for clients outside the process, like invites.sh.
/// </summary>
public sealed class InvitationCodeServer : WebApplicationFactory<Program>
{
    public const string SignupKey = "test-signup-key";
    public const string CreateKey = "test-create-key";
    public const string Root = "/api/v1/invitation-codes";

    private readonly string _path = TempDatabase.NewPath();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("InvitationCodes:DatabasePath", _path);
        builder.UseSetting("InvitationCodes:SignupApiKey", SignupKey);
        builder.UseSetting("InvitationCodes:CreateApiKey", CreateKey);
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
        string source = "test", bool enablePublicWebPresence = true)
    {
        using var client = ClientWithKey(CreateKey);
        var response = await client.PostAsJsonAsync(Root + "/",
            new CreateRequest(owner, maxUses, source, enablePublicWebPresence));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvitationCode>())!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        TempDatabase.Delete(_path);
    }
}
