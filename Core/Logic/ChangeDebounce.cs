using System.Diagnostics;

namespace SortAndDelete.Logic;

/// <summary>
/// Gathers the library's change notifications into bursts, so that a burst is acted on once: a
/// quiet period after its last notification, or a longest wait after its first while they keep
/// coming. Moments are readings of <see cref="Now"/>.
/// </summary>
public sealed class ChangeDebounce(TimeSpan quietPeriod, TimeSpan longestWait)
{
    private TimeSpan? _first;
    private TimeSpan _last;

    /// <summary>A monotonic clock, counting from an arbitrary moment.</summary>
    public static TimeSpan Now => Stopwatch.GetElapsedTime(0);

    /// <summary>When the burst not yet taken is due, null when there is none.</summary>
    public TimeSpan? DueAt
    {
        get
        {
            if (_first is not { } first)
                return null;

            var afterQuiet = _last + quietPeriod;
            var latest = first + longestWait;
            return afterQuiet < latest ? afterQuiet : latest;
        }
    }

    /// <summary>
    /// Adds a notification that came at <paramref name="now"/> to the burst not yet taken, or
    /// starts one with it.
    /// </summary>
    public void Notify(TimeSpan now)
    {
        _first ??= now;
        _last = now;
    }

    /// <summary>
    /// Takes the burst once it is due at <paramref name="now"/>, giving the moment of its last
    /// notification. False while there is none, or while it isn't due yet: it then stays as it was.
    /// </summary>
    public bool TryTake(TimeSpan now, out TimeSpan lastNotification)
    {
        if (DueAt is not { } due || now < due)
        {
            lastNotification = default;
            return false;
        }

        lastNotification = _last;
        _first = null;
        return true;
    }

    /// <summary>Drops the burst not yet taken.</summary>
    public void Clear() => _first = null;

    /// <summary>
    /// Whether a burst whose last notification came at <paramref name="lastNotification"/> still
    /// calls for a reload: not once a load began after it (<paramref name="loadStartedAt"/>, null
    /// before the first load), as that load read the library with the burst's changes in it.
    /// </summary>
    public static bool NeedsReload(TimeSpan lastNotification, TimeSpan? loadStartedAt) =>
        loadStartedAt is not { } started || started <= lastNotification;
}
