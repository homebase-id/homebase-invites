using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// Who may buy a plan on the upgrade page: everyone when purchases are open, only the testers while they are not.
/// Admins switch it with the create key (raven.sh purchases).
/// </summary>
/// <param name="Open">Whether anyone signed in may buy; false until an admin opens purchases</param>
/// <param name="Testers">Identities that may buy while purchases are not open, in the order they were added</param>
public record PurchaseAccess(bool Open, IReadOnlyList<PurchaseTester> Testers)
{
    public bool CanBuy(string identity) =>
        Open || Testers.Any(t => string.Equals(t.Identity, identity, StringComparison.OrdinalIgnoreCase));
}

public record PurchaseTester(string Identity, long AddedAt);

/// <summary>Whether purchases are open, as given to the API</summary>
public record PurchasesOpenRequest(bool? Open);

/// <summary>
/// A tester that is not an identity's domain. The API answers 400.
/// </summary>
public class InvalidPurchaseAccessRequestException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// Who may buy, in the same SQLite file as the codes: a settings row for whether purchases are open (none means not),
/// and a row per tester. Purchases start closed, so a new service sells nothing until an admin opens it.
/// </summary>
public class PurchaseAccessStore
{
    private const string OpenSetting = "purchases_open";

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Lock _initializeLock = new();
    private bool _initialized;

    public PurchaseAccessStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task<PurchaseAccess> GetAsync()
    {
        await using var connection = Open();

        bool open;
        await using (var setting = connection.CreateCommand())
        {
            setting.CommandText = "SELECT value FROM settings WHERE name = @name;";
            setting.Parameters.AddWithValue("@name", OpenSetting);
            open = await setting.ExecuteScalarAsync() is string value && value == "true";
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT identity, added_at FROM purchase_testers ORDER BY added_at, identity;";
        await using var reader = await command.ExecuteReaderAsync();
        var testers = new List<PurchaseTester>();
        while (await reader.ReadAsync())
        {
            testers.Add(new PurchaseTester(reader.GetString(0), reader.GetInt64(1)));
        }
        return new PurchaseAccess(open, testers);
    }

    public async Task<bool> CanBuyAsync(string identity) => (await GetAsync()).CanBuy(identity);

    public async Task<PurchaseAccess> SetOpenAsync(bool open)
    {
        await using (var connection = Open())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO settings (name, value) VALUES (@name, @value) " +
                "ON CONFLICT (name) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("@name", OpenSetting);
            command.Parameters.AddWithValue("@value", open ? "true" : "false");
            await command.ExecuteNonQueryAsync();
        }
        return await GetAsync();
    }

    /// <summary>Lets the identity buy while purchases are not open; adding one already there changes nothing</summary>
    public async Task<PurchaseAccess> AddTesterAsync(string? identity, long now)
    {
        var normalized = DomainNames.Normalize(identity) ??
                         throw new InvalidPurchaseAccessRequestException(
                             $"'{identity}' is not a Homebase identity; give its domain, like frodo.homebase.id");
        await using (var connection = Open())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO purchase_testers (identity, added_at) VALUES (@identity, @now) ON CONFLICT DO NOTHING;";
            command.Parameters.AddWithValue("@identity", normalized);
            command.Parameters.AddWithValue("@now", now);
            await command.ExecuteNonQueryAsync();
        }
        return await GetAsync();
    }

    /// <summary>Takes the identity off the testers; null when it is not one</summary>
    public async Task<PurchaseAccess?> RemoveTesterAsync(string? identity)
    {
        var normalized = DomainNames.Normalize(identity);
        if (normalized == null)
        {
            return null;
        }

        await using (var connection = Open())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM purchase_testers WHERE identity = @identity;";
            command.Parameters.AddWithValue("@identity", normalized);
            if (await command.ExecuteNonQueryAsync() == 0)
            {
                return null;
            }
        }
        return await GetAsync();
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
                CREATE TABLE IF NOT EXISTS settings (
                    name TEXT NOT NULL PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS purchase_testers (
                    identity TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    added_at INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
