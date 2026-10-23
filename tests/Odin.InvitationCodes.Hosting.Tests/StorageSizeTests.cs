namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// Quotas as the upgrade page and the CLI show them
/// </summary>
public class StorageSizeTests
{
    [TestCase(1099511627776L, "1 TB")]
    [TestCase(64424509440L, "60 GB")]
    [TestCase(10737418240L, "10 GB")]
    [TestCase(5368709120L, "5 GB")]
    [TestCase(1048576L, "1 MB")]
    [TestCase(1610612736L, "1.5 GB")]
    [TestCase(1288490189L, "1.2 GB")]
    [TestCase(2048L, "2 KB")]
    [TestCase(500L, "500 bytes")]
    [TestCase(1L, "1 byte")]
    public void BytesReadAsPeopleReadThem(long bytes, string expected) =>
        Assert.That(StorageSize.Format(bytes), Is.EqualTo(expected));
}
