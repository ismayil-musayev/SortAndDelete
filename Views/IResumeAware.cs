namespace SortAndDelete.Views;

/// <summary>
/// A page that looks again when the app comes back to the foreground with it up: other apps may
/// have changed the library meanwhile, and a page's OnAppearing doesn't run then. The app tells
/// the page that is up in the window that came back (see <c>App.TellPageAppResumedAsync</c>).
/// </summary>
public interface IResumeAware
{
    /// <summary>
    /// The app has come back with this page up. Called on the UI thread, after the gallery was
    /// marked stale, so what the page reads from it now is read from the library again.
    /// </summary>
    Task OnAppResumedAsync();
}
