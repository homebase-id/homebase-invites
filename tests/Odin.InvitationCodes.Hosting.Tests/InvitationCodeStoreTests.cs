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
}
