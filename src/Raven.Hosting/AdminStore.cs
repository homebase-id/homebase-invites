using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>An identity that may use the admin pages (/admin), signed in with YouAuth</summary>
public record Admin(string Identity, long AddedAt);

/// <summary>
/// An admin that is not an identity's domain. The API answers 400.
/// </summary>
public class InvalidAdminRequestException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// Who may use the admin pages, in the same SQLite file as the codes: a row per identity. There are none at first;
/// the first are added with the create key (raven.sh admins add).
/// </summary>
public class AdminStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Lock _initializeLock = new();
    private bool _initialized;

    public AdminStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>The admins, in the order they were added</summary>
    public async Task<List<Admin>> ListAsync()
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT identity, added_at FROM admins ORDER BY added_at, identity;";
        await using var reader = await command.ExecuteReaderAsync();
        var admins = new List<Admin>();
        while (await reader.ReadAsync())
        {
            admins.Add(new Admin(reader.GetString(0), reader.GetInt64(1)));
        }
        return admins;
    }

    public async Task<bool> IsAdminAsync(string? identity)
    {
        var normalized = DomainNames.Normalize(identity);
        if (normalized == null)
        {
            return false;
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM admins WHERE identity = @identity;";
        command.Parameters.AddWithValue("@identity", normalized);
        return await command.ExecuteScalarAsync() != null;
    }

    /// <summary>Makes the identity an admin; adding one already there changes nothing</summary>
    public async Task<List<Admin>> AddAsync(string? identity, long now)
    {
        var normalized = DomainNames.Normalize(identity) ??
                         throw new InvalidAdminRequestException(
                             $"'{identity}' is not a Homebase identity; give its domain, like frodo.homebase.id");
        await using (var connection = Open())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO admins (identity, added_at) VALUES (@identity, @now) ON CONFLICT DO NOTHING;";
            command.Parameters.AddWithValue("@identity", normalized);
            command.Parameters.AddWithValue("@now", now);
            await command.ExecuteNonQueryAsync();
        }
        return await ListAsync();
    }

    /// <summary>Takes the identity off the admins; null when it is not one</summary>
    public async Task<List<Admin>?> RemoveAsync(string? identity)
    {
        var normalized = DomainNames.Normalize(identity);
        if (normalized == null)
        {
            return null;
        }

        await using (var connection = Open())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM admins WHERE identity = @identity;";
            command.Parameters.AddWithValue("@identity", normalized);
            if (await command.ExecuteNonQueryAsync() == 0)
            {
                return null;
            }
        }
        return await ListAsync();
    }

    //

    private SqliteConnection Open()
    {
        Initialize();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Creates the table on first use, next to the codes'
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
                CREATE TABLE IF NOT EXISTS admins (
                    identity TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    added_at INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
