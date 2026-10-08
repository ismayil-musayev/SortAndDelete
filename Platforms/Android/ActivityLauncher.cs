using System.Collections.Concurrent;
using Android.App;
using Android.Content;

namespace SortAndDelete.Services;

/// <summary>
/// Launches activities that return data (e.g. the system folder picker) and completes
/// with the result Intent. Complements IntentSenderLauncher, which handles consent dialogs.
/// </summary>
public static class ActivityLauncher
{
    private static int _nextRequestCode = 52000;
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<Intent?>> Pending = new();

    public static Task<Intent?> LaunchForResultAsync(Intent intent)
    {
        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("No current activity.");

        var code = Interlocked.Increment(ref _nextRequestCode);
        var tcs = new TaskCompletionSource<Intent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[code] = tcs;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                activity.StartActivityForResult(intent, code);
            }
            catch (Exception ex)
            {
                Pending.TryRemove(code, out _);
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <summary>Called from MainActivity.OnActivityResult; ignores request codes it doesn't own.</summary>
    public static void OnResult(int requestCode, Result resultCode, Intent? data)
    {
        if (Pending.TryRemove(requestCode, out var tcs))
            tcs.TrySetResult(resultCode == Result.Ok ? data : null);
    }
}
