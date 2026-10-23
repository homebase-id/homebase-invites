using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Odin.InvitationCodes.Hosting;

/// <summary>
/// A hosting plan: what an identity on it gets. The identity hosts have no plans of their own (odin-core #1994):
/// whoever gives an identity a plan sends its quota and public web presence with it, and the host stores them.
/// </summary>
/// <param name="Id">The plan Id, e.g. "veterans"; unique whatever its case, and never changed</param>
/// <param name="QuotaBytes">Bytes an identity on it may store; more than 0, there is no unlimited</param>
/// <param name="PublicWebPresence">Whether an identity on it has a public home page</param>
/// <param name="ForSale">Whether owners can buy it on the upgrade page</param>
/// <param name="Price">Shown to buyers, e.g. "$5 a month"; nothing charges it yet</param>
/// <param name="SortOrder">Plans are listed by this, then by Id</param>
public record Plan(
    string Id,
    string Name,
    string Description,
    long QuotaBytes,
    bool PublicWebPresence,
    bool ForSale,
    string Price,
    int SortOrder,
    long CreatedAt,
    long UpdatedAt)
{
    /// <summary>The quota as people read it, e.g. "1 TB"</summary>
    public string Storage => StorageSize.Format(QuotaBytes);
}

/// <summary>
/// A new plan. Description, price and sort order may be left out; the rest is required.
/// </summary>
public record PlanRequest(
    string? Id,
    string? Name,
    long? QuotaBytes,
    bool? PublicWebPresence,
    bool? ForSale = null,
    string? Description = null,
    string? Price = null,
    int? SortOrder = null);

/// <summary>
/// A change to a plan; null leaves a field as it is. The Id cannot be changed.
/// </summary>
public record PlanChange(
    string? Name = null,
    string? Description = null,
    long? QuotaBytes = null,
    bool? PublicWebPresence = null,
    bool? ForSale = null,
    string? Price = null,
    int? SortOrder = null);

/// <summary>
/// A plan request the store refuses: a bad Id, a missing name, a quota that is not more than 0. The API answers 400.
/// </summary>
public class InvalidPlanRequestException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// A plan Id that is taken, or a plan that codes or grants still use. The API answers 409.
/// </summary>
public class PlanConflictException(string message) : Exception(message);

/// <summary>
/// What a plan Id may look like, for plans, codes and grants: letters, digits, '.', '_' and '-', up to 64
/// </summary>
public static class PlanIds
{
    // "grants" would be /api/v1/plans/grants, the grants' own route
    public static readonly string[] Reserved = ["grants"];

    private static readonly Regex Format = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    public static bool IsValid(string? planId) => planId != null && Format.IsMatch(planId);
}

/// <summary>
/// Bytes as people read them: 1024-based, labelled TB, GB, MB and KB, at most one decimal
/// </summary>
public static class StorageSize
{
    private static readonly (long Bytes, string Unit)[] Units =
        [(1L << 40, "TB"), (1L << 30, "GB"), (1L << 20, "MB"), (1L << 10, "KB")];

    public static string Format(long bytes)
    {
        foreach (var (size, unit) in Units)
        {
            if (bytes >= size)
            {
                var value = Math.Round((double)bytes / size, 1);
                return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit;
            }
        }
        return bytes == 1 ? "1 byte" : $"{bytes} bytes";
    }
}

/// <summary>
/// The hosting plans, in the same SQLite file as the codes and grants. There are no built-in plans: the operator
/// creates them (README, "Plans").
/// </summary>
public class PlanStore
{
    private const int SqliteConstraintError = 19;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public PlanStore(string databasePath)
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

