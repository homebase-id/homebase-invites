using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Odin.InvitationCodes.Hosting;

/// <summary>
/// An invitation code as shown to people: the code in its display form (K7QX-M2PD-9FHW).
/// </summary>
/// <param name="Owner">The identity the code belongs to, if any</param>
/// <param name="Source">Free text saying where the code came from</param>
/// <param name="EnablePublicWebPresence">Whether an identity that signs up with the code gets a public home page</param>
/// <param name="Disabled">A disabled code cannot be used, whatever uses it has left</param>
public record InvitationCode(
    string Code,
    string? Owner,
    string Source,
    bool EnablePublicWebPresence,
    int MaxUses,
    int UsesLeft,
    bool Disabled);

/// <summary>
/// A change to a code; null leaves a field as it is. An empty <see cref="Owner"/> removes the owner.
/// </summary>
public record InvitationCodeChange(
    int? MaxUses = null,
    string? Source = null,
    string? Owner = null,
    bool? EnablePublicWebPresence = null,
    bool? Disabled = null);

/// <summary>
/// A request the store refuses: a bad owner or number of uses. The API answers 400.
/// </summary>
public class InvalidInvitationCodeRequestException(string message) : Exception(message);

/// <summary>
/// The invitation codes and their uses (#1908), in SQLite. A use records only when it happened: who signed
/// up with a code is kept on their own registration, not here.
///
/// Every method takes a code as typed (any case, with or without dashes); a code that cannot be one is
/// simply not found.
/// </summary>
public class InvitationCodeStore
{
    public const int MaxUsesLimit = 10_000;

    private const int MaxCreateAttempts = 5;
    private const int SqliteConstraintError = 19;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public InvitationCodeStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    //

