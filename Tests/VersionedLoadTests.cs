using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class VersionedLoadTests
{
    /// <summary>
    /// Stands in for the UI thread the cache is used on: what is posted to it runs on the test's
    /// thread, in order, when <see cref="RunPending"/> is called.
    /// </summary>
    private sealed class UiThread : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public UiThread() => SetSynchronizationContext(this);

        public override void Post(SendOrPostCallback d, object? state) => _posted.Enqueue((d, state));

        public void RunPending()
        {
            while (_posted.TryDequeue(out var posted))
                posted.Callback(posted.State);
        }

        public void Dispose() => SetSynchronizationContext(_previous);
    }

    /// <summary>
    /// Loads that end when the test says so, numbered from 1 in the order they start. The reads
    /// waiting for a load go on, on the test's thread, before the test does.
    /// </summary>
    private sealed class Loads : IDisposable
    {
        private readonly UiThread _ui = new();
        private readonly List<TaskCompletionSource<string>> _started = [];

        public int Started => _started.Count;

        public Task<string> Load()
        {
            var load = new TaskCompletionSource<string>();
            _started.Add(load);
            return load.Task;
        }

        /// <summary>Ends load <paramref name="number"/> with the value "load {number}".</summary>
        public void Finish(int number)
        {
            _started[number - 1].SetResult($"load {number}");
            _ui.RunPending();
        }

        public void Fail(int number, Exception error)
        {
            _started[number - 1].SetException(error);
            _ui.RunPending();
        }

        public void Dispose() => _ui.Dispose();
    }

    /// <summary>The value of a read that has ended.</summary>
    private static string ValueOf(Task<string> read)
    {
        Assert.True(read.IsCompletedSuccessfully, $"The read hasn't ended: {read.Status}");
        return read.Result;
    }

    [Fact]
    public void A_value_is_kept_for_later_reads_until_it_is_marked_stale()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        var first = cache.GetAsync();
        loads.Finish(1);

        Assert.Equal("load 1", ValueOf(first));
        Assert.Equal("load 1", ValueOf(cache.GetAsync()));
        Assert.Equal(1, loads.Started);

        cache.MarkStale();
        var next = cache.GetAsync();

        Assert.Equal("load 1", cache.Value); // answers until the next load ends
        Assert.Equal(2, loads.Started);
        loads.Finish(2);
        Assert.Equal("load 2", ValueOf(next));
        Assert.Equal("load 2", cache.Value);
    }

    [Fact]
    public void A_load_says_when_it_began()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        Assert.Null(cache.LoadStartedAt);
        var before = ChangeDebounce.Now;

        _ = cache.GetAsync();

        Assert.InRange(cache.LoadStartedAt!.Value, before, ChangeDebounce.Now);
    }

    [Fact]
    public void Reads_at_the_same_time_share_one_load()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);

        var reads = Enumerable.Range(0, 5).Select(_ => cache.GetAsync()).ToList();
        loads.Finish(1);

        Assert.Equal(1, loads.Started);
        Assert.All(reads, read => Assert.Equal("load 1", ValueOf(read)));
    }

    [Fact]
    public void A_mark_during_a_load_does_not_hold_up_a_read_that_began_before_it()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        var read = cache.GetAsync();

        cache.MarkStale();
        loads.Finish(1);

        Assert.Equal("load 1", ValueOf(read));
        Assert.Equal(1, loads.Started);
    }

    [Fact]
    public void A_read_that_began_after_a_mark_gets_a_load_that_began_after_it()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        var before = cache.GetAsync();

        cache.MarkStale();
        var after = cache.GetAsync();
        loads.Finish(1);

        Assert.Equal("load 1", ValueOf(before));
        Assert.False(after.IsCompleted);
        Assert.Equal(2, loads.Started);
        loads.Finish(2);
        Assert.Equal("load 2", ValueOf(after));
    }

    [Fact]
    public void An_invalidation_during_a_load_gives_the_next_read_fresh_data()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        var before = cache.GetAsync();

        cache.Invalidate();
        var after = cache.GetAsync();
        loads.Finish(1);

        Assert.Equal("load 1", ValueOf(before)); // it began before the invalidation
        Assert.Null(cache.Value); // a load that began before it doesn't bring the old value back
        Assert.False(after.IsCompleted);
        loads.Finish(2);
        Assert.Equal("load 2", ValueOf(after));
        Assert.Equal("load 2", cache.Value);
    }

    /// <summary>
    /// What the bin counts on after a trash request that failed as a whole: the read that follows
    /// its invalidation tells what the request still removed, so it must not be the value kept.
    /// </summary>
    [Fact]
    public void An_invalidation_drops_the_value_kept_and_the_next_read_waits_for_a_new_load()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        _ = cache.GetAsync();
        loads.Finish(1);

        cache.Invalidate();
        var after = cache.GetAsync();

        Assert.Null(cache.Value);
        Assert.False(after.IsCompleted);
        Assert.Equal(2, loads.Started);
        loads.Finish(2);
        Assert.Equal("load 2", ValueOf(after));
    }

    [Fact]
    public void A_failed_load_throws_to_every_read_waiting_for_it_and_the_next_read_loads_again()
    {
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        var first = cache.GetAsync();
        var second = cache.GetAsync();

        loads.Fail(1, new IOException("The query failed."));

        Assert.IsType<IOException>(first.Exception?.InnerException);
        Assert.IsType<IOException>(second.Exception?.InnerException);
        var retry = cache.GetAsync();
        Assert.Equal(2, loads.Started);
        loads.Finish(2);
        Assert.Equal("load 2", ValueOf(retry));
    }

    [Fact]
    public void Marks_that_keep_coming_during_loads_still_end_every_read_within_two_loads()
    {
        // Loads take 4 s, and a chat app saves a photo every 2 s: twice per load, a burst marks
        // the library stale and queues a reload.
        using var loads = new Loads();
        var cache = new VersionedLoad<string>(loads.Load);
        var reads = new List<(Task<string> Read, int LoadsEndedBefore)>();

        for (int ended = 0; ended < 10; ended++)
        {
            for (int burst = 0; burst < 2; burst++)
            {
                cache.MarkStale();
                reads.Add((cache.GetAsync(), ended));
            }

            loads.Finish(ended + 1);

            Assert.All(reads.Where(read => read.LoadsEndedBefore + 2 <= ended + 1),
                read => Assert.True(read.Read.IsCompletedSuccessfully));
        }

        Assert.Equal(11, loads.Started); // one more runs for the last reads
    }
}
