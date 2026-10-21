using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;

namespace Odin.InvitationCodes.Hosting;

/// <summary>
/// A plan an owner can buy on the upgrade page. Only <c>Id</c> is needed (a plan Id on the identity hosts); the
/// name shown defaults to it. <c>Price</c> is accepted but not shown: there is no payment yet.
/// </summary>
public record PlanForSale
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Price { get; init; } = "";
}

/// <summary>
/// The upgrade page's settings. Off (its routes answer 404) while <c>PlanService:PublicDomain</c> is empty.
/// </summary>
public class UpgradeOptions
{
    public const string CallbackPath = "/upgrade/youauth-callback";

    public UpgradeOptions(string? publicDomain, string? displayName, IEnumerable<PlanForSale> plansForSale)
    {
        PlansForSale = plansForSale.Select(p => p with
        {
            Id = p.Id?.Trim() ?? "",
            Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id?.Trim() ?? "" : p.Name.Trim(),
            Description = p.Description?.Trim() ?? "",
            Price = p.Price?.Trim() ?? "",
        }).ToList();
        if (PlansForSale.Any(p => p.Id == ""))
        {
            throw new InvalidOperationException("PlanService:PlansForSale: every plan needs an Id");
        }
        if (PlansForSale.Select(p => p.Id.ToLowerInvariant()).Distinct().Count() != PlansForSale.Count)
        {
            throw new InvalidOperationException("PlanService:PlansForSale has a plan Id twice");
        }

        if (string.IsNullOrWhiteSpace(publicDomain))
        {
            if (PlansForSale.Count > 0)
            {
                throw new InvalidOperationException("PlanService:PublicDomain is not configured, but PlanService:PlansForSale is");
            }
            PublicDomain = "";
        }
        else
        {
            PublicDomain = DomainNames.Normalize(publicDomain) ??
                           throw new InvalidOperationException($"PlanService:PublicDomain: '{publicDomain}' is not a domain name");
        }

        DisplayName = string.IsNullOrWhiteSpace(displayName) ? PublicDomain : displayName.Trim();
    }

    /// <summary>The service's own domain, e.g. invites.ravenhosting.cloud: the YouAuth client id; empty when off</summary>
    public string PublicDomain { get; }

    /// <summary>What the service calls itself on the identity's consent screen (.well-known/youauth-client.json)</summary>
    public string DisplayName { get; }

    public IReadOnlyList<PlanForSale> PlansForSale { get; }

    public bool Enabled => PublicDomain != "";

    public string CallbackUrl => $"https://{PublicDomain}{CallbackPath}";

