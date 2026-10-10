using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity;
using SortAndDelete.Services;

namespace SortAndDelete;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // After base.OnCreate: MAUI swaps the splash theme for the main theme in there, and
        // EdgeToEdge touches the decor view, which would otherwise be built from the splash theme.
        base.OnCreate(savedInstanceState);

        // Android 15+ forces edge-to-edge for targetSdk 35+; this gives older versions the same
        // transparent bars so every device looks alike. MAUI's safe-area padding keeps content
        // clear of the bars. The UI is always dark, so ask for light bar icons explicitly.
        var transparent = Android.Graphics.Color.Transparent.ToArgb();
        EdgeToEdge.Enable(this, SystemBarStyle.Dark(transparent), SystemBarStyle.Dark(transparent));
    }

    /// <summary>
    /// Notes when each finger goes down, as the system stamped the touch, before anything handles
    /// it. The review deck places a press by that time: a tap is about the card that was up when
    /// the finger went down, however long the app takes to get to it (see <see cref="PressClock"/>).
    /// </summary>
    public override bool DispatchTouchEvent(Android.Views.MotionEvent? e)
    {
        if (e is { ActionMasked: Android.Views.MotionEventActions.Down or Android.Views.MotionEventActions.PointerDown })
            PressClock.NoteFingerDown(e.EventTime);
        return base.DispatchTouchEvent(e);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        IntentSenderLauncher.OnResult(requestCode, resultCode);
        ActivityLauncher.OnResult(requestCode, resultCode, data);
    }
}
