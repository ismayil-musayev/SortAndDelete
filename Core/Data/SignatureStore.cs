using SQLite;
using SortAndDelete.Models;

namespace SortAndDelete.Services;

/// <summary>
/// Cache of everything the duplicate scan derived from the library.
///
/// It lives in its own database file, apart from the review decisions: this one is pure
/// derived data, so deleting it costs nothing but a rescan, while losing the review store
/// would lose the user's actual work.
/// </summary>
public sealed class SignatureStore
{
    private readonly SQLiteAsyncConnection _db;
    private Task? _init;

    public SignatureStore(string dbPath)
    {
        _db = new SQLiteAsyncConnection(dbPath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
    }

    private Task InitAsync()
    {
        // Opening writes now (the migration), so a failure such as a full disk is retried, not kept.
        if (_init is { IsFaulted: true })
            _init = null;
        return _init ??= InitCoreAsync();
    }

    private async Task InitCoreAsync()
    {
        // Also the migration: a column a record has gained is added to the existing table. Old
        // rows hold NULL in it, which sqlite-net reads back as 0. It can't be [NotNull]: SQLite
        // won't add a NOT NULL column without a default.
        await _db.CreateTableAsync<SignatureRecord>();
        await _db.CreateTableAsync<ContentHashRecord>();
    }

    /// <summary>
    /// Closes the database and lets go of its file, which sqlite-net otherwise keeps open for
    /// the life of the process. The next call opens it again.
    /// </summary>
    /// <remarks>
    /// Here for the tests, which delete the file when they are done. The app never calls it: its
    /// store stays open for the life of the process. It must not be called while other calls are
    /// in flight, which would find the connection closed under them.
    /// </remarks>
    public async Task CloseAsync()
    {
        _init = null;
        await _db.CloseAsync();
    }

    /// <summary>Every cached fingerprint, grouped by photo and ordered by frame.</summary>
    public async Task<Dictionary<string, List<SignatureRecord>>> GetSignaturesAsync()
    {
        await InitAsync();
        var rows = await _db.Table<SignatureRecord>().ToListAsync();

        var byPhoto = new Dictionary<string, List<SignatureRecord>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!byPhoto.TryGetValue(row.PhotoId, out var list))
                byPhoto[row.PhotoId] = list = [];
            list.Add(row);
        }

        foreach (var list in byPhoto.Values)
            list.Sort((a, b) => a.FrameIndex.CompareTo(b.FrameIndex));

        return byPhoto;
    }

    public async Task SaveSignaturesAsync(IReadOnlyCollection<SignatureRecord> records)
    {
        if (records.Count == 0)
            return;

        await InitAsync();
        await _db.RunInTransactionAsync(connection =>
        {
            foreach (var record in records)
                connection.InsertOrReplace(record);
        });
    }

    public async Task<Dictionary<string, ContentHashRecord>> GetContentHashesAsync()
    {
        await InitAsync();
        var rows = await _db.Table<ContentHashRecord>().ToListAsync();
        return rows.ToDictionary(r => r.PhotoId, StringComparer.Ordinal);
    }

    public async Task SaveContentHashesAsync(IReadOnlyCollection<ContentHashRecord> records)
    {
        if (records.Count == 0)
            return;

        await InitAsync();
        await _db.RunInTransactionAsync(connection =>
        {
            foreach (var record in records)
                connection.InsertOrReplace(record);
        });
    }

    /// <summary>Drops fingerprints for items no longer in the library, so the cache tracks it.</summary>
    public async Task ForgetMissingAsync(IReadOnlySet<string> livePhotoIds)
    {
        await InitAsync();

        var stale = (await _db.Table<SignatureRecord>().ToListAsync())
            .Select(r => r.PhotoId)
            .Concat((await _db.Table<ContentHashRecord>().ToListAsync()).Select(r => r.PhotoId))
            .Distinct(StringComparer.Ordinal)
            .Where(id => !livePhotoIds.Contains(id))
            .ToList();

        if (stale.Count == 0)
            return;

        await _db.RunInTransactionAsync(connection =>
        {
            foreach (var id in stale)
            {
                connection.Execute("DELETE FROM signatures WHERE PhotoId = ?", id);
                connection.Execute("DELETE FROM content_hashes WHERE PhotoId = ?", id);
            }
        });
    }

    /// <summary>Throws the cache away so the next scan starts from nothing.</summary>
    public async Task ClearAsync()
    {
        await InitAsync();
        await _db.ExecuteAsync("DELETE FROM signatures");
        await _db.ExecuteAsync("DELETE FROM content_hashes");
    }
}
