using Gt5pOnlineServer.Data;

namespace Gt5pOnlineServer.Services;

// Replay bytes are already encrypted by the game. Preserve them without conversion.
// Include the score in the storage name so a faster lap cannot expose an older replay.
public sealed class ReplayStore(string directory)
{
    private string PathFor(RankingEntry row) => Path.Combine(directory, $"{row.Id}-{row.TimeMs}.dat");
    public string? Find(RankingEntry row) => File.Exists(PathFor(row)) ? PathFor(row) : null;
    public async Task SaveAsync(RankingEntry row, byte[] data)
    {
        Directory.CreateDirectory(directory);
        var target = PathFor(row);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<bool> DuplicateAsync(RankingEntry source, RankingEntry target)
    {
        var path = Find(source);
        if (path is null || source.TimeMs != target.TimeMs) return false;
        if (source.Id != target.Id) await SaveAsync(target, await File.ReadAllBytesAsync(path));
        return true;
    }
}
