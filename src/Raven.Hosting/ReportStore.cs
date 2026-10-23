using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// A report of content or a user breaking the terms, filed on the site's report page (/report). Every field is what
/// the person reporting wrote.
/// </summary>
public record ContentReport(
    long Id,
    string ContactInfo,
    string ContentType,
    string Violation,
    string ContentAuthor,
    string Evidence,
    string Additional,
    long CreatedAt);

/// <summary>What the report form sends; every field but Additional is required</summary>
public record ContentReportRequest(
    string? ContactInfo,
    string? ContentType,
    string? Violation,
    string? ContentAuthor,
    string? Evidence,
    string? Additional)
{
    public const int ShortMax = 500;
    public const int LongMax = 5000;

    /// <summary>Why it cannot be filed; null when it can</summary>
    public string? Problem()
    {
        if (new[] { ContactInfo, ContentType, Violation, ContentAuthor, Evidence }.Any(string.IsNullOrWhiteSpace))
        {
            return "Fill in every field except the last.";
        }
        if (new[] { ContactInfo, ContentType, Violation, ContentAuthor }.Any(f => f!.Length > ShortMax) ||
            Evidence!.Length > LongMax || (Additional?.Length ?? 0) > LongMax)
        {
            return $"Keep the short answers under {ShortMax} characters and the long ones under {LongMax}.";
        }
        return null;
    }
}

/// <summary>
/// The reports filed on the report page, in the same SQLite file as the codes. Admins read them with the create key
/// (raven.sh reports).
/// </summary>
public class ReportStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Lock _initializeLock = new();
    private bool _initialized;

    public ReportStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task<long> AddAsync(ContentReportRequest report, long now)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO content_reports (contact_info, content_type, violation, content_author, evidence, additional, created_at)
            VALUES (@contactInfo, @contentType, @violation, @contentAuthor, @evidence, @additional, @createdAt)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@contactInfo", report.ContactInfo!.Trim());
        command.Parameters.AddWithValue("@contentType", report.ContentType!.Trim());
        command.Parameters.AddWithValue("@violation", report.Violation!.Trim());
        command.Parameters.AddWithValue("@contentAuthor", report.ContentAuthor!.Trim());
        command.Parameters.AddWithValue("@evidence", report.Evidence!.Trim());
        command.Parameters.AddWithValue("@additional", report.Additional?.Trim() ?? "");
        command.Parameters.AddWithValue("@createdAt", now);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The reports, newest first; only those since <paramref name="since"/> (ms) when given</summary>
    public async Task<List<ContentReport>> ListAsync(long? since = null)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, contact_info, content_type, violation, content_author, evidence, additional, created_at
            FROM content_reports WHERE created_at >= @since ORDER BY id DESC;
            """;
        command.Parameters.AddWithValue("@since", since ?? 0);
        await using var reader = await command.ExecuteReaderAsync();
        var reports = new List<ContentReport>();
        while (await reader.ReadAsync())
        {
            reports.Add(new ContentReport(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7)));
        }
        return reports;
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
                CREATE TABLE IF NOT EXISTS content_reports (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    contact_info TEXT NOT NULL,
                    content_type TEXT NOT NULL,
                    violation TEXT NOT NULL,
                    content_author TEXT NOT NULL,
                    evidence TEXT NOT NULL,
                    additional TEXT NOT NULL,
                    created_at INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
