namespace SortAndDelete.Logic;

/// <summary>
/// Lets a screen's loads run one at a time. One asked for while another is running waits it out
/// and then looks for itself, and one whose visit has ended by then never starts.
/// </summary>
/// <remarks>
/// Not thread-safe: a screen asks for its loads on the UI thread, which is also where one that
/// waited carries on.
/// </remarks>
public sealed class LoadGate
{
    private Task _running = Task.CompletedTask;

    /// <summary>Whether a load is under way.</summary>
    public bool IsRunning => !_running.IsCompleted;

    /// <summary>
    /// Runs <paramref name="load"/> once nothing else is running, unless
    /// <paramref name="visit"/> has been cancelled by then. A failure of the load is the caller's
    /// own; one in a load it only waited for is not.
    /// </summary>
    public async Task RunAsync(Func<Task> load, CancellationToken visit)
    {
        // One still running may be an ended visit's, saving what it read, or this visit's own:
        // wait it out and look again, as another that was waiting may have started meanwhile.
        while (!_running.IsCompleted)
        {
            await _running.ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        if (visit.IsCancellationRequested)
            return;

        _running = load();
        await _running;
    }
}
