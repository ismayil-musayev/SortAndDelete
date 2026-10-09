using SortAndDelete.Logic;

namespace SortAndDelete.Services;

/// <summary>
/// Reports changes made to the photo library while this app is in the foreground: a screenshot, a
/// finished download, a photo deleted in an app beside this one, and the app's own changes too.
/// Changes made while the app is in the background are left to the reload on coming back.
/// </summary>
public interface ILibraryChangeWatcher
{
    /// <summary>
    /// Raised on the main thread once a burst of changes has settled (see <see cref="ChangeDebounce"/>),
    /// with the moment of its last notification: a read of the library that began after that
    /// moment found the burst's changes.
    /// </summary>
    event EventHandler<LibraryChangedEventArgs>? Changed;

    /// <summary>Starts watching; does nothing while already watching.</summary>
    void Start();

    /// <summary>Stops watching and drops a burst not yet reported; does nothing while stopped.</summary>
    void Stop();
}

/// <summary>A burst of library changes that has settled.</summary>
public sealed class LibraryChangedEventArgs(TimeSpan lastNotifiedAt) : EventArgs
{
    /// <summary>When the burst's last change notification came, on <see cref="ChangeDebounce.Now"/>.</summary>
    public TimeSpan LastNotifiedAt => lastNotifiedAt;
}

/// <summary>Watches nothing: <see cref="Changed"/> is never raised.</summary>
public sealed class NoLibraryChangeWatcher : ILibraryChangeWatcher
{
    public event EventHandler<LibraryChangedEventArgs>? Changed { add { } remove { } }

    public void Start()
    {
    }

    public void Stop()
    {
    }
}
