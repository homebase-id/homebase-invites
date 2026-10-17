using Microsoft.Data.Sqlite;

namespace Odin.InvitationCodes.Hosting.Tests;

public class InvitationCodeStoreTests : StoreTestBase
{
    private Task<InvitationCode> Create(int maxUses, string? owner = "frodo.dotyou.cloud") =>
        Store.CreateAsync(owner, maxUses, "test", enablePublicWebPresence: true, planId: "free");

    [Test]
    public async Task ACodeCanBeUsedExactlyMaxUsesTimes()
    {
        var created = await Create(2);

        Assert.That(await Store.TryUseAsync(created.Code), Is.Not.Null);
        Assert.That(await Store.TryUseAsync(created.Code), Is.Not.Null);
        Assert.That(await Store.TryUseAsync(created.Code), Is.Null);

        Assert.That((await Store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(0));
    }

    [Test]
    public async Task AUseReturnsWhatTheCodeCarries()
    {
        var created = await Store.CreateAsync("Frodo.DotYou.Cloud", 3, "  spring campaign ", enablePublicWebPresence: false,
            planId: " pro-100gb ");

        var used = await Store.TryUseAsync(created.Code);

        Assert.That(used, Is.EqualTo(created with { UsesLeft = 2 }));
        Assert.That(used!.Owner, Is.EqualTo("frodo.dotyou.cloud"));
        Assert.That(used.Source, Is.EqualTo("spring campaign"));
        Assert.That(used.EnablePublicWebPresence, Is.False);
        Assert.That(used.PlanId, Is.EqualTo("pro-100gb"));
    }

    [Test]
    public void ACodeMustHaveAPlan()
    {
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => Store.CreateAsync(null, 1, "test", true, planId: null));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => Store.CreateAsync(null, 1, "test", true, planId: " "));
    }

    [Test]
    public async Task ACodeNeedNotHaveAnOwner()
    {
        var created = await Create(1, owner: null);

        Assert.That(created.Owner, Is.Null);
        Assert.That((await Store.TryUseAsync(created.Code))!.Owner, Is.Null);
    }

    [Test]
    public async Task AnUnknownCodeCannotBeUsed()
    {
        Assert.That(await Store.TryUseAsync("K7QXM2PD9FHW"), Is.Null);
        Assert.That(await Store.GetAsync("K7QXM2PD9FHW"), Is.Null);
    }

    [Test]
    public async Task UndoGivesAUseBack()
    {
        var created = await Create(1);
        Assert.That(await Store.TryUseAsync(created.Code), Is.Not.Null);

        await Store.UndoUseAsync(created.Code);

        Assert.That((await Store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(1));
        Assert.That(await Store.TryUseAsync(created.Code), Is.Not.Null);
    }

    [Test]
    public async Task UndoWithNoUsesChangesNothing()
    {
        var created = await Create(3);

        await Store.UndoUseAsync(created.Code);

        Assert.That((await Store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(3));
    }

    [Test]
    public async Task ListReturnsTheOwnersCodesOrAllWithUsesLeft()
    {
        var first = await Create(5);
        var second = await Create(1);
        var sams = await Create(5, owner: "sam.dotyou.cloud");
        await Store.TryUseAsync(first.Code);

        var frodos = await Store.ListAsync("frodo.dotyou.cloud");
        Assert.That(frodos.Select(c => c.Code), Is.EquivalentTo(new[] { first.Code, second.Code }));
        Assert.That(frodos.Single(c => c.Code == first.Code).UsesLeft, Is.EqualTo(4));

        var all = await Store.ListAsync(null);
        Assert.That(all.Select(c => c.Code), Is.EquivalentTo(new[] { first.Code, second.Code, sams.Code }));
    }

    [Test]
    public async Task ParallelUsesNeverExceedMaxUses()
    {
        var created = await Create(5);

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => Store.TryUseAsync(created.Code))));

        Assert.That(results.Count(used => used != null), Is.EqualTo(5));
        Assert.That((await Store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(0));
    }

    [Test]
    public async Task ACodeIsFoundHoweverItIsTyped()
    {
        var created = await Create(1);
        var typed = created.Code.Replace("-", " ").ToLowerInvariant();

        Assert.That(await Store.GetAsync(typed), Is.Not.Null);
        Assert.That(await Store.TryUseAsync(typed), Is.Not.Null);
    }

    [Test]
    public void AnInvalidOwnerOrUseCountIsRefused()
    {
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => Create(1, owner: "not a domain"));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => Create(0));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => Store.ListAsync("not a domain"));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            Store.CreateAsync(null, 1, "test", true, planId: "not a plan"));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            Store.CreateAsync(null, 1, "test", true, planId: new string('x', 65)));
    }

    [Test]
    public async Task ADisabledCodeCannotBeUsedUntilEnabled()
    {
        var created = await Create(5);

        var disabled = await Store.UpdateAsync(created.Code, new InvitationCodeChange(Disabled: true));
        Assert.That(disabled!.Disabled, Is.True);
        Assert.That(await Store.TryUseAsync(created.Code), Is.Null);
        Assert.That((await Store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(5));

        await Store.UpdateAsync(created.Code, new InvitationCodeChange(Disabled: false));
        Assert.That(await Store.TryUseAsync(created.Code), Is.Not.Null);
    }

    [Test]
    public async Task EditChangesOnlyWhatIsGiven()
    {
        var created = await Create(5);

        var edited = await Store.UpdateAsync(created.Code.ToLowerInvariant(),
            new InvitationCodeChange(MaxUses: 8, Source: " renamed ", EnablePublicWebPresence: false));

        Assert.That(edited, Is.EqualTo(created with
        {
            MaxUses = 8, UsesLeft = 8, Source = "renamed", EnablePublicWebPresence = false
        }));
    }

    [Test]
    public async Task EditCanChangeOrRemoveTheOwner()
    {
        var created = await Create(1);

        var moved = await Store.UpdateAsync(created.Code, new InvitationCodeChange(Owner: "Sam.DotYou.Cloud"));
        Assert.That(moved!.Owner, Is.EqualTo("sam.dotyou.cloud"));

        var removed = await Store.UpdateAsync(created.Code, new InvitationCodeChange(Owner: ""));
        Assert.That(removed!.Owner, Is.Null);
    }

    [Test]
    public async Task EditCanChangeThePlanButNotRemoveIt()
    {
        var created = await Create(2);

        Assert.That(created.PlanId, Is.EqualTo("free"));
        var planned = await Store.UpdateAsync(created.Code, new InvitationCodeChange(PlanId: "pro-100gb"));
        Assert.That(planned, Is.EqualTo(created with { PlanId = "pro-100gb" }));
        Assert.That((await Store.TryUseAsync(created.Code))!.PlanId, Is.EqualTo("pro-100gb"));

        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            Store.UpdateAsync(created.Code, new InvitationCodeChange(PlanId: " ")));
        Assert.That((await Store.GetAsync(created.Code))!.PlanId, Is.EqualTo("pro-100gb"));
    }

    [Test]
    public async Task LoweringMaxUsesBelowTheUsesTakenLeavesNone()
    {
        var created = await Create(3);
        await Store.TryUseAsync(created.Code);
        await Store.TryUseAsync(created.Code);

        var edited = await Store.UpdateAsync(created.Code, new InvitationCodeChange(MaxUses: 1));

        Assert.That(edited!.UsesLeft, Is.EqualTo(0));
        Assert.That(await Store.TryUseAsync(created.Code), Is.Null);
    }

    [Test]
    public async Task EditRefusesBadInputAndUnknownCodes()
    {
        var created = await Create(1);

        Assert.That(await Store.UpdateAsync("K7QXM2PD9FHW", new InvitationCodeChange(MaxUses: 2)), Is.Null);
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            Store.UpdateAsync(created.Code, new InvitationCodeChange()));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            Store.UpdateAsync(created.Code, new InvitationCodeChange(MaxUses: 0)));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            Store.UpdateAsync(created.Code, new InvitationCodeChange(Owner: "not a domain")));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            Store.UpdateAsync(created.Code, new InvitationCodeChange(PlanId: "pro/100gb")));
    }

    [Test]
    public async Task ADatabaseFromBeforeDisablingAndPlansIsUpgraded()
    {
        using var old = new TempInvitationCodeStore();
        await using (var connection = new SqliteConnection($"Data Source={old.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE invitation_codes (code TEXT NOT NULL PRIMARY KEY, owner TEXT NULL, source TEXT NOT NULL,
                    enable_public_web_presence INTEGER NOT NULL, max_uses INTEGER NOT NULL, created_at INTEGER NOT NULL);
                CREATE TABLE invitation_code_uses (id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT NOT NULL, used_at INTEGER NOT NULL);
                INSERT INTO invitation_codes VALUES ('K7QXM2PD9FHW', NULL, 'old', 1, 2, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.That(await old.Store.GetAsync("K7QX-M2PD-9FHW"), Is.EqualTo(
            new InvitationCode("K7QX-M2PD-9FHW", null, "old", true, PlanId: null, 2, 2, Disabled: false)));
        Assert.That(await old.Store.TryUseAsync("K7QX-M2PD-9FHW"), Is.Not.Null);
        Assert.That((await old.Store.UpdateAsync("K7QX-M2PD-9FHW", new InvitationCodeChange(PlanId: "free")))!.PlanId,
            Is.EqualTo("free"));
    }
}
