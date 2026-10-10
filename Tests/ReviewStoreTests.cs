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

    [Fact]
    public async Task A_bin_decision_keeps_the_volume_the_photo_was_on()
    {
        var onCard = Trash("card", 100);
        onCard.VolumeName = "1103-3d09";
        await _store.UpsertAsync(onCard);
        await _store.UpsertAsync(Trash("unknown", 50));

        var pending = (await _store.GetPendingTrashAsync()).ToDictionary(r => r.PhotoId);
        Assert.Equal("1103-3d09", pending["card"].VolumeName);
        Assert.Null(pending["unknown"].VolumeName);

        await _store.MarkCommittedAsync(["card"]);
        var committed = Assert.Single(await _store.GetCommittedTrashSinceAsync(DateTime.UtcNow.AddDays(-1)));
        Assert.Equal("1103-3d09", committed.VolumeName);
    }

    /// <summary>
    /// The reviews of an installed version have no volume column. Opening them adds it, empty,
    /// and loses nothing: these are the user's decisions, bin included.
    /// </summary>
    [Fact]
    public async Task A_database_from_before_volumes_were_kept_opens_with_its_decisions()
    {
        // The table as versions 1.0 to 3.0 created it.
        var old = new SQLiteConnection(_dbPath);
        old.Execute("""
            CREATE TABLE "reviews"(
                "PhotoId" varchar primary key not null,
                "MonthKey" varchar,
                "Decision" integer,
                "DecidedAt" bigint,
                "CommittedAt" bigint,
                "SizeBytes" bigint,
                "TargetAlbum" varchar)
            """);
        old.Execute("CREATE INDEX \"reviews_MonthKey\" ON \"reviews\"(\"MonthKey\")");
        var decidedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        old.Execute("INSERT INTO reviews VALUES (?, ?, ?, ?, NULL, ?, NULL)",
            "binned", "2026-07", (int)SwipeDecision.Trash, decidedAt.Ticks, 4321L);
        old.Execute("INSERT INTO reviews VALUES (?, ?, ?, ?, ?, ?, NULL)",
            "emptied", "2026-07", (int)SwipeDecision.Trash, decidedAt.Ticks, decidedAt.Ticks, 1000L);
        old.Execute("INSERT INTO reviews VALUES (?, ?, ?, ?, NULL, 0, ?)",
            "moved", "2026-06", (int)SwipeDecision.Moved, decidedAt.Ticks, "Trips");
        old.Close();

        var all = (await _store.GetAllAsync()).ToDictionary(r => r.PhotoId);
        Assert.Equal(["binned", "emptied", "moved"], all.Keys.Order());
        Assert.All(all.Values, record => Assert.Null(record.VolumeName));
        Assert.Equal(4321, all["binned"].SizeBytes);
        Assert.Equal(decidedAt, all["binned"].DecidedAt);
        Assert.Equal(decidedAt, all["emptied"].CommittedAt);
        Assert.Equal("Trips", all["moved"].TargetAlbum);
        Assert.Equal("binned", Assert.Single(await _store.GetPendingTrashAsync()).PhotoId);
        Assert.Equal(1000, await _store.GetFreedBytesAsync());

        var onCard = Trash("card");
        onCard.VolumeName = "1103-3d09";
        await _store.UpsertAsync(onCard);
        Assert.Equal("1103-3d09", (await _store.GetAllAsync()).Single(r => r.PhotoId == "card").VolumeName);
    }

    [Fact]
    public async Task One_decision_can_be_looked_up_as_it_stands()
    {
        await _store.UpsertAsync(Trash("binned", 10));
        await _store.UpsertAsync(Trash("emptied", 20));
        await _store.MarkCommittedAsync(["emptied"]);

        Assert.Null((await _store.FindAsync("binned"))!.CommittedAt);
        Assert.NotNull((await _store.FindAsync("emptied"))!.CommittedAt);
        Assert.Equal(SwipeDecision.Trash, (await _store.FindAsync("emptied"))!.Decision);
        Assert.Null(await _store.FindAsync("never-decided"));
    }

    [Fact]
    public async Task Forgetting_several_decisions_leaves_the_others()
    {
        await _store.UpsertAsync(Trash("a"));
        await _store.UpsertAsync(Trash("b"));
        await _store.UpsertAsync(Trash("c"));

        await _store.RemoveAllAsync(["a", "c", "never-there"]);

        Assert.Equal("b", Assert.Single(await _store.GetAllAsync()).PhotoId);
    }
}
