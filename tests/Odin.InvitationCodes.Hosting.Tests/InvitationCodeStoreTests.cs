namespace Odin.InvitationCodes.Hosting.Tests;

public class InvitationCodeStoreTests
{
    private TempInvitationCodeStore _temp = null!;
    private InvitationCodeStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _temp = new TempInvitationCodeStore();
        _store = _temp.Store;
    }

    [TearDown]
    public void TearDown() => _temp.Dispose();

    private Task<InvitationCode> Create(int maxUses, string? owner = "frodo.dotyou.cloud") =>
        _store.CreateAsync(owner, maxUses, "test", enablePublicWebPresence: true);

    [Test]
    public async Task ACodeCanBeUsedExactlyMaxUsesTimes()
    {
        var created = await Create(2);

        Assert.That(await _store.TryUseAsync(created.Code), Is.Not.Null);
        Assert.That(await _store.TryUseAsync(created.Code), Is.Not.Null);
        Assert.That(await _store.TryUseAsync(created.Code), Is.Null);

        Assert.That((await _store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(0));
    }

    [Test]
    public async Task AUseReturnsWhatTheCodeCarries()
    {
        var created = await _store.CreateAsync("Frodo.DotYou.Cloud", 3, "  spring campaign ", enablePublicWebPresence: false);

        var used = await _store.TryUseAsync(created.Code);

        Assert.That(used, Is.EqualTo(created with { UsesLeft = 2 }));
        Assert.That(used!.Owner, Is.EqualTo("frodo.dotyou.cloud"));
        Assert.That(used.Source, Is.EqualTo("spring campaign"));
        Assert.That(used.EnablePublicWebPresence, Is.False);
    }

    [Test]
    public async Task ACodeNeedNotHaveAnOwner()
    {
        var created = await Create(1, owner: null);

        Assert.That(created.Owner, Is.Null);
        Assert.That((await _store.TryUseAsync(created.Code))!.Owner, Is.Null);
    }

    [Test]
    public async Task AnUnknownCodeCannotBeUsed()
    {
        Assert.That(await _store.TryUseAsync("K7QXM2PD9FHW"), Is.Null);
        Assert.That(await _store.GetAsync("K7QXM2PD9FHW"), Is.Null);
    }

    [Test]
    public async Task UndoGivesAUseBack()
    {
        var created = await Create(1);
        Assert.That(await _store.TryUseAsync(created.Code), Is.Not.Null);

        await _store.UndoUseAsync(created.Code);

        Assert.That((await _store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(1));
        Assert.That(await _store.TryUseAsync(created.Code), Is.Not.Null);
    }

    [Test]
    public async Task UndoWithNoUsesChangesNothing()
    {
        var created = await Create(3);

        await _store.UndoUseAsync(created.Code);

        Assert.That((await _store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(3));
    }

    [Test]
    public async Task ListReturnsTheOwnersCodesOrAllWithUsesLeft()
    {
        var first = await Create(5);
        var second = await Create(1);
        var sams = await Create(5, owner: "sam.dotyou.cloud");
        await _store.TryUseAsync(first.Code);

        var frodos = await _store.ListAsync("frodo.dotyou.cloud");
        Assert.That(frodos.Select(c => c.Code), Is.EquivalentTo(new[] { first.Code, second.Code }));
        Assert.That(frodos.Single(c => c.Code == first.Code).UsesLeft, Is.EqualTo(4));

        var all = await _store.ListAsync(null);
        Assert.That(all.Select(c => c.Code), Is.EquivalentTo(new[] { first.Code, second.Code, sams.Code }));
    }

    [Test]
    public async Task ParallelUsesNeverExceedMaxUses()
    {
        var created = await Create(5);

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => _store.TryUseAsync(created.Code))));

        Assert.That(results.Count(used => used != null), Is.EqualTo(5));
        Assert.That((await _store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(0));
    }

    [Test]
    public async Task ACodeIsFoundHoweverItIsTyped()
    {
        var created = await Create(1);
        var typed = created.Code.Replace("-", " ").ToLowerInvariant();

        Assert.That(await _store.GetAsync(typed), Is.Not.Null);
        Assert.That(await _store.TryUseAsync(typed), Is.Not.Null);
    }

    [Test]
    public void AnInvalidOwnerOrUseCountIsRefused()
    {
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => Create(1, owner: "not a domain"));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => Create(0));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() => _store.ListAsync("not a domain"));
    }

    [Test]
    public async Task ADisabledCodeCannotBeUsedUntilEnabled()
    {
        var created = await Create(5);

        var disabled = await _store.UpdateAsync(created.Code, new InvitationCodeChange(Disabled: true));
        Assert.That(disabled!.Disabled, Is.True);
        Assert.That(await _store.TryUseAsync(created.Code), Is.Null);
        Assert.That((await _store.GetAsync(created.Code))!.UsesLeft, Is.EqualTo(5));

        await _store.UpdateAsync(created.Code, new InvitationCodeChange(Disabled: false));
        Assert.That(await _store.TryUseAsync(created.Code), Is.Not.Null);
    }

    [Test]
    public async Task EditChangesOnlyWhatIsGiven()
    {
        var created = await Create(5);

        var edited = await _store.UpdateAsync(created.Code.ToLowerInvariant(),
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

        var moved = await _store.UpdateAsync(created.Code, new InvitationCodeChange(Owner: "Sam.DotYou.Cloud"));
        Assert.That(moved!.Owner, Is.EqualTo("sam.dotyou.cloud"));

        var removed = await _store.UpdateAsync(created.Code, new InvitationCodeChange(Owner: ""));
        Assert.That(removed!.Owner, Is.Null);
    }

    [Test]
    public async Task LoweringMaxUsesBelowTheUsesTakenLeavesNone()
    {
        var created = await Create(3);
        await _store.TryUseAsync(created.Code);
        await _store.TryUseAsync(created.Code);

        var edited = await _store.UpdateAsync(created.Code, new InvitationCodeChange(MaxUses: 1));

        Assert.That(edited!.UsesLeft, Is.EqualTo(0));
        Assert.That(await _store.TryUseAsync(created.Code), Is.Null);
    }

    [Test]
    public async Task EditRefusesBadInputAndUnknownCodes()
    {
        var created = await Create(1);

        Assert.That(await _store.UpdateAsync("K7QXM2PD9FHW", new InvitationCodeChange(MaxUses: 2)), Is.Null);
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            _store.UpdateAsync(created.Code, new InvitationCodeChange()));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            _store.UpdateAsync(created.Code, new InvitationCodeChange(MaxUses: 0)));
        Assert.ThrowsAsync<InvalidInvitationCodeRequestException>(() =>
            _store.UpdateAsync(created.Code, new InvitationCodeChange(Owner: "not a domain")));
    }

    [Test]
    public async Task ADatabaseFromBeforeDisablingIsUpgraded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"invitation-codes-old-{Guid.NewGuid()}.db");
        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
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

            var store = new InvitationCodeStore(path);

            Assert.That(await store.GetAsync("K7QX-M2PD-9FHW"), Is.EqualTo(
                new InvitationCode("K7QX-M2PD-9FHW", null, "old", true, 2, 2, Disabled: false)));
            Assert.That(await store.TryUseAsync("K7QX-M2PD-9FHW"), Is.Not.Null);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }
}
