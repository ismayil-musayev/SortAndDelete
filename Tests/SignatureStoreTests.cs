using SQLite;
using SortAndDelete.Models;
using SortAndDelete.Services;
using Xunit;

namespace SortAndDelete.Tests;

/// <summary>Exercises the real fingerprint cache against a temp database file.</summary>
public sealed class SignatureStoreTests : IDisposable
{
    private static readonly DateTime Taken = new(2026, 7, 1, 12, 0, 0);

    /// <summary>The file's stamp, as Android's DATE_MODIFIED gives it.</summary>
    private static readonly DateTime Written = new(2026, 7, 1, 12, 0, 5, DateTimeKind.Utc);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sortanddelete_test_{Guid.NewGuid():N}.db3");

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // best-effort: sqlite-net's pooled connection holds the file until the test run ends
        }
    }

    /// <summary><see cref="SignatureRecord"/> as Android 3.0 shipped it, before the modification time.</summary>
    [Table("signatures")]
    private sealed class SignatureRecordIn30
    {
        [PrimaryKey]
        public string Key { get; set; } = "";

        [Indexed]
        public string PhotoId { get; set; } = "";

        public int FrameIndex { get; set; }
        public long SizeBytes { get; set; }
        public long TakenAtTicks { get; set; }
        public long DHash { get; set; }
        public long DHash90 { get; set; }
        public long DHash180 { get; set; }
        public long DHash270 { get; set; }
        public long PHash { get; set; }
        public long PHash90 { get; set; }
        public long PHash180 { get; set; }
        public long PHash270 { get; set; }
        public byte[] Histogram { get; set; } = [];
        public double Detail { get; set; }
    }

    /// <summary><see cref="ContentHashRecord"/> as Android 3.0 shipped it.</summary>
    [Table("content_hashes")]
    private sealed class ContentHashRecordIn30
    {
        [PrimaryKey]
        public string PhotoId { get; set; } = "";

        public long SizeBytes { get; set; }

        public string Hash { get; set; } = "";
    }

    private static PhotoAsset Photo(DateTime modifiedAt = default, DateTime fileModifiedAt = default) => new()
    {
        Id = "42",
        SizeBytes = 5_000_000,
        TakenAt = Taken,
        ModifiedAt = modifiedAt,
        FileModifiedAt = fileModifiedAt,
    };

    private static ImageSignature Signature() => new()
    {
        DHash = 0x1234,
        DHash90 = 0,
        DHash180 = 0,
        DHash270 = 0,
        PHash = 0,
        PHash90 = 0,
        PHash180 = 0,
        PHash270 = 0,
        Histogram = [.. Enumerable.Repeat((byte)4, 64)],
        Detail = 37.25,
    };

    /// <summary>
    /// The cache as Android 3.0 left it: each table created by sqlite-net from the record as 3.0
    /// declared it, just as the app did, with a row in each for <see cref="Photo"/>.
    /// </summary>
    private void CreateCacheAs30LeftIt()
    {
        using var connection = new SQLiteConnection(_dbPath);
        connection.CreateTable<SignatureRecordIn30>();
        connection.CreateTable<ContentHashRecordIn30>();

        connection.Insert(new SignatureRecordIn30
        {
            Key = "42",
            PhotoId = "42",
            SizeBytes = 5_000_000,
            TakenAtTicks = Taken.Ticks,
            DHash = 0x1234,
            Histogram = [.. Enumerable.Repeat((byte)4, 64)],
            Detail = 37.25,
        });
        connection.Insert(new ContentHashRecordIn30 { PhotoId = "42", SizeBytes = 5_000_000, Hash = "ABC" });
    }

    private List<string> ColumnsOf(string table)
    {
        using var connection = new SQLiteConnection(_dbPath);
        return [.. connection.GetTableInfo(table).Select(column => column.Name)];
    }

    /// <summary>
    /// Opening a 3.0 cache has to add the fingerprints' column without losing a row, and those
    /// rows have to read back as untracked so they stay current for an Android photo. Otherwise
    /// every Android user would sit through a full rescan after updating.
    /// </summary>
    [Fact]
    public async Task Rows_cached_before_the_upgrade_stay_current_for_an_untracked_photo()
    {
        CreateCacheAs30LeftIt();
        var store = new SignatureStore(_dbPath);

        var signature = Assert.Single((await store.GetSignaturesAsync())["42"]);

        // Added rather than just missing, so the zero below is the old row's NULL read back.
        Assert.Contains(nameof(SignatureRecord.ModifiedAtTicks), ColumnsOf("signatures"));
        Assert.Equal(0, signature.ModifiedAtTicks);
        Assert.True(signature.IsCurrentFor(Photo()));
        Assert.Equal(0x1234UL, signature.ToSignature().DHash);
    }

    /// <summary>
    /// The content hashes gain the file's stamp the same way, and a row hashed before then
    /// reads back unstamped and current, whatever stamp the file has now. Otherwise every
    /// Android user would re-read each file that shares a size with another after updating.
    /// </summary>
    [Fact]
    public async Task Hashes_cached_before_the_upgrade_read_back_unstamped_and_current()
    {
        CreateCacheAs30LeftIt();
        var store = new SignatureStore(_dbPath);

        var hash = (await store.GetContentHashesAsync())["42"];

        // Added rather than just missing, so the zero below is the old row's NULL read back.
        Assert.Contains(nameof(ContentHashRecord.FileModifiedAtTicks), ColumnsOf("content_hashes"));
        Assert.Equal(0, hash.FileModifiedAtTicks);
        Assert.Equal("42", hash.PhotoId);
        Assert.Equal(5_000_000, hash.SizeBytes);
        Assert.Equal("ABC", hash.Hash);
        Assert.True(hash.IsCurrentFor(Photo()));
        Assert.True(hash.IsCurrentFor(Photo(fileModifiedAt: Written)));
    }

    [Fact]
    public async Task An_upgraded_cache_keeps_the_stamp_of_new_hashes()
    {
        CreateCacheAs30LeftIt();
        var store = new SignatureStore(_dbPath);
        var stamped = Photo(fileModifiedAt: Written);

        await store.SaveContentHashesAsync([ContentHashRecord.From(stamped, "DEF")]);

        var hash = (await store.GetContentHashesAsync())["42"];
        Assert.Equal(Written.Ticks, hash.FileModifiedAtTicks);
        Assert.Equal("DEF", hash.Hash);
        Assert.True(hash.IsCurrentFor(stamped));
        Assert.False(hash.IsCurrentFor(Photo(fileModifiedAt: Written.AddMinutes(1))));
    }

    [Fact]
    public async Task An_upgraded_cache_keeps_the_modification_time_of_new_fingerprints()
    {
        CreateCacheAs30LeftIt();
        var store = new SignatureStore(_dbPath);
        var tracked = Photo(modifiedAt: new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc));

        await store.SaveSignaturesAsync([SignatureRecord.From("42", 0, tracked, Signature())]);

        var signature = Assert.Single((await store.GetSignaturesAsync())["42"]);
        Assert.Equal(tracked.ModifiedAt.Ticks, signature.ModifiedAtTicks);
        Assert.True(signature.IsCurrentFor(tracked));
    }

    /// <summary>
    /// Opening writes now that it migrates, so something like a full disk can make it fail.
    /// That mustn't stick for the life of the app: the next call has to try again.
    /// </summary>
    [Fact]
    public async Task A_cache_that_failed_to_open_tries_again_on_the_next_call()
    {
        // Stands in for a full disk: SQLite can't open a folder as its file.
        Directory.CreateDirectory(_dbPath);
        var store = new SignatureStore(_dbPath);

        await Assert.ThrowsAsync<SQLiteException>(() => store.GetSignaturesAsync());

        Directory.Delete(_dbPath);
        Assert.Empty(await store.GetSignaturesAsync());
    }
}
