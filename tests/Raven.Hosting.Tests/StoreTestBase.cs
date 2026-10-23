namespace Raven.Hosting.Tests;

/// <summary>
/// A fresh store for every test
/// </summary>
public abstract class StoreTestBase
{
    protected TempInvitationCodeStore Temp { get; private set; } = null!;
    protected InvitationCodeStore Store => Temp.Store;

    [SetUp]
    public void SetUp() => Temp = new TempInvitationCodeStore();

    [TearDown]
    public void TearDown() => Temp.Dispose();
}
