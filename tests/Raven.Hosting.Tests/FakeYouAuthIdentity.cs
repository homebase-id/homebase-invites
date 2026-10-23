using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;

namespace Raven.Hosting.Tests;

/// <summary>
/// An identity's side of YouAuth (odin-core YouAuthUnifiedController and YouAuthUnifiedService), in memory:
/// <see cref="Authorize"/> plays the browser visiting the authorize URL and being sent back ([030] to [080]), and
/// the token endpoint answers the service's exchange ([100] to [140]). The crypto it uses is YouAuthCrypto's own,
/// which <see cref="YouAuthCryptoTests"/> checks against odin-core's outputs.
/// </summary>
public sealed class FakeYouAuthIdentity : HttpMessageHandler
{
    // digest -> exchange secret, for one token exchange each, as odin-core keeps them in its cache
    private readonly ConcurrentDictionary<string, byte[]> _pending = new();

    /// <summary>What the token response says sealed it; odin-core says what the client asked for</summary>
    public string Cipher { get; set; } = YouAuthCrypto.CipherAesGcm;

    public ConcurrentQueue<(string Host, string Path)> TokenRequests { get; } = new();

    /// <summary>
    /// What the identity does with the browser at the authorize URL when its owner is signed in and consents: makes
    /// its own key pair and salt, keeps the token for the exchange secret's digest, and sends the browser back to
    /// the redirect_uri. Returns that callback URL.
    /// </summary>
    public string Authorize(string authorizeUrl)
    {
        var uri = new Uri(authorizeUrl);
        Assert.That(uri.AbsolutePath, Is.EqualTo("/api/owner/v1/youauth/authorize"));
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.That(query["client_type"], Is.EqualTo("domain"));
        Assert.That(new Uri(query["redirect_uri"]!).Host, Is.EqualTo(query["client_id"]),
            "odin-core refuses a domain client whose redirect is not on its own domain");

        using var key = YouAuthCrypto.NewKey();
        var salt = RandomNumberGenerator.GetBytes(16);
        var secret = YouAuthCrypto.ExchangeSecret(key, query["public_key"]!, salt);
        _pending[YouAuthCrypto.SecretDigest(secret)] = secret;

        return $"{query["redirect_uri"]}?identity={Uri.EscapeDataString(uri.Host)}" +
               $"&public_key={Uri.EscapeDataString(YouAuthCrypto.PublicKeyJwkBase64Url(key))}" +
               $"&salt={Uri.EscapeDataString(Convert.ToBase64String(salt))}" +
               $"&state={Uri.EscapeDataString(query["state"]!)}";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TokenRequests.Enqueue((request.RequestUri!.Host, request.RequestUri.AbsolutePath));
        if (request.Method != HttpMethod.Post || request.RequestUri.AbsolutePath != "/api/owner/v1/youauth/token")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var digest = body.RootElement.GetProperty("secret_digest").GetString()!;
        if (!_pending.TryRemove(digest, out var secret))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var (sharedSecretIv, sharedSecretCipher) = YouAuthCrypto.SealGcm(secret, RandomNumberGenerator.GetBytes(16));
        var (catIv, catCipher) = YouAuthCrypto.SealGcm(secret, RandomNumberGenerator.GetBytes(49));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                base64SharedSecretCipher = Convert.ToBase64String(sharedSecretCipher),
                base64SharedSecretIv = Convert.ToBase64String(sharedSecretIv),
                base64ClientAuthTokenCipher = Convert.ToBase64String(catCipher),
                base64ClientAuthTokenIv = Convert.ToBase64String(catIv),
                cipher = Cipher,
            })
        };
    }
}

/// <summary>
/// The real clock, moved forward by <see cref="Offset"/>
/// </summary>
public sealed class MovableTime : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
