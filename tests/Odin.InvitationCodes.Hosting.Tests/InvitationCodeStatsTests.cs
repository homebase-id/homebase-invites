namespace Odin.InvitationCodes.Hosting.Tests;

public class InvitationCodeStatsTests : StoreTestBase
{
    private async Task<InvitationCode> CreateAndUse(string source, string? owner, int maxUses, int uses,
        string planId = "free")
    {
        var code = await Store.CreateAsync(owner, maxUses, source, enablePublicWebPresence: true, planId);
        for (var i = 0; i < uses; i++)
        {
            Assert.That(await Store.TryUseAsync(code.Code), Is.Not.Null);
        }
        return code;
    }

    [Test]
    public async Task AnEmptyStoreHasNothingToCount()
    {
        var stats = await Store.StatsAsync(null);

        Assert.That(stats.Totals, Is.EqualTo(new InvitationCodeStatsLine(null, 0, 0, 0, 0)));
        Assert.That((stats.NeverUsed, stats.UsedUp, stats.Disabled), Is.EqualTo((0, 0, 0)));
        Assert.That(stats.BySource, Is.Empty);
        Assert.That(stats.ByOwner, Is.Empty);
        Assert.That(stats.ByPlan, Is.Empty);
        Assert.That(stats.ByWeek, Is.Empty);
    }

    [Test]
    public async Task CodesAndSignupsAreCountedInTotalBySourceAndByOwner()
    {
        await CreateAndUse("spring", "frodo.dotyou.cloud", maxUses: 2, uses: 2);
        await CreateAndUse("spring", owner: null, maxUses: 1, uses: 1);
        var unused = await CreateAndUse("booth", "sam.dotyou.cloud", maxUses: 3, uses: 0);
        await Store.UpdateAsync(unused.Code, new InvitationCodeChange(Disabled: true));

        var stats = await Store.StatsAsync(null);

        Assert.That(stats.Totals, Is.EqualTo(new InvitationCodeStatsLine(null, 3, 6, 3, 0)));
        Assert.That((stats.NeverUsed, stats.UsedUp, stats.Disabled), Is.EqualTo((1, 2, 1)));
        Assert.That(stats.BySource, Is.EqualTo(new[]
        {
            new InvitationCodeStatsLine("spring", 2, 3, 3, 0),
            // Disabled, so none left whatever it has unused
            new InvitationCodeStatsLine("booth", 1, 3, 0, 0),
        }));
        // A code without an owner has no line of its own
        Assert.That(stats.ByOwner, Is.EqualTo(new[]
        {
            new InvitationCodeStatsLine("frodo.dotyou.cloud", 1, 2, 2, 0),
            new InvitationCodeStatsLine("sam.dotyou.cloud", 1, 3, 0, 0),
        }));
    }

    [Test]
    public async Task CodesAndSignupsAreCountedByPlan()
    {
        await CreateAndUse("spring", null, maxUses: 2, uses: 2, planId: "pro-100gb");
        await CreateAndUse("booth", null, maxUses: 3, uses: 1, planId: "pro-100gb");
        await CreateAndUse("spring", null, maxUses: 4, uses: 0, planId: "free");
        await CreateAndUse("booth", null, maxUses: 1, uses: 1);

        var stats = await Store.StatsAsync(null);

        Assert.That(stats.ByPlan, Is.EqualTo(new[]
        {
            new InvitationCodeStatsLine("pro-100gb", 2, 5, 3, 2),
            new InvitationCodeStatsLine("free", 2, 5, 1, 4),
        }));
    }

    [Test]
    public async Task ACodeLoweredBelowItsUsesLeavesNoneNotLessThanNone()
    {
        var lowered = await CreateAndUse("spring", null, maxUses: 3, uses: 2);
        await Store.UpdateAsync(lowered.Code, new InvitationCodeChange(MaxUses: 1));
        await CreateAndUse("spring", null, maxUses: 4, uses: 1);

        var stats = await Store.StatsAsync(null);

        Assert.That(stats.Totals.UsesLeft, Is.EqualTo(3));
    }

    [Test]
    public async Task SinceLimitsTheSignupsButNotTheCodes()
    {
        var code = await CreateAndUse("spring", "frodo.dotyou.cloud", maxUses: 2, uses: 2);
        var now = DateTimeOffset.UtcNow;
        await Temp.SetUsedAtAsync(code.Code, now.AddDays(-40), now.AddDays(-1));

        var stats = await Store.StatsAsync(now.AddDays(-30).ToUnixTimeMilliseconds());

        Assert.That(stats.Totals, Is.EqualTo(new InvitationCodeStatsLine(null, 1, 2, 1, 0)));
        Assert.That(stats.BySource.Single().Signups, Is.EqualTo(1));
        Assert.That(stats.ByWeek.Sum(w => w.Signups), Is.EqualTo(1));
        // Used up is about the code as it is, whenever its uses were taken
        Assert.That(stats.UsedUp, Is.EqualTo(1));
    }

    [Test]
    public async Task SignupsAreGroupedByTheWeekStartingMonday()
    {
        var code = await CreateAndUse("spring", null, maxUses: 4, uses: 4);
        await Temp.SetUsedAtAsync(code.Code,
            Utc(2026, 10, 12), // Monday
            Utc(2026, 10, 14), // Wednesday
            Utc(2026, 10, 18, 23), // Sunday, late
            Utc(2026, 10, 19)); // the next Monday

        var stats = await Store.StatsAsync(null);

        Assert.That(stats.ByWeek, Is.EqualTo(new[]
        {
            new WeeklySignups("2026-10-12", 3),
            new WeeklySignups("2026-10-19", 1),
        }));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 12) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
