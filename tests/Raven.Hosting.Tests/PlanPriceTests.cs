using Microsoft.Data.Sqlite;

namespace Raven.Hosting.Tests;

/// <summary>
/// Prices as people read them, what longer terms save, and how plans are kept: a JSON document per plan, and the
/// column-per-field table from before
/// </summary>
public class PlanPriceTests
{
    [TestCase(500L, "$5")]
    [TestCase(499L, "$4.99")]
    [TestCase(450L, "$4.50")]
    [TestCase(120000L, "$1,200")]
    [TestCase(0L, "Free")]
    public void MoneyReadsAsDollars(long cents, string expected) => Assert.That(Money.Format(cents), Is.EqualTo(expected));

    [TestCase(1, "month")]
    [TestCase(12, "year")]
    [TestCase(24, "2 years")]
    [TestCase(36, "3 years")]
    [TestCase(3, "3 months")]
    [TestCase(18, "18 months")]
    public void ATermReadsAsPeopleSayIt(int months, string expected) =>
        Assert.That(BillingTerm.Name(months), Is.EqualTo(expected));

    [Test]
    public void APriceReadsAsAmountPerTerm()
    {
        Assert.That(new PlanPrice(12, 5000).Text, Is.EqualTo("$50 / year"));
        Assert.That(new PlanPrice(1, 0).Text, Is.EqualTo("Free"), "free has no term");
    }

    [Test]
    public void LongerTermsSaveOnPayingMonthly()
    {
        var plan = Plan([new(1, 500), new(12, 5000), new(24, 12000), new(36, 17999)]);

        Assert.That(plan.Saving(plan.PriceFor(1)!), Is.Null, "the monthly price saves nothing on itself");
        Assert.That(plan.Saving(plan.PriceFor(12)!), Is.EqualTo(17), "$50 instead of $60");
        Assert.That(plan.Saving(plan.PriceFor(24)!), Is.Null, "$120 is what monthly costs");
        Assert.That(plan.Saving(plan.PriceFor(36)!), Is.Null, "$179.99 instead of $180 is under 1%");
    }

    [Test]
    public void WithoutAMonthlyPriceThereIsNothingToSaveOn()
    {
        var plan = Plan([new(12, 5000)]);

        Assert.That(plan.Saving(plan.PriceFor(12)!), Is.Null);
        Assert.That(Plan([new(1, 0), new(12, 0)]).Saving(new PlanPrice(12, 0)), Is.Null, "free monthly");
    }

