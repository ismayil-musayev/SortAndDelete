using SQLite;
using SortAndDelete.Models;
using SortAndDelete.Services;
using Xunit;

namespace SortAndDelete.Tests;

/// <summary>Exercises the real SQLite store against a temp database file.</summary>
public sealed class ReviewStoreTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sortanddelete_test_{Guid.NewGuid():N}.db3");
    private readonly ReviewStore _store;

    public ReviewStoreTests() => _store = new ReviewStore(_dbPath);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Closed first: sqlite-net keeps the connection pooled, and the file with it.
        await _store.CloseAsync();
        File.Delete(_dbPath);
    }

    private static ReviewRecord Trash(string id, long size = 0) => new()
    {
        PhotoId = id,
        MonthKey = "2026-07",
        Decision = SwipeDecision.Trash,
        DecidedAt = DateTime.UtcNow,
        SizeBytes = size,
    };

    [Fact]
    public async Task Upsert_then_read_roundtrips()
    {
        await _store.UpsertAsync(Trash("p1", 1234));

        var all = await _store.GetAllAsync();
        var record = Assert.Single(all);
        Assert.Equal("p1", record.PhotoId);
        Assert.Equal(1234, record.SizeBytes);
        Assert.Null(record.CommittedAt);
    }

    [Fact]
    public async Task Upsert_same_photo_replaces_the_decision()
    {
        await _store.UpsertAsync(Trash("p1"));
        await _store.UpsertAsync(new ReviewRecord
        {
            PhotoId = "p1",
            MonthKey = "2026-07",
            Decision = SwipeDecision.Keep,
            DecidedAt = DateTime.UtcNow,
        });

        var record = Assert.Single(await _store.GetAllAsync());
        Assert.Equal(SwipeDecision.Keep, record.Decision);
    }

    [Fact]
    public async Task Pending_trash_excludes_committed_and_kept()
    {
        await _store.UpsertAsync(Trash("pending"));
        await _store.UpsertAsync(Trash("committed"));
        await _store.UpsertAsync(new ReviewRecord
        {
            PhotoId = "kept",
            Decision = SwipeDecision.Keep,
            DecidedAt = DateTime.UtcNow,
        });
        await _store.MarkCommittedAsync(["committed"]);

        var pending = await _store.GetPendingTrashAsync();

        Assert.Equal("pending", Assert.Single(pending).PhotoId);
    }

    [Fact]
    public async Task Remove_makes_a_photo_unreviewed_again_undo_and_restore()
    {
        await _store.UpsertAsync(Trash("p1"));
        await _store.RemoveAsync("p1");

        Assert.Empty(await _store.GetAllAsync());
    }

    [Fact]
    public async Task Freed_bytes_counts_only_committed_trash()
    {
        await _store.UpsertAsync(Trash("a", 100));
        await _store.UpsertAsync(Trash("b", 200));
        await _store.MarkCommittedAsync(["a"]);

        Assert.Equal(100, await _store.GetFreedBytesAsync());
    }

    [Fact]
    public async Task Committed_since_returns_recent_commits_only()
    {
        await _store.UpsertAsync(Trash("recent"));
        await _store.MarkCommittedAsync(["recent"]);

        var recent = await _store.GetCommittedTrashSinceAsync(DateTime.UtcNow.AddDays(-30));
        Assert.Equal("recent", Assert.Single(recent).PhotoId);

        var none = await _store.GetCommittedTrashSinceAsync(DateTime.UtcNow.AddMinutes(5));
        Assert.Empty(none);
    }

    [Fact]
    public async Task Clearing_keep_decisions_enables_re_review_but_keeps_the_bin()
    {
        await _store.UpsertAsync(new ReviewRecord
        {
            PhotoId = "kept",
            MonthKey = "2026-07",
            Decision = SwipeDecision.Keep,
            DecidedAt = DateTime.UtcNow,
        });
        await _store.UpsertAsync(new ReviewRecord
        {
            PhotoId = "moved",
            MonthKey = "2026-07",
            Decision = SwipeDecision.Moved,
            DecidedAt = DateTime.UtcNow,
        });
        await _store.UpsertAsync(Trash("binned"));                    // 2026-07, in-app bin
        await _store.UpsertAsync(new ReviewRecord
        {
            PhotoId = "other_month",
            MonthKey = "2026-06",
            Decision = SwipeDecision.Keep,
            DecidedAt = DateTime.UtcNow,
        });

        int cleared = await _store.ClearKeepDecisionsForMonthAsync("2026-07");

        Assert.Equal(2, cleared); // kept + moved only
        var remaining = (await _store.GetAllAsync()).Select(r => r.PhotoId).Order().ToList();
        Assert.Equal(["binned", "other_month"], remaining);
    }

    /// <summary>
    /// Opening creates the table, so something like a full disk can make it fail. That mustn't
    /// stick for the life of the app, failing every decision made from then on: the next call
    /// has to try again.
    /// </summary>
    [Fact]
    public async Task A_store_that_failed_to_open_tries_again_on_the_next_call()
    {
        // Stands in for a full disk: SQLite can't open a folder as its file.
        Directory.CreateDirectory(_dbPath);

        await Assert.ThrowsAsync<SQLiteException>(() => _store.UpsertAsync(Trash("p1")));

        Directory.Delete(_dbPath);
        await _store.UpsertAsync(Trash("p1"));
        Assert.Equal("p1", Assert.Single(await _store.GetAllAsync()).PhotoId);
    }

    /// <summary>
    /// Closing lets go of the file, and the store opens it again when next needed, with what it
    /// held. Should the file have gone meanwhile, it starts a new one.
    /// </summary>
    [Fact]
    public async Task A_closed_store_opens_again_on_the_next_call()
    {
        await _store.UpsertAsync(Trash("p1"));

        await _store.CloseAsync();

        Assert.Equal("p1", Assert.Single(await _store.GetAllAsync()).PhotoId);

        await _store.CloseAsync();
        File.Delete(_dbPath); // Windows refuses this while a connection still holds the file

        Assert.Empty(await _store.GetAllAsync());
    }
}
