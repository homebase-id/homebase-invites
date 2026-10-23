using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// The kinds of add-on: what each changes on top of the identity's plan
/// </summary>
public static class AddOnKind
{
    /// <summary>Bytes more than the plan's quota</summary>
    public const string Storage = "storage";

    /// <summary>A public home page on (1) or off (0), whatever the plan says; off is also a safety tool</summary>
    public const string PublicWebPresence = "public-web-presence";

    /// <summary>A public email address (1); recorded only, as email is not offered yet</summary>
    public const string PublicEmail = "public-email";

    /// <summary>Email aliases more than the plan's; recorded only, as email is not offered yet</summary>
    public const string EmailAliases = "email-aliases";

    public static readonly string[] All = [Storage, PublicWebPresence, PublicEmail, EmailAliases];

    /// <summary>Whether the identity hosts are given it; the email ones are only recorded until email exists</summary>
    public static bool IsSentToHosts(string kind) => kind is Storage or PublicWebPresence;
}

/// <summary>
/// Something an identity has on top of its plan, given by an admin: more storage, a public home page on or off, and
/// later email. It belongs to the identity, so it outlives plan changes and a paid term running out.
/// </summary>
/// <param name="Amount">Bytes for storage, aliases for email aliases; 1 on and 0 off for the others</param>
/// <param name="EndsAt">When it stops (unix ms); null when it lasts until removed</param>
/// <param name="RemovedAt">When it was removed, or ran out; null while it is active</param>
public record AddOn(
    long Id,
    string Identity,
    string Kind,
    long Amount,
    string? Note,
    long? EndsAt,
    string? AddedBy,
    long AddedAt,
    long? RemovedAt,
    string? RemovedBy)
{
    public bool IsActive => RemovedAt == null;

    /// <summary>What it gives, as people read it: "+200 GB storage", "Public home page off"</summary>
    public string Description => Kind switch
    {
        AddOnKind.Storage => $"+{StorageSize.Format(Amount)} storage",
        AddOnKind.PublicWebPresence => Amount == 1 ? "Public home page on" : "Public home page off",
        AddOnKind.PublicEmail => "Public email address",
        AddOnKind.EmailAliases => $"+{Amount} email alias{(Amount == 1 ? "" : "es")}",
        _ => Kind,
    };
}

/// <summary>
/// What an identity gets, its plan and add-ons together: what the host is given
/// </summary>
/// <param name="QuotaBytes">The plan's quota and every storage add-on; null when the plan's is not known</param>
/// <param name="PublicWebPresence">The latest public home page add-on's, or else the plan's</param>
public record Entitlement(long? QuotaBytes, bool? PublicWebPresence, long ExtraBytes)
{
    public static Entitlement Of(PlanGrant grant, IEnumerable<AddOn> addOns)
    {
        var active = addOns.Where(a => a.IsActive).ToList();
        var extra = active.Where(a => a.Kind == AddOnKind.Storage).Sum(a => a.Amount);
        var page = active.Where(a => a.Kind == AddOnKind.PublicWebPresence).MaxBy(a => a.Id);
        return new Entitlement(
            grant.QuotaBytes is { } quota ? quota + extra : null,
            page != null ? page.Amount == 1 : grant.PublicWebPresence,
            extra);
    }
}

/// <summary>
/// A request for an add-on the store refuses: an unknown kind, an amount that is not more than 0, an end date in the
/// past. The admin pages show the message.
/// </summary>
public class InvalidAddOnRequestException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// Add-ons, in the same SQLite file as the grants: one row each, kept when removed so the identity's history shows
/// it
/// </summary>
public class AddOnStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Lock _initializeLock = new();
    private bool _initialized;

    public AddOnStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>The identity's add-ons, active and removed, newest first</summary>
    public async Task<List<AddOn>> ListAsync(string identity)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE identity = @identity ORDER BY id DESC;";
        command.Parameters.AddWithValue("@identity", identity);
        return await ReadAllAsync(command);
    }

    /// <summary>Every active add-on, by identity</summary>
    public async Task<Dictionary<string, List<AddOn>>> ActiveByIdentityAsync()
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE removed_at IS NULL ORDER BY id;";
        return (await ReadAllAsync(command)).GroupBy(a => a.Identity).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Active add-ons whose end date is before <paramref name="now"/>: due to be removed</summary>
    public async Task<List<AddOn>> EndedAsync(long now)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE removed_at IS NULL AND ends_at IS NOT NULL AND ends_at < @now;";
        command.Parameters.AddWithValue("@now", now);
        return await ReadAllAsync(command);
    }

    public async Task<AddOn> AddAsync(string identity, string kind, long amount, string? note, long? endsAt,
        string? addedBy, long now)
    {
        if (!AddOnKind.All.Contains(kind))
        {
            throw new InvalidAddOnRequestException($"Unknown add-on '{kind}'");
        }
        if (kind is AddOnKind.Storage or AddOnKind.EmailAliases ? amount <= 0 : amount is not (0 or 1))
        {
            throw new InvalidAddOnRequestException(kind == AddOnKind.Storage
                ? "Storage must be more than 0"
                : kind == AddOnKind.EmailAliases ? "Aliases must be 1 or more" : "Choose on or off");
        }
        if (endsAt != null && endsAt <= now)
        {
            throw new InvalidAddOnRequestException("The end date must be in the future");
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plan_add_ons (identity, kind, amount, note, ends_at, added_by, added_at)
            VALUES (@identity, @kind, @amount, @note, @endsAt, @addedBy, @now)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@identity", identity);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@amount", amount);
        command.Parameters.AddWithValue("@note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
        command.Parameters.AddWithValue("@endsAt", (object?)endsAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@addedBy", (object?)addedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        var id = (long)(await command.ExecuteScalarAsync())!;
        return new AddOn(id, identity, kind, amount, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), endsAt,
            addedBy, now, null, null);
    }

    /// <summary>Removes an active add-on; null when there is no such active one for the identity</summary>
    public async Task<AddOn?> RemoveAsync(string identity, long id, string removedBy, long now)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE plan_add_ons SET removed_at = @now, removed_by = @removedBy " +
            "WHERE id = @id AND identity = @identity AND removed_at IS NULL;" +
            Select + " WHERE id = @id AND changes() = 1;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@identity", identity);
        command.Parameters.AddWithValue("@removedBy", removedBy);
        command.Parameters.AddWithValue("@now", now);
        var removed = await ReadAllAsync(command);
        return removed.FirstOrDefault();
    }

    //

    private const string Select =
        "SELECT id, identity, kind, amount, note, ends_at, added_by, added_at, removed_at, removed_by FROM plan_add_ons";

    private static async Task<List<AddOn>> ReadAllAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var addOns = new List<AddOn>();
        while (await reader.ReadAsync())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
            addOns.Add(new AddOn(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                Text(4), Number(5), Text(6), reader.GetInt64(7), Number(8), Text(9)));
        }
        return addOns;
    }

    private SqliteConnection Open()
    {
        Initialize();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Creates the table on first use, next to the grants'
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
                CREATE TABLE IF NOT EXISTS plan_add_ons (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    identity TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    amount INTEGER NOT NULL,
                    note TEXT NULL,
                    ends_at INTEGER NULL,
                    added_by TEXT NULL,
                    added_at INTEGER NOT NULL,
                    removed_at INTEGER NULL,
                    removed_by TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_plan_add_ons_identity ON plan_add_ons(identity);
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
