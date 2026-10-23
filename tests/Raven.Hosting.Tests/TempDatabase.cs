using Microsoft.Data.Sqlite;

namespace Raven.Hosting.Tests;

/// <summary>
/// A database file in the temp directory, for one test
/// </summary>
public static class TempDatabase
{
    public static string NewPath() => Path.Combine(Path.GetTempPath(), $"invitation-codes-{Guid.NewGuid()}.db");

    /// <summary>
    /// Deletes the database and its WAL files
    /// </summary>
    public static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file);
        }
    }
}
