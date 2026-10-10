namespace SortAndDelete.Services;

/// <summary>
/// A deck card's picture, ready to go on screen.
///
/// On Android it is decoded already. An image control given a source decodes it in the
/// background and swaps it in some frames later, keeping the previous picture up meanwhile, so
/// the card's label, which changes at once, would name one photo over the picture of another,
/// and the buttons would act on the one named. A decoded bitmap is set in the same step as the
/// label, and both are drawn in the same frame. Elsewhere it is a source the image control
/// loads by itself, as it always has.
/// </summary>
public sealed class CardPicture : IDisposable
{
#if ANDROID
    private Android.Graphics.Bitmap? _bitmap;

    public CardPicture(Android.Graphics.Bitmap bitmap) => _bitmap = bitmap;

    /// <summary>The decoded picture; null once let go of.</summary>
    public Android.Graphics.Bitmap? Bitmap => _bitmap;

    /// <summary>Whether <see cref="Dispose"/> has run: there is no picture here to show any more.</summary>
    public bool IsLetGo => _bitmap is null;

    /// <summary>
    /// Lets go of this side's handle on the bitmap, which is all that keeps a bitmap no view shows
    /// from being collected. It is not recycled: a card still showing it keeps it.
    /// </summary>
    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
#else
    public CardPicture(ImageSource source) => Source = source;

    /// <summary>The source the card's image control loads the picture from.</summary>
    public ImageSource Source { get; }

    /// <summary>Never: a source holds nothing to let go of.</summary>
    public bool IsLetGo => false;

    public void Dispose()
    {
    }
#endif
}

/// <summary>
/// The clock the deck times its cards and the presses on its buttons on: milliseconds, monotonic,
/// and on Android the one the system stamps touch events with, so that a press can be placed by
/// when the finger went down rather than by when the app got round to handling it.
/// </summary>
public static class PressClock
{
    /// <summary>Now, on this clock.</summary>
    public static long Now =>
#if ANDROID
        Android.OS.SystemClock.UptimeMillis();
#else
        Environment.TickCount64;
#endif

    /// <summary>
    /// When the latest finger went down on the app's window (Android, from the activity), as the
    /// system stamped it; null before the first, and where touches aren't seen.
    /// </summary>
    public static long? LastFingerDownAt { get; private set; }

    /// <summary>A finger went down at <paramref name="eventTime"/>, the touch event's own time.</summary>
    public static void NoteFingerDown(long eventTime) => LastFingerDownAt = eventTime;
}
