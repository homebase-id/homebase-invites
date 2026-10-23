using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// A hosting plan: what an identity on it gets. The identity hosts have no plans of their own (odin-core #1994):
/// whoever gives an identity a plan sends its quota and public web presence with it, and the host stores them.
/// </summary>
/// <param name="Id">The plan Id, e.g. "veterans"; unique whatever its case, and never changed</param>
/// <param name="QuotaBytes">Bytes an identity on it may store; more than 0, there is no unlimited</param>
/// <param name="PublicWebPresence">Whether an identity on it has a public home page</param>
/// <param name="ForSale">Whether owners can buy it on the upgrade page; a plan for sale has at least one price</param>
/// <param name="Prices">What it costs, one price per billing term, shortest term first; none for a plan not sold</param>
/// <param name="SortOrder">Plans are listed by this, then by Id</param>
public record Plan(
    string Id,
    string Name,
    string Description,
    long QuotaBytes,
    bool PublicWebPresence,
    bool ForSale,
    IReadOnlyList<PlanPrice> Prices,
    int SortOrder,
    long CreatedAt,
    long UpdatedAt)
{
    /// <summary>The quota as people read it, e.g. "1 TB"</summary>
    public string Storage => StorageSize.Format(QuotaBytes);

    public PlanPrice? PriceFor(int? months) => Prices.FirstOrDefault(p => p.Months == months);

    /// <summary>
    /// How much less a longer term costs than paying monthly for as long, in whole percent; null when the plan has no
    /// monthly price, the term is the monthly one, or it saves less than 1%
    /// </summary>
    public int? Saving(PlanPrice price)
    {
        var monthly = PriceFor(1);
        if (monthly == null || monthly.AmountCents == 0 || price.Months == 1)
        {
            return null;
        }
        var full = (decimal)monthly.AmountCents * price.Months;
        var percent = (int)Math.Round((full - price.AmountCents) * 100 / full, MidpointRounding.AwayFromZero);
        return percent >= 1 ? percent : null;
    }

    // A record compares lists by reference; plans are the same when their prices are
    public virtual bool Equals(Plan? other) =>
        other != null && Id == other.Id && Name == other.Name && Description == other.Description &&
        QuotaBytes == other.QuotaBytes && PublicWebPresence == other.PublicWebPresence && ForSale == other.ForSale &&
        Prices.SequenceEqual(other.Prices) && SortOrder == other.SortOrder && CreatedAt == other.CreatedAt &&
        UpdatedAt == other.UpdatedAt;

    public override int GetHashCode() => HashCode.Combine(Id, QuotaBytes, ForSale, Prices.Count, UpdatedAt);
}

/// <summary>
/// What a plan costs for one billing term, in US dollars
/// </summary>
/// <param name="Months">The term: 1 is monthly, 12 yearly; any number from 1 to 120</param>
/// <param name="AmountCents">What the term costs, in cents; 0 is free</param>
public record PlanPrice(int Months, long AmountCents)
{
    /// <summary>The amount as people read it: "$5", "$4.99", "Free"</summary>
    public string Amount => Money.Format(AmountCents);

    /// <summary>The term as people read it: "month", "year", "2 years", "3 months"</summary>
    public string Per => BillingTerm.Name(Months);

    /// <summary>"$50 / year", or "Free"</summary>
    public string Text => AmountCents == 0 ? Amount : $"{Amount} / {Per}";
}

/// <summary>
/// A price for one billing term, as given to the API
/// </summary>
public record PlanPriceRequest(int? Months, long? AmountCents);

/// <summary>
/// A new plan. Description, prices and sort order may be left out; the rest is required. A plan for sale needs at
/// least one price.
/// </summary>
public record PlanRequest(
    string? Id,
    string? Name,
    long? QuotaBytes,
    bool? PublicWebPresence,
    bool? ForSale = null,
    string? Description = null,
    IReadOnlyList<PlanPriceRequest>? Prices = null,
    int? SortOrder = null);

/// <summary>
/// A change to a plan; null leaves a field as it is. The Id cannot be changed; prices are changed one term at a time.
/// </summary>
public record PlanChange(
    string? Name = null,
    string? Description = null,
    long? QuotaBytes = null,
    bool? PublicWebPresence = null,
    bool? ForSale = null,
    int? SortOrder = null);

/// <summary>
/// A plan request the store refuses: a bad Id, a missing name, a quota that is not more than 0, a bad price, a plan
/// for sale without one. The API answers 400.
/// </summary>
public class InvalidPlanRequestException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// A plan Id that is taken, a plan that codes or grants still use, or the last price of a plan for sale. The API
/// answers 409.
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
/// US dollars as people read them: "$5", "$4.99", "$1,200", "Free"
/// </summary>
public static class Money
{
    public static string Format(long cents) => cents == 0
        ? "Free"
        : cents % 100 == 0
            ? "$" + (cents / 100).ToString("N0", CultureInfo.InvariantCulture)
            : "$" + (cents / 100m).ToString("N2", CultureInfo.InvariantCulture);
}

