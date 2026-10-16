using Microsoft.Data.Sqlite;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// An invitation-code store in a temp file, deleted on dispose
/// </summary>
public sealed class TempInvitationCodeStore : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"invitation-codes-{Guid.NewGuid()}.db");

    public TempInvitationCodeStore()
    {
        Store = new InvitationCodeStore(_path);
    }

    public InvitationCodeStore Store { get; }

    /// <summary>
    /// Sets when every use of a code happened, for tests about time
    /// </summary>
    public async Task SetUsedAtAsync(string displayCode, params DateTimeOffset[] usedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        await using var ids = connection.CreateCommand();
        ids.CommandText = "SELECT id FROM invitation_code_uses WHERE code = @code ORDER BY id;";
        ids.Parameters.AddWithValue("@code", displayCode.Replace("-", ""));
        var idList = new List<long>();
        await using (var reader = await ids.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) idList.Add(reader.GetInt64(0));
        }

        if (idList.Count != usedAt.Length)
        {
            throw new InvalidOperationException($"{displayCode} has {idList.Count} uses, not {usedAt.Length}");
        }

        for (var i = 0; i < idList.Count; i++)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE invitation_code_uses SET used_at = @at WHERE id = @id;";
            update.Parameters.AddWithValue("@at", usedAt[i].ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("@id", idList[i]);
            await update.ExecuteNonQueryAsync();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }
}
