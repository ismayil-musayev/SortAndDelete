using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

/// <summary>
/// The duplicates screen's loads as its page and view model ask for them: each OnAppearing
/// enters a visit and loads under it, each OnDisappearing leaves.
/// </summary>
public class LoadGateTests
{
    /// <summary>
    /// Stands in for the UI thread, where the screen asks for its loads and where one that waited
    /// carries on: what is posted to it runs later, one thing after another, in the order posted.
    /// Left to xunit's own context the waiting loads would carry on side by side on pool threads.
    /// </summary>
    private sealed class UiThread : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly Queue<Action> _posted = new();

        public UiThread() => SetSynchronizationContext(this);

        public void Dispose() => SetSynchronizationContext(_previous);

        public override void Post(SendOrPostCallback d, object? state) => _posted.Enqueue(() => d(state));

        /// <summary>Does <paramref name="something"/>, then everything it left posted.</summary>
        public void Do(Action something)
        {
            something();
            while (_posted.TryDequeue(out var work))
                work();
        }
    }

    private int _started;
    private int _running;
    private int _mostAtOnce;

    /// <summary>A load that runs until <paramref name="until"/> is done.</summary>
    private Func<Task> Load(Task until) => async () =>
    {
        _started++;
        _mostAtOnce = Math.Max(_mostAtOnce, ++_running);
        try
        {
            await until;
        }
        finally
        {
            _running--;
        }
    };

    /// <summary>A check that finds the groups still hold, and so is over at once.</summary>
    private Func<Task> Check() => Load(Task.CompletedTask);

    /// <summary>A scan, which runs until its visit ends.</summary>
    private Func<Task> ScanUnder(CancellationToken visit)
    {
        var stopped = new TaskCompletionSource();
        visit.Register(stopped.SetResult);
        return Load(stopped.Task);
    }

    [Fact]
    public void A_load_asked_for_while_another_runs_waits_it_out_and_then_runs()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();
        var scan = new TaskCompletionSource();

        var first = gate.RunAsync(Load(scan.Task), CancellationToken.None);
        var second = gate.RunAsync(Check(), CancellationToken.None);

        Assert.True(gate.IsRunning);
        Assert.Equal(1, _started);
        Assert.False(second.IsCompleted);

        ui.Do(scan.SetResult);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.False(gate.IsRunning);
        Assert.Equal(2, _started);
        Assert.Equal(1, _mostAtOnce);
    }

    /// <summary>
    /// Why a load looks again after waiting: the one it waited for is over, but another that
    /// was waiting beside it may have started first.
    /// </summary>
    [Fact]
    public void Loads_that_waited_together_still_run_one_at_a_time()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();
        var scan = new TaskCompletionSource();
        var next = new TaskCompletionSource();

        var first = gate.RunAsync(Load(scan.Task), CancellationToken.None);
        var second = gate.RunAsync(Load(next.Task), CancellationToken.None);
        var third = gate.RunAsync(Check(), CancellationToken.None);

        ui.Do(scan.SetResult);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal(2, _started);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        ui.Do(next.SetResult);

        Assert.True(second.IsCompletedSuccessfully);
        Assert.True(third.IsCompletedSuccessfully);
        Assert.Equal(3, _started);
        Assert.Equal(1, _mostAtOnce);
    }

    [Fact]
    public void A_load_for_a_visit_already_over_never_starts()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();

        var load = gate.RunAsync(Check(), new CancellationToken(canceled: true));

        Assert.True(load.IsCompletedSuccessfully);
        Assert.Equal(0, _started);
    }

    /// <summary>
    /// Leave, return, leave, all while the first visit's scan is still saving what it read. The
    /// second visit was waiting for that and has ended too, so it must not start a scan that
    /// nothing is left to stop.
    /// </summary>
    [Fact]
    public void A_visit_that_ended_while_it_waited_for_the_last_one_s_scan_starts_none_of_its_own()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();
        var page = new VisitScope();
        var saving = new TaskCompletionSource();

        var first = gate.RunAsync(Load(saving.Task), page.Enter());
        ui.Do(page.Leave);
        var second = gate.RunAsync(ScanUnder(page.Enter()), page.Current);
        ui.Do(page.Leave);

        Assert.False(second.IsCompleted);

        ui.Do(saving.SetResult);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, _started);
        Assert.False(gate.IsRunning);
    }

    /// <summary>The same, but the user stays the second time: their scan follows the first one's end.</summary>
    [Fact]
    public void A_visit_that_came_back_during_the_last_one_s_scan_scans_once_that_has_stopped()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();
        var page = new VisitScope();
        var saving = new TaskCompletionSource();

        var first = gate.RunAsync(Load(saving.Task), page.Enter());
        ui.Do(page.Leave);
        var second = gate.RunAsync(ScanUnder(page.Enter()), page.Current);

        Assert.Equal(1, _started);

        ui.Do(saving.SetResult);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal(2, _started);
        Assert.Equal(1, _mostAtOnce);
        Assert.False(second.IsCompleted);

        ui.Do(page.Leave);

        Assert.True(second.IsCompletedSuccessfully);
    }

    /// <summary>
    /// OnAppearing twice with no OnDisappearing between. The second rejoins the visit, so the
    /// one leave that follows still stops the scan the first started, and its own load waits
    /// behind that scan instead of running beside it.
    /// </summary>
    [Fact]
    public void A_repeat_appearing_on_one_page_shares_the_visit_and_waits_its_turn()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();
        var page = new VisitScope();

        var visit = page.Enter();
        var first = gate.RunAsync(ScanUnder(visit), visit);
        var again = page.Enter();
        var second = gate.RunAsync(Check(), again);

        Assert.Equal(visit, again);
        Assert.Equal(1, _started);
        Assert.False(second.IsCompleted);

        ui.Do(page.Leave);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, _started); // the scan stopped, and the repeat's visit was the same one
    }

    /// <summary>
    /// A new page's OnAppearing can arrive before the old page's OnDisappearing. The old page
    /// leaving then stops its own scan and nothing of the new page's, whose load starts once that
    /// scan is out of the way.
    /// </summary>
    [Fact]
    public void A_new_page_appearing_before_the_old_one_disappears_loads_after_the_old_scan_stops()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();
        var oldPage = new VisitScope();
        var newPage = new VisitScope();

        var oldVisit = oldPage.Enter();
        var first = gate.RunAsync(ScanUnder(oldVisit), oldVisit);
        var newVisit = newPage.Enter();
        var second = gate.RunAsync(ScanUnder(newVisit), newVisit);

        Assert.Equal(1, _started);

        ui.Do(oldPage.Leave);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(newVisit.IsCancellationRequested);
        Assert.True(newPage.IsLive);
        Assert.Equal(2, _started);
        Assert.Equal(1, _mostAtOnce);
        Assert.False(second.IsCompleted); // the new page's scan, running under its own visit

        ui.Do(newPage.Leave);

        Assert.True(second.IsCompletedSuccessfully);
    }

    /// <summary>
    /// The screen's own loads never throw, but the gate doesn't lean on that: a failure belongs
    /// to whoever asked for that load, and the next in line still gets its turn.
    /// </summary>
    [Fact]
    public void A_load_that_failed_doesn_t_fail_the_one_waiting_behind_it()
    {
        using var ui = new UiThread();
        var gate = new LoadGate();
        var scan = new TaskCompletionSource();

        var first = gate.RunAsync(Load(scan.Task), CancellationToken.None);
        var second = gate.RunAsync(Check(), CancellationToken.None);

        ui.Do(() => scan.SetException(new IOException("disk full")));

        Assert.True(first.IsFaulted);
        Assert.IsType<IOException>(first.Exception?.InnerException);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(2, _started);
        Assert.False(gate.IsRunning);
    }
}