    [Test]
    public async Task ThePlansTableFromBeforeMovesIntoDocuments()
    {
        var path = TempDatabase.NewPath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE plans (
                        id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, name TEXT NOT NULL, description TEXT NOT NULL,
                        quota_bytes INTEGER NOT NULL CHECK (quota_bytes > 0), public_web_presence INTEGER NOT NULL,
                        for_sale INTEGER NOT NULL, price TEXT NOT NULL, sort_order INTEGER NOT NULL,
                        created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                    INSERT INTO plans VALUES ('old', 'Old', '', 1, 1, 0, '$5 a month', 0, 1, 1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var store = new PlanStore(path);
            var old = await store.GetAsync("old");
            var created = await store.CreateAsync(new PlanRequest("new", "New", 1, true, Prices: [new(1, 100)]));

            Assert.That(old!.Name, Is.EqualTo("Old"), "kept");
            Assert.That(old.Prices, Is.Empty, "the free-text price is not a price");
            Assert.That(created.Prices, Is.EqualTo(new[] { new PlanPrice(1, 100) }));
        }
        finally
        {
            TempDatabase.Delete(path);
        }
    }

    [Test]
    public async Task ThePlansAndPricesTablesFromBeforeMoveIntoDocuments()
    {
        var path = TempDatabase.NewPath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                // The shape live had with prices in their own table: a plan for sale with prices, and one without
                command.CommandText =
                    """
                    CREATE TABLE plans (
                        id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, name TEXT NOT NULL, description TEXT NOT NULL,
                        quota_bytes INTEGER NOT NULL CHECK (quota_bytes > 0), public_web_presence INTEGER NOT NULL,
                        for_sale INTEGER NOT NULL, sort_order INTEGER NOT NULL, created_at INTEGER NOT NULL,
                        updated_at INTEGER NOT NULL);
                    CREATE TABLE plan_prices (plan_id TEXT NOT NULL COLLATE NOCASE, months INTEGER NOT NULL,
                        amount_cents INTEGER NOT NULL, PRIMARY KEY (plan_id, months));
                    INSERT INTO plans VALUES ('pro', 'Pro', 'Everything', 1099511627776, 1, 1, 3, 10, 20);
                    INSERT INTO plans VALUES ('personal', 'Personal', '', 107374182400, 1, 1, 1, 10, 20);
                    INSERT INTO plans VALUES ('free', 'Free', '', 64424509440, 1, 0, 0, 10, 20);
                    INSERT INTO plan_prices VALUES ('pro', 12, 10000), ('PRO', 1, 1000);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var plans = await new PlanStore(path).ListAsync();

            Assert.That(plans, Is.EqualTo(new[]
            {
                new Plan("free", "Free", "", 64424509440, true, false, 0, false, [], 0, 10, 20),
                new Plan("personal", "Personal", "", 107374182400, true, false, 0, true, [], 1, 10, 20),
                new Plan("pro", "Pro", "Everything", 1099511627776, true, false, 0, true, [new(1, 1000), new(12, 10000)], 3, 10, 20),
            }), "every plan as it was, prices included");

            await using var check = new SqliteConnection($"Data Source={path}");
            await check.OpenAsync();
            await using var tables = check.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'plan_prices';";
            Assert.That(Convert.ToInt64(await tables.ExecuteScalarAsync()), Is.Zero, "plan_prices is gone");
        }
        finally
        {
            TempDatabase.Delete(path);
        }
    }

    [Test]
    public async Task APlanIsKeptAsItsIdAndADocument()
    {
        var path = TempDatabase.NewPath();
        try
        {
            await new PlanStore(path).CreateAsync(new PlanRequest("gold", "Gold", 1, true, Prices: [new(12, 5000)]));

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('plans') ORDER BY cid;";
            var columns = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            }
            command.CommandText = "SELECT json FROM plans WHERE id = 'gold';";
            var json = (string)(await command.ExecuteScalarAsync())!;

            Assert.That(columns, Is.EqualTo(new[] { "id", "json" }));
            Assert.That(json, Does.Contain("\"prices\":[{\"months\":12,\"amountCents\":5000}]"));
            Assert.That(json, Does.Not.Contain("\"text\""), "only what is kept, not what is worked out from it");
        }
        finally
        {
            TempDatabase.Delete(path);
        }
    }

    [Test]
    public async Task ADocumentWithoutAFieldReadsItsDefault_AndOneWithAnUnknownFieldIgnoresIt()
    {
        var path = TempDatabase.NewPath();
        try
        {
            var store = new PlanStore(path);
            await store.ListAsync(); // makes the table
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO plans (id, json) VALUES
                        ('older', '{"name":"Older","quotaBytes":5}'),
                        ('newer', '{"name":"Newer","quotaBytes":5,"badge":"Popular","prices":[{"months":1,"amountCents":100,"currency":"USD"}]}');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var older = (await store.GetAsync("older"))!;
            var newer = (await store.GetAsync("newer"))!;

            Assert.That((older.Name, older.Description, older.ForSale, older.Prices.Count, older.SortOrder),
                Is.EqualTo(("Older", "", false, 0, 0)));
            Assert.That(newer.Prices, Is.EqualTo(new[] { new PlanPrice(1, 100) }));
            var edited = await store.UpdateAsync("older", new PlanChange(Description: "Edited"));
            Assert.That(edited!.Description, Is.EqualTo("Edited"));
        }
        finally
        {
            TempDatabase.Delete(path);
        }
    }

    private static Plan Plan(PlanPrice[] prices) => new("p", "P", "", 1, true, false, 0, true, prices, 0, 0, 0);
}