    public async Task<Plan> CreateAsync(PlanRequest request)
    {
        var id = request.Id?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidPlanRequestException("id is required");
        }
        if (!PlanIds.IsValid(id))
        {
            throw new InvalidPlanRequestException("Invalid id: letters, digits, '.', '_' and '-', up to 64");
        }
        if (PlanIds.Reserved.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidPlanRequestException($"'{id}' cannot be a plan id");
        }
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidPlanRequestException("name is required");
        }
        var quotaBytes = request.QuotaBytes ?? throw new InvalidPlanRequestException("quotaBytes is required");
        CheckQuota(quotaBytes);
        var publicWebPresence = request.PublicWebPresence ??
                                throw new InvalidPlanRequestException("publicWebPresence is required");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var plan = new Plan(id, name, request.Description?.Trim() ?? "", quotaBytes, publicWebPresence,
            request.ForSale ?? false, request.Price?.Trim() ?? "", request.SortOrder ?? 0, now, now);

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plans (id, name, description, quota_bytes, public_web_presence, for_sale, price, sort_order,
                               created_at, updated_at)
            VALUES (@id, @name, @description, @quotaBytes, @publicWebPresence, @forSale, @price, @sortOrder, @now, @now);
            """;
        command.Parameters.AddWithValue("@id", plan.Id);
        command.Parameters.AddWithValue("@name", plan.Name);
        command.Parameters.AddWithValue("@description", plan.Description);
        command.Parameters.AddWithValue("@quotaBytes", plan.QuotaBytes);
        command.Parameters.AddWithValue("@publicWebPresence", plan.PublicWebPresence);
        command.Parameters.AddWithValue("@forSale", plan.ForSale);
        command.Parameters.AddWithValue("@price", plan.Price);
        command.Parameters.AddWithValue("@sortOrder", plan.SortOrder);
        command.Parameters.AddWithValue("@now", now);
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError)
        {
            throw new PlanConflictException($"There is already a plan '{id}'");
        }
        return plan;
    }

    //

    /// <summary>
    /// The plan with this Id, in any case; null when there is none, or the Id cannot be one
    /// </summary>
    public async Task<Plan?> GetAsync(string? id)
    {
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return null;
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", trimmed);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    /// <summary>
    /// Every plan, by sort order, then Id
    /// </summary>
    public async Task<List<Plan>> ListAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " ORDER BY sort_order, id;";
        await using var reader = await command.ExecuteReaderAsync();
        var plans = new List<Plan>();
        while (await reader.ReadAsync())
        {
            plans.Add(Read(reader));
        }
        return plans;
    }

    //

    /// <summary>
    /// Changes a plan and returns it as it is now; null if there is no such plan. Identities already on it keep
    /// what they were given.
    /// </summary>
    public async Task<Plan?> UpdateAsync(string? id, PlanChange change)
    {
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return null;
        }

        var sets = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();

        if (change.Name != null)
        {
            if (change.Name.Trim() == "")
            {
                throw new InvalidPlanRequestException("name cannot be empty");
            }
            sets.Add("name = @name");
            command.Parameters.AddWithValue("@name", change.Name.Trim());
        }
        if (change.Description != null)
        {
            sets.Add("description = @description");
            command.Parameters.AddWithValue("@description", change.Description.Trim());
        }
        if (change.QuotaBytes != null)
        {
            CheckQuota(change.QuotaBytes.Value);
            sets.Add("quota_bytes = @quotaBytes");
            command.Parameters.AddWithValue("@quotaBytes", change.QuotaBytes.Value);
        }
        if (change.PublicWebPresence != null)
        {
            sets.Add("public_web_presence = @publicWebPresence");
            command.Parameters.AddWithValue("@publicWebPresence", change.PublicWebPresence.Value);
        }
        if (change.ForSale != null)
        {
            sets.Add("for_sale = @forSale");
            command.Parameters.AddWithValue("@forSale", change.ForSale.Value);
        }
        if (change.Price != null)
        {
            sets.Add("price = @price");
            command.Parameters.AddWithValue("@price", change.Price.Trim());
        }
        if (change.SortOrder != null)
        {
            sets.Add("sort_order = @sortOrder");
            command.Parameters.AddWithValue("@sortOrder", change.SortOrder.Value);
        }
        if (sets.Count == 0)
        {
            throw new InvalidPlanRequestException("Nothing to change");
        }

        command.CommandText = $"UPDATE plans SET {string.Join(", ", sets)}, updated_at = @now WHERE id = @id;" +
                              Select + " WHERE id = @id AND changes() = 1;";
        command.Parameters.AddWithValue("@id", trimmed);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    /// <summary>
    /// Deletes a plan; false if there is none. The caller checks no code or grant uses it.
    /// </summary>
    public async Task<bool> DeleteAsync(string? id)
    {
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return false;
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plans WHERE id = @id;";
        command.Parameters.AddWithValue("@id", trimmed);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    //

    private const string Select =
        """
        SELECT id, name, description, quota_bytes, public_web_presence, for_sale, price, sort_order, created_at,
               updated_at
        FROM plans
        """;

    private static Plan Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetBoolean(4),
        reader.GetBoolean(5),
        reader.GetString(6),
        reader.GetInt32(7),
        reader.GetInt64(8),
        reader.GetInt64(9));

    private static void CheckQuota(long quotaBytes)
    {
        if (quotaBytes <= 0)
        {
            throw new InvalidPlanRequestException("quotaBytes must be more than 0; there is no unlimited");
        }
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        await InitializeAsync();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // Creates the table on first use, next to the codes' and grants'
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
                CREATE TABLE IF NOT EXISTS plans (
                    id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    name TEXT NOT NULL,
                    description TEXT NOT NULL,
                    quota_bytes INTEGER NOT NULL CHECK (quota_bytes > 0),
                    public_web_presence INTEGER NOT NULL,
                    for_sale INTEGER NOT NULL,
                    price TEXT NOT NULL,
                    sort_order INTEGER NOT NULL,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }
}
