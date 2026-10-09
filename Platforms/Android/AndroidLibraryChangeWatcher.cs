using Android.Database;
using Android.OS;
using Android.Provider;
using SortAndDelete.Logic;
using AndroidUri = Android.Net.Uri;
using Application = Android.App.Application;

namespace SortAndDelete.Services;

/// <summary>
/// Watches MediaStore's image and video collections, where it announces each change to a photo or
/// a video, whichever app makes it, and raises <see cref="Changed"/> once a burst of notifications
/// has settled. Its files collection also announces every other file in shared storage
/// (documents, audio, hidden files such as caches), none of which the gallery lists.
/// </summary>
public sealed class AndroidLibraryChangeWatcher : ILibraryChangeWatcher
{
    // Gathers a camera burst, or a batch another app moves, into one reload, and a screenshot
    // still shows within a couple of seconds.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);

    // Changes that never pause for a quiet period, a long copy for instance, still show this often.
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(10);

    private readonly Handler _mainThread = new(Looper.MainLooper!);
    private readonly ChangeDebounce _debounce = new(QuietPeriod, LongestWait);
    private readonly Observer _observer;
    private readonly Java.Lang.Runnable _check;
    private bool _watching;
    private bool _checkPosted;

    public AndroidLibraryChangeWatcher()
    {
        _observer = new Observer(_mainThread, OnNotified);
        _check = new Java.Lang.Runnable(Check);
    }

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public void Start()
    {
        if (_watching || Application.Context.ContentResolver is not { } resolver)
            return;

        try
        {
            resolver.RegisterContentObserver(MediaStore.Images.Media.ExternalContentUri!, true, _observer);
            resolver.RegisterContentObserver(MediaStore.Video.Media.ExternalContentUri!, true, _observer);
            _watching = true;
        }
        catch (Exception)
        {
            // Unwatched, the library still reloads on coming back to the app and on a pull.
            resolver.UnregisterContentObserver(_observer);
        }
    }

    public void Stop()
    {
        if (!_watching)
            return;

        Application.Context.ContentResolver?.UnregisterContentObserver(_observer);
        _watching = false;
        _mainThread.RemoveCallbacks(_check);
        _checkPosted = false;
        _debounce.Clear();
    }

    private void OnNotified()
    {
        if (!_watching)
            return; // dispatched before Stop

        var now = ChangeDebounce.Now;
        _debounce.Notify(now);
        if (!_checkPosted)
            PostCheck(now);
    }

    private void Check()
    {
        _checkPosted = false;
        var now = ChangeDebounce.Now;
        if (_debounce.TryTake(now, out var lastNotification))
            Changed?.Invoke(this, new LibraryChangedEventArgs(lastNotification));
        else
            PostCheck(now); // not due yet: more notifications came since
    }

    /// <summary>Runs <see cref="Check"/> on the main thread when the burst not yet taken is due.</summary>
    private void PostCheck(TimeSpan now)
    {
        if (_debounce.DueAt is not { } due)
            return;

        _mainThread.PostDelayed(_check, (long)Math.Ceiling(Math.Max(0, (due - now).TotalMilliseconds)));
        _checkPosted = true;
    }

    /// <summary>Hands every notification to <paramref name="notified"/> on the handler's thread.</summary>
    private sealed class Observer(Handler handler, Action notified) : ContentObserver(handler)
    {
        // Android 11+ hands over a batch of uris, and the batch's default handler calls this per uri.
        public override void OnChange(bool selfChange, AndroidUri? uri) => notified();
    }
}
