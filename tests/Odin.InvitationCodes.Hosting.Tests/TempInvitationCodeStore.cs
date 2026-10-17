using Microsoft.Data.Sqlite;

namespace Odin.InvitationCodes.Hosting.Tests;

/// <summary>
/// An invitation-code store in a temp file, deleted on dispose. The store creates its tables on first use, so
/// a test can put an older database at <see cref="Path"/> before then.
/// </summary>
public sealed class TempInvitationCodeStore : IDisposable
{
    public TempInvitationCodeStore()
    {
        Store = new InvitationCodeStore(Path);
    }

    public string Path { get; } = TempDatabase.NewPath();

    public InvitationCodeStore Store { get; }

    /// <summary>
    /// Sets when every use of a code happened, for tests about time
    /// </summary>
    public async Task SetUsedAtAsync(string displayCode, params DateTimeOffset[] usedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={Path}");
        await connection.OpenAsync();
        await using var ids = connection.CreateCommand();
        ids.CommandText = "SELECT id FROM invitation_code_uses WHERE code = @code ORDER BY id;";
        ids.Parameters.AddWithValue("@code", InvitationCodeFormat.Normalize(displayCode));
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

    public void Dispose() => TempDatabase.Delete(Path);
}