    public PlanForSale? ForSale(string? planId) =>
        PlansForSale.FirstOrDefault(p => string.Equals(p.Id, planId?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static UpgradeOptions FromConfiguration(IConfiguration configuration) =>
        new(configuration["PlanService:PublicDomain"],
            configuration["PlanService:DisplayName"],
            configuration.GetSection("PlanService:PlansForSale").Get<PlanForSale[]>() ?? []);
}

/// <summary>
/// The outcome of a YouAuth login: the identity whose owner signed in, or why nobody did
/// </summary>
public record UpgradeLoginResult(string? Identity, string? HostHint, string? Error)
{
    public static UpgradeLoginResult Failed(string error) => new(null, null, error);
}

/// <summary>
/// Signs an identity's owner in with YouAuth, as a domain client, entirely on the server
/// (odin-core docs/youauth-unified-authorization.md): <see cref="BeginAsync"/> is steps [010] and [030],
/// <see cref="CompleteAsync"/> steps [090] to [150]. The identity is the one this service stored with the state
/// when it began, never what the callback says: a completed token exchange with that identity is the proof its
/// owner is here.
/// </summary>
public class YouAuthLogin(
    YouAuthLoginStore store,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClientFactory,
    UpgradeOptions options,
    TimeProvider time,
    ILogger<YouAuthLogin> logger)
{
    public const string HttpClientName = "youauth-identities";

    // Long enough to sign in and consent at the identity, short enough that a stale link is useless
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _keyProtector = dataProtection.CreateProtector("youauth-login-private-key");

    /// <summary>
    /// Makes a key pair and a single-use state for <paramref name="identity"/>, and returns the identity's authorize
    /// URL to send the browser to
    /// </summary>
    public async Task<string> BeginAsync(string identity, string? hostHint)
    {
        using var key = YouAuthCrypto.NewKey();
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var protectedKey = Convert.ToBase64String(_keyProtector.Protect(key.ExportECPrivateKey()));
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        await store.SaveAsync(state, identity, hostHint, protectedKey, now + (long)StateLifetime.TotalMilliseconds, now);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.PublicDomain,
            ["client_type"] = "domain",
            ["client_info"] = "",
            ["redirect_uri"] = options.CallbackUrl,
            ["permission_request"] = "",
            ["public_key"] = YouAuthCrypto.PublicKeyJwkBase64Url(key),
            ["state"] = state,
            ["cipher"] = YouAuthCrypto.CipherAesGcm,
        };
        return $"https://{identity}/api/owner/v1/youauth/authorize?" +
               string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
    }

    /// <summary>
    /// Finishes the login the identity's callback reports: takes the state (once, if it has not expired), derives
    /// the exchange secret, trades its digest for the token and opens it.
    /// </summary>
    public async Task<UpgradeLoginResult> CompleteAsync(string? state, string? publicKey, string? salt, string? error,
        string? errorDescription)
    {
        var login = string.IsNullOrEmpty(state)
            ? null
            : await store.TakeAsync(state, time.GetUtcNow().ToUnixTimeMilliseconds());

        if (!string.IsNullOrEmpty(error))
        {
            logger.LogInformation("YouAuth for {identity} came back with {error}: {description}",
                login?.Identity ?? "(unknown)", error, errorDescription);
            return UpgradeLoginResult.Failed(error == "cancelled-by-user"
                ? "You cancelled signing in."
                : "Your identity did not let you sign in" + (string.IsNullOrWhiteSpace(errorDescription) ? "." : $": {errorDescription}"));
        }

        if (login == null)
        {
            return UpgradeLoginResult.Failed("This sign-in link has expired or was already used. Please start again.");
        }

        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(salt))
        {
            return UpgradeLoginResult.Failed("Your identity's answer was incomplete. Please start again.");
        }

        try
        {
            using var key = YouAuthCrypto.NewKey();
            key.ImportECPrivateKey(_keyProtector.Unprotect(Convert.FromBase64String(login.ProtectedPrivateKey)), out _);
            var exchangeSecret = YouAuthCrypto.ExchangeSecret(key, publicKey, Convert.FromBase64String(salt));

            using var response = await httpClientFactory.CreateClient(HttpClientName).PostAsJsonAsync(
                $"https://{login.Identity}/api/owner/v1/youauth/token",
                new Dictionary<string, string> { ["secret_digest"] = YouAuthCrypto.SecretDigest(exchangeSecret) });
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("YouAuth token exchange with {identity} answered {status}", login.Identity, (int)response.StatusCode);
                return UpgradeLoginResult.Failed("Your identity did not confirm the sign-in. Please start again.");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonSerializerOptions.Web)
                        ?? throw new CryptographicException("No token");

            // Opening both fields authenticates them: GCM fails on a wrong key or any changed byte
            YouAuthCrypto.OpenGcm(token.Cipher, exchangeSecret,
                Convert.FromBase64String(token.Base64SharedSecretIv ?? ""),
                Convert.FromBase64String(token.Base64SharedSecretCipher ?? ""));
            YouAuthCrypto.OpenGcm(token.Cipher, exchangeSecret,
                Convert.FromBase64String(token.Base64ClientAuthTokenIv ?? ""),
                Convert.FromBase64String(token.Base64ClientAuthTokenCipher ?? ""));

            logger.LogInformation("{identity} signed in to the upgrade page", login.Identity);
            return new UpgradeLoginResult(login.Identity, login.HostHint, null);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException or HttpRequestException
                                      or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(e, "YouAuth sign-in for {identity} failed", login.Identity);
            return UpgradeLoginResult.Failed("We could not confirm the sign-in with your identity. Please start again.");
        }
    }

    private record TokenResponse(
        string? Base64SharedSecretCipher,
        string? Base64SharedSecretIv,
        string? Base64ClientAuthTokenCipher,
        string? Base64ClientAuthTokenIv,
        string? Cipher);
}

/// <summary>A YouAuth login in progress, under its state</summary>
public record YouAuthLoginState(string Identity, string? HostHint, string ProtectedPrivateKey);

/// <summary>
/// YouAuth logins in progress, and the data-protection keys that seal them and the upgrade page's session cookie,
/// in the same SQLite file as the codes, so both survive a restart
/// </summary>
public class YouAuthLoginStore : IXmlRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Lock _initializeLock = new();
    private bool _initialized;

    public YouAuthLoginStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task SaveAsync(string state, string identity, string? hostHint, string protectedPrivateKey,
        long expiresAt, long now)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM youauth_logins WHERE expires_at <= @now;
            INSERT INTO youauth_logins (state, identity, host_hint, private_key, expires_at)
            VALUES (@state, @identity, @hostHint, @privateKey, @expiresAt);
            """;
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@identity", identity);
        command.Parameters.AddWithValue("@hostHint", (object?)hostHint ?? DBNull.Value);
        command.Parameters.AddWithValue("@privateKey", protectedPrivateKey);
        command.Parameters.AddWithValue("@expiresAt", expiresAt);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The login under this state, removed so it cannot be used again; null when there is none or it expired
    /// </summary>
    public async Task<YouAuthLoginState?> TakeAsync(string state, long now)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM youauth_logins WHERE state = @state
            RETURNING identity, host_hint, private_key, expires_at;
            """;
        command.Parameters.AddWithValue("@state", state);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetInt64(3) <= now)
        {
            return null;
        }
        return new YouAuthLoginState(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));
    }

    //
    // IXmlRepository: data-protection keys
    //

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT xml FROM data_protection_keys;";
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO data_protection_keys (name, xml) VALUES (@name, @xml);";
        command.Parameters.AddWithValue("@name", friendlyName);
        command.Parameters.AddWithValue("@xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }

    //

    private SqliteConnection Open()
    {
        Initialize();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Creates the tables on first use, next to the codes'
    private void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initializeLock)
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS youauth_logins (
                    state TEXT PRIMARY KEY,
                    identity TEXT NOT NULL,
                    host_hint TEXT NULL,
                    private_key TEXT NOT NULL,
                    expires_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS data_protection_keys (
                    name TEXT PRIMARY KEY,
                    xml TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
