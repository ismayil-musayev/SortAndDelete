using SQLite;
using SortAndDelete.Models;

namespace SortAndDelete.Services;

/// <summary>SQLite-backed store of review decisions. This is what survives app restarts.</summary>
public sealed class ReviewStore
{
    private readonly SQLiteAsyncConnection _db;
    private Task? _init;

    public ReviewStore(string dbPath)
    {
        _db = new SQLiteAsyncConnection(dbPath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
    }

    private Task InitAsync() => _init ??= _db.CreateTableAsync<ReviewRecord>();

    public async Task<List<ReviewRecord>> GetAllAsync()
    {
        await InitAsync();
        return await _db.Table<ReviewRecord>().ToListAsync();
    }

    // Raw SQL on purpose: sqlite-net's LINQ-to-SQL translation of "enum == constant &&
    // nullable == null" breaks silently under Release AOT on Android (returns no rows).

    /// <summary>Photos in the in-app bin: marked for deletion but not yet moved to the system trash.</summary>
    public async Task<List<ReviewRecord>> GetPendingTrashAsync()
    {
        await InitAsync();
        return await _db.QueryAsync<ReviewRecord>(
            "SELECT * FROM reviews WHERE Decision = ? AND CommittedAt IS NULL",
            (int)SwipeDecision.Trash);
    }

    /// <summary>Photos already sent to the system trash after <paramref name="cutoffUtc"/> (still restorable there).</summary>
    public async Task<List<ReviewRecord>> GetCommittedTrashSinceAsync(DateTime cutoffUtc)
    {
        await InitAsync();
        return await _db.QueryAsync<ReviewRecord>(
            "SELECT * FROM reviews WHERE Decision = ? AND CommittedAt IS NOT NULL AND CommittedAt > ?",
            (int)SwipeDecision.Trash, cutoffUtc);
    }

    public async Task UpsertAsync(ReviewRecord record)
    {
        await InitAsync();
        await _db.InsertOrReplaceAsync(record);
    }

    /// <summary>Removes a decision — the photo becomes "unreviewed" again (used by undo and restore).</summary>
    public async Task RemoveAsync(string photoId)
    {
        await InitAsync();
        await _db.DeleteAsync<ReviewRecord>(photoId);
    }

    /// <summary>
    /// Re-review support: forgets Keep/Moved decisions for a month so its photos come back
    /// into the deck. Trash decisions are untouched — the bin and the system trash stay intact.
    /// </summary>
    public async Task<int> ClearKeepDecisionsForMonthAsync(string monthKey)
    {
        await InitAsync();
        return await _db.ExecuteAsync(
            "DELETE FROM reviews WHERE MonthKey = ? AND Decision IN (?, ?)",
            monthKey, (int)SwipeDecision.Keep, (int)SwipeDecision.Moved);
    }

    public async Task MarkCommittedAsync(IReadOnlyCollection<string> photoIds)
    {
        await InitAsync();
        var now = DateTime.UtcNow;
        await _db.RunInTransactionAsync(conn =>
        {
            foreach (var id in photoIds)
                conn.Execute("UPDATE reviews SET CommittedAt = ? WHERE PhotoId = ?", now, id);
        });
    }

    /// <summary>Total bytes of photos that were actually sent to the system trash.</summary>
    public async Task<long> GetFreedBytesAsync()
    {
        await InitAsync();
        return await _db.ExecuteScalarAsync<long>(
            "SELECT IFNULL(SUM(SizeBytes), 0) FROM reviews WHERE Decision = ? AND CommittedAt IS NOT NULL",
            (int)SwipeDecision.Trash);
    }
}
