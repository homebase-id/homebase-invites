using System.Net;
using System.Text;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// What keeps junk traffic cheap: the key is checked before the body is read, bodies are capped, and each
/// client gets a number of requests a minute.
/// </summary>
public class InvitationCodeHardeningTests
{
    private const string Root = InvitationCodeServer.Root;

    [Test]
    public async Task AMissingKeyIsRefusedBeforeTheBodyIsRead()
    {
        using var server = new InvitationCodeServer();
        using var noKey = server.ClientWithKey(null);
        using var signup = server.ClientWithKey(InvitationCodeServer.SignupKey);

        Assert.That((await noKey.PostAsync(Root + "/validate", Json("not json"))).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await signup.PostAsync(Root + "/validate", Json("not json"))).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task ABodyOverTheLimitIsRefused()
    {
        using var server = new InvitationCodeServer();
        using var client = KestrelClient(server, InvitationCodeServer.SignupKey);
        var code = new string('A', 4 * 1024);

        var response = await client.PostAsync(Root + "/validate", Json($$"""{"code":"{{code}}"}"""));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task AClientOverItsRequestsAMinuteIsRefused()
    {
        using var server = new InvitationCodeServer(requestsPerMinutePerIp: 3);
        using var client = server.ClientWithKey(null);

        for (var i = 0; i < 3; i++)
        {
            Assert.That((await client.GetAsync("/ping")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        Assert.That((await client.GetAsync("/ping")).StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    [Test]
    public async Task BehindTheProxyEachClientHasItsOwnLimit()
    {
        using var server = new InvitationCodeServer(requestsPerMinutePerIp: 1);
        using var client = KestrelClient(server, key: null);

        Assert.That((await PingAs(client, "203.0.113.1")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await PingAs(client, "203.0.113.1")).StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That((await PingAs(client, "203.0.113.2")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private static Task<HttpResponseMessage> PingAs(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    // A client over a real socket, which the body limit and the proxy's address need
    private static HttpClient KestrelClient(InvitationCodeServer server, string? key)
    {
        var client = new HttpClient { BaseAddress = new Uri(server.StartOnKestrel()) };
        if (key != null)
        {
            client.DefaultRequestHeaders.Add("Odin-Api-Key", key);
        }
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