/// <summary>
/// A billing term as people read it: 1 month is "month", 12 "year", 24 "2 years", 3 "3 months"
/// </summary>
public static class BillingTerm
{
    public const int LongestMonths = 120;

    public static string Name(int months) => months switch
    {
        1 => "month",
        12 => "year",
        _ when months % 12 == 0 => $"{months / 12} years",
        _ => $"{months} months",
    };
}

/// <summary>
/// The hosting plans and their prices, in the same SQLite file as the codes and grants. There are no built-in plans:
/// the operator creates them (README, "Plans").
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

        var prices = (request.Prices ?? []).Select(p => CheckPrice(
                p.Months ?? throw new InvalidPlanRequestException("Each price needs months"),
                p.AmountCents ?? throw new InvalidPlanRequestException("Each price needs amountCents")))
            .OrderBy(p => p.Months)
            .ToList();
        if (prices.Select(p => p.Months).Distinct().Count() != prices.Count)
        {
            throw new InvalidPlanRequestException("Two prices for the same number of months");
        }
        var forSale = request.ForSale ?? false;
        if (forSale && prices.Count == 0)
        {
            throw new InvalidPlanRequestException("A plan for sale needs at least one price");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var plan = new Plan(id, name, request.Description?.Trim() ?? "", quotaBytes, publicWebPresence, forSale, prices,
            request.SortOrder ?? 0, now, now);

        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO plans (id, name, description, quota_bytes, public_web_presence, for_sale, sort_order,
                                   created_at, updated_at)
                VALUES (@id, @name, @description, @quotaBytes, @publicWebPresence, @forSale, @sortOrder, @now, @now);
                """;
            command.Parameters.AddWithValue("@id", plan.Id);
            command.Parameters.AddWithValue("@name", plan.Name);
            command.Parameters.AddWithValue("@description", plan.Description);
            command.Parameters.AddWithValue("@quotaBytes", plan.QuotaBytes);
            command.Parameters.AddWithValue("@publicWebPresence", plan.PublicWebPresence);
            command.Parameters.AddWithValue("@forSale", plan.ForSale);
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
        }
        foreach (var price in prices)
        {
            await SetPriceAsync(connection, plan.Id, price);
        }
        await transaction.CommitAsync();
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
        return await GetAsync(connection, trimmed!);
    }

    /// <summary>
    /// Every plan, by sort order, then Id
    /// </summary>
    public async Task<List<Plan>> ListAsync()
    {
        await using var connection = await OpenAsync();
        var prices = await ReadPricesAsync(connection, null);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " ORDER BY sort_order, id;";
        await using var reader = await command.ExecuteReaderAsync();
        var plans = new List<Plan>();
        while (await reader.ReadAsync())
        {
            plans.Add(Read(reader, prices));
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
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
        if (change.SortOrder != null)
        {
            sets.Add("sort_order = @sortOrder");
            command.Parameters.AddWithValue("@sortOrder", change.SortOrder.Value);
        }
        if (sets.Count == 0)
        {
            throw new InvalidPlanRequestException("Nothing to change");
        }

        var plan = await GetAsync(connection, trimmed!);
        if (plan == null)
        {
            return null;
        }
        if (change.ForSale == true && plan.Prices.Count == 0)
        {
            throw new InvalidPlanRequestException(
                $"A plan for sale needs at least one price; give {plan.Id} one first (raven.sh plan-price)");
        }

        command.CommandText = $"UPDATE plans SET {string.Join(", ", sets)}, updated_at = @now WHERE id = @id;";
        command.Parameters.AddWithValue("@id", plan.Id);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return await GetAsync(connection, plan.Id);
    }

    /// <summary>
    /// Sets what the plan costs for this many months, adding the term or changing its amount; null if there is no
    /// such plan
    /// </summary>
    public async Task<Plan?> SetPriceAsync(string? id, int months, long amountCents)
    {
        var price = CheckPrice(months, amountCents);
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return null;
        }

        await using var connection = await OpenAsync();
        var plan = await GetAsync(connection, trimmed!);
        if (plan == null)
        {
            return null;
        }
        await SetPriceAsync(connection, plan.Id, price);
        await TouchAsync(connection, plan.Id);
        return await GetAsync(connection, plan.Id);
    }

    /// <summary>
    /// Takes away the plan's price for this many months. Found is false when there is no such plan or price; the last
    /// price of a plan for sale cannot be taken away.
    /// </summary>
    public async Task<(Plan? Plan, bool Found)> RemovePriceAsync(string? id, int months)
    {
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return (null, false);
        }

        await using var connection = await OpenAsync();
        var plan = await GetAsync(connection, trimmed!);
        if (plan?.PriceFor(months) == null)
        {
            return (plan, false);
        }
        if (plan.ForSale && plan.Prices.Count == 1)
        {
            throw new PlanConflictException(
                $"{plan.Id} is for sale and this is its only price; mark it not for sale first, or add another price");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM plan_prices WHERE plan_id = @id AND months = @months;";
            command.Parameters.AddWithValue("@id", plan.Id);
            command.Parameters.AddWithValue("@months", months);
            await command.ExecuteNonQueryAsync();
        }
        await TouchAsync(connection, plan.Id);
        return (await GetAsync(connection, plan.Id), true);
    }

    /// <summary>
    /// Deletes a plan and its prices; false if there is none. The caller checks no code or grant uses it.
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
        command.CommandText = "DELETE FROM plan_prices WHERE plan_id = @id; DELETE FROM plans WHERE id = @id;";
        command.Parameters.AddWithValue("@id", trimmed);
        await command.ExecuteNonQueryAsync();
        await using var changes = connection.CreateCommand();
        changes.CommandText = "SELECT changes();";
        return Convert.ToInt64(await changes.ExecuteScalarAsync()) == 1;
    }

    //

    private const string Select =
        """
        SELECT id, name, description, quota_bytes, public_web_presence, for_sale, sort_order, created_at, updated_at
        FROM plans
        """;

    private static async Task<Plan?> GetAsync(SqliteConnection connection, string id)
    {
        var prices = await ReadPricesAsync(connection, id);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader, prices) : null;
    }

    // Prices by plan Id (in any case), shortest term first; only one plan's when an Id is given
    private static async Task<Dictionary<string, List<PlanPrice>>> ReadPricesAsync(SqliteConnection connection,
        string? id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT plan_id, months, amount_cents FROM plan_prices" +
                              (id == null ? "" : " WHERE plan_id = @id") + " ORDER BY months;";
        if (id != null)
        {
            command.Parameters.AddWithValue("@id", id);
        }
        var prices = new Dictionary<string, List<PlanPrice>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var planId = reader.GetString(0);
            if (!prices.TryGetValue(planId, out var list))
            {
                prices[planId] = list = [];
            }
            list.Add(new PlanPrice(reader.GetInt32(1), reader.GetInt64(2)));
        }
        return prices;
    }

    private static Plan Read(SqliteDataReader reader, Dictionary<string, List<PlanPrice>> prices) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetBoolean(4),
        reader.GetBoolean(5),
        prices.TryGetValue(reader.GetString(0), out var list) ? list : [],
        reader.GetInt32(6),
        reader.GetInt64(7),
        reader.GetInt64(8));

    private static async Task SetPriceAsync(SqliteConnection connection, string planId, PlanPrice price)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plan_prices (plan_id, months, amount_cents) VALUES (@id, @months, @amountCents)
            ON CONFLICT (plan_id, months) DO UPDATE SET amount_cents = excluded.amount_cents;
            """;
        command.Parameters.AddWithValue("@id", planId);
        command.Parameters.AddWithValue("@months", price.Months);
        command.Parameters.AddWithValue("@amountCents", price.AmountCents);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task TouchAsync(SqliteConnection connection, string planId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plans SET updated_at = @now WHERE id = @id;";
        command.Parameters.AddWithValue("@id", planId);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync();
    }

    private static PlanPrice CheckPrice(int months, long amountCents)
    {
        if (months is < 1 or > BillingTerm.LongestMonths)
        {
            throw new InvalidPlanRequestException($"months must be between 1 and {BillingTerm.LongestMonths}");
        }
        if (amountCents < 0)
        {
            throw new InvalidPlanRequestException("amountCents cannot be negative; 0 is free");
        }
        return new PlanPrice(months, amountCents);
    }

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

    // Creates the tables on first use, next to the codes' and grants'
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
            await using (var command = connection.CreateCommand())
            {
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
                        sort_order INTEGER NOT NULL,
                        created_at INTEGER NOT NULL,
                        updated_at INTEGER NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS plan_prices (
                        plan_id TEXT NOT NULL COLLATE NOCASE,
                        months INTEGER NOT NULL CHECK (months BETWEEN 1 AND 120),
                        amount_cents INTEGER NOT NULL CHECK (amount_cents >= 0),
                        PRIMARY KEY (plan_id, months)
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            // The first plans table had one free-text price; prices are their own table now
            await using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('plans') WHERE name = 'price';";
                if (Convert.ToInt64(await columns.ExecuteScalarAsync()) == 1)
                {
                    await using var drop = connection.CreateCommand();
                    drop.CommandText = "ALTER TABLE plans DROP COLUMN price;";
                    await drop.ExecuteNonQueryAsync();
                }
            }

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }
}
