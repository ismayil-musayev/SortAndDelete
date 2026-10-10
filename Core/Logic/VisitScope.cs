namespace SortAndDelete.Logic;

/// <summary>
/// A page's visits, each with a cancellation of its own, so leaving stops only what that visit
/// started, in whatever order an old page's leaving and a new one's entering arrive.
/// </summary>
public sealed class VisitScope
{
    private CancellationTokenSource? _visit;

    /// <summary>Whether the page is on a visit: entered, and not left since.</summary>
    public bool IsLive => _visit is { IsCancellationRequested: false };

    /// <summary>The visit the page is on. Already cancelled when it is on none.</summary>
    public CancellationToken Current => _visit?.Token ?? new CancellationToken(canceled: true);

    /// <summary>
    /// Starts a visit, or rejoins the one the page is still on: replaced, that visit's work would
    /// be left with nothing to stop it.
    /// </summary>
    public CancellationToken Enter()
    {
        if (_visit is null || _visit.IsCancellationRequested)
            _visit = new CancellationTokenSource();
        return _visit.Token;
    }

    /// <summary>
    /// Ends the visit, cancelling what it started. Not disposed: that work can still be winding
    /// down on it.
    /// </summary>
    public void Leave() => _visit?.Cancel();
}
