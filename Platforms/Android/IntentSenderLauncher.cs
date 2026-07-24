using System.Collections.Concurrent;
using Android.App;
using Android.Content;

namespace SortAndDelete.Services;

/// <summary>
/// Launches system consent dialogs (trash/write requests return a PendingIntent that must be
/// started for a result) and completes when the user confirms or denies.
/// </summary>
public static class IntentSenderLauncher
{
    private static int _nextRequestCode = 42000;
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> Pending = new();

    public static Task<bool> LaunchAsync(IntentSender sender)
    {
        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("No current activity.");

        var code = Interlocked.Increment(ref _nextRequestCode);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[code] = tcs;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                activity.StartIntentSenderForResult(sender, code, null, 0, 0, 0);
            }
            catch (Exception ex)
            {
                Pending.TryRemove(code, out _);
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <summary>Called from MainActivity.OnActivityResult.</summary>
    public static void OnResult(int requestCode, Result resultCode)
    {
        if (Pending.TryRemove(requestCode, out var tcs))
            tcs.TrySetResult(resultCode == Result.Ok);
    }
}