    public async Task<InvitationCode> CreateAsync(string? owner, int maxUses, string? source, bool enablePublicWebPresence)
    {
        if (maxUses is < 1 or > MaxUsesLimit)
        {
            throw new InvalidInvitationCodeRequestException($"MaxUses must be between 1 and {MaxUsesLimit}");
        }

        var normalizedOwner = owner == null ? null : NormalizeOwner(owner);
        var normalizedSource = source?.Trim() ?? "";

        await using var connection = await OpenAsync();
        for (var attempt = 1; ; attempt++)
        {
            var code = InvitationCodeFormat.Generate();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO invitation_codes (code, owner, source, enable_public_web_presence, max_uses, created_at)
                VALUES (@code, @owner, @source, @enablePublicWebPresence, @maxUses, @now);
                """;
            command.Parameters.AddWithValue("@code", code);
            command.Parameters.AddWithValue("@owner", (object?)normalizedOwner ?? DBNull.Value);
            command.Parameters.AddWithValue("@source", normalizedSource);
            command.Parameters.AddWithValue("@enablePublicWebPresence", enablePublicWebPresence);
            command.Parameters.AddWithValue("@maxUses", maxUses);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try
            {
                await command.ExecuteNonQueryAsync();
                return new InvitationCode(InvitationCodeFormat.Display(code), normalizedOwner, normalizedSource,
                    enablePublicWebPresence, maxUses, maxUses, Disabled: false);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError && attempt < MaxCreateAttempts)
            {
                // The random code is already taken; draw another
            }
        }
    }

    //

    public async Task<InvitationCode?> GetAsync(string? input)
    {
        var code = InvitationCodeFormat.Normalize(input);
        if (code == null)
        {
            return null;
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectWithUsesLeft + " WHERE c.code = @code GROUP BY c.code;";
        command.Parameters.AddWithValue("@code", code);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    //

    /// <summary>
    /// The codes, oldest first; only the owner's when <paramref name="owner"/> is given.
    /// </summary>
    public async Task<List<InvitationCode>> ListAsync(string? owner)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        if (owner == null)
        {
            command.CommandText = SelectWithUsesLeft + " GROUP BY c.code ORDER BY c.created_at;";
        }
        else
        {
            command.CommandText = SelectWithUsesLeft + " WHERE c.owner = @owner GROUP BY c.code ORDER BY c.created_at;";
            command.Parameters.AddWithValue("@owner", NormalizeOwner(owner));
        }

        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<InvitationCode>();
        while (await reader.ReadAsync())
        {
            result.Add(Read(reader));
        }
        return result;
    }

    //

    /// <summary>
    /// Records a use if the code exists, is not disabled and has one left, and returns the code; null if refused.
    /// One statement takes the use, so two signups racing for the last use cannot both get it.
    /// </summary>
    public async Task<InvitationCode?> TryUseAsync(string? input)
    {
        var code = InvitationCodeFormat.Normalize(input);
        if (code == null)
        {
            return null;
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO invitation_code_uses (code, used_at)
            SELECT @code, @now
            WHERE EXISTS (
                SELECT 1 FROM invitation_codes c
                WHERE c.code = @code
                  AND c.disabled = 0
                  AND c.max_uses > (SELECT COUNT(*) FROM invitation_code_uses u WHERE u.code = @code)
            );
            """ + SelectWithUsesLeft + " WHERE c.code = @code AND changes() = 1 GROUP BY c.code;";
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    //

    /// <summary>
    /// Gives back the latest use, for a signup that failed after taking it.
    /// </summary>
    public async Task UndoUseAsync(string? input)
    {
        var code = InvitationCodeFormat.Normalize(input);
        if (code == null)
        {
            return;
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM invitation_code_uses WHERE id = (SELECT MAX(id) FROM invitation_code_uses WHERE code = @code);";
        command.Parameters.AddWithValue("@code", code);
        await command.ExecuteNonQueryAsync();
    }

    //

    /// <summary>
    /// Changes a code and returns it as it is now; null if there is no such code. Lowering MaxUses below the uses
    /// already taken leaves none left; it never takes a use back.
    /// </summary>
    public async Task<InvitationCode?> UpdateAsync(string? input, InvitationCodeChange change)
    {
        var code = InvitationCodeFormat.Normalize(input);
        if (code == null)
        {
            return null;
        }

        var sets = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();

        if (change.MaxUses != null)
        {
            if (change.MaxUses is < 1 or > MaxUsesLimit)
            {
                throw new InvalidInvitationCodeRequestException($"MaxUses must be between 1 and {MaxUsesLimit}");
            }
            sets.Add("max_uses = @maxUses");
            command.Parameters.AddWithValue("@maxUses", change.MaxUses.Value);
        }
        if (change.Source != null)
        {
            sets.Add("source = @source");
            command.Parameters.AddWithValue("@source", change.Source.Trim());
        }
        if (change.Owner != null)
        {
            sets.Add("owner = @owner");
            command.Parameters.AddWithValue("@owner",
                change.Owner.Trim() == "" ? DBNull.Value : NormalizeOwner(change.Owner));
        }
        if (change.EnablePublicWebPresence != null)
        {
            sets.Add("enable_public_web_presence = @enablePublicWebPresence");
            command.Parameters.AddWithValue("@enablePublicWebPresence", change.EnablePublicWebPresence.Value);
        }
        if (change.Disabled != null)
        {
            sets.Add("disabled = @disabled");
            command.Parameters.AddWithValue("@disabled", change.Disabled.Value);
        }
        if (sets.Count == 0)
        {
            throw new InvalidInvitationCodeRequestException("Nothing to change");
        }

        command.CommandText = $"UPDATE invitation_codes SET {string.Join(", ", sets)} WHERE code = @code;";
        command.Parameters.AddWithValue("@code", code);
        if (await command.ExecuteNonQueryAsync() == 0)
        {
            return null;
        }

        return await GetAsync(code);
    }

    //

    private const string SelectWithUsesLeft =
        """
        SELECT c.code, c.owner, c.source, c.enable_public_web_presence, c.max_uses, c.max_uses - COUNT(u.id), c.disabled
        FROM invitation_codes c LEFT JOIN invitation_code_uses u ON u.code = c.code
        """;

    private static InvitationCode Read(SqliteDataReader reader)
    {
        return new InvitationCode(
            InvitationCodeFormat.Display(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            reader.GetInt32(4),
            Math.Max(0, reader.GetInt32(5)),
            reader.GetBoolean(6));
    }

    // Two or more labels of letters, digits and inner hyphens, each 1-63 long
    private static readonly Regex DomainName =
        new(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

    private static string NormalizeOwner(string owner)
    {
        var normalized = owner.Trim().ToLowerInvariant();
        if (normalized.Length > 253 || !DomainName.IsMatch(normalized))
        {
            throw new InvalidInvitationCodeRequestException("Invalid owner");
        }
        return normalized;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        await InitializeAsync();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // Creates the database on first use
    private async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync();
        try
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS invitation_codes (
                    code TEXT NOT NULL PRIMARY KEY,
                    owner TEXT NULL,
                    source TEXT NOT NULL,
                    enable_public_web_presence INTEGER NOT NULL,
                    max_uses INTEGER NOT NULL,
                    created_at INTEGER NOT NULL,
                    disabled INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_invitation_codes_owner ON invitation_codes(owner);
                CREATE TABLE IF NOT EXISTS invitation_code_uses (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL,
                    used_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_invitation_code_uses_code ON invitation_code_uses(code);
                """;
            await command.ExecuteNonQueryAsync();

            // Databases created before codes could be disabled lack the column
            await using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('invitation_codes') WHERE name = 'disabled';";
            if (Convert.ToInt64(await columns.ExecuteScalarAsync()) == 0)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE invitation_codes ADD COLUMN disabled INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync();
            }

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }
}
