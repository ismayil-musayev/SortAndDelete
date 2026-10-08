using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// Picks which copy of a duplicate set to keep.
///
/// The interesting case is the one this feature exists for: the original sitting in the
/// camera folder and the shrunken copy that came back over WhatsApp. Both are "the same
/// photo", but only one is worth keeping, and the folder it lives in usually gives it away
/// well before file size does.
/// </summary>
public static class KeeperRanking
{
    /// <summary>
    /// Folder and file-name fragments that mark a copy as received rather than taken.
    /// Matched case-insensitively against the item's folder path and file name.
    /// </summary>
    private static readonly string[] ReceivedMarkers =
    [
        "whatsapp",
        "telegram",
        "viber",
        "messenger",
        "bluetooth",
        "download",
    ];

    /// <summary>WhatsApp names what it hands to the gallery <c>IMG-20260115-WA0001.jpg</c>.</summary>
    private const string WhatsAppFileMarker = "-wa";

    public static PhotoAsset Best(IEnumerable<PhotoAsset> items) =>
        items
            .OrderByDescending(p => p.IsFavorite)          // the user already said this one matters
            .ThenBy(IsReceivedCopy)                        // prefer the copy that wasn't sent to us
            .ThenByDescending(p => (long)p.Width * p.Height)
            .ThenByDescending(p => p.SizeBytes)
            .ThenBy(p => p.TakenAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)     // stable when everything above ties
            .First();

    /// <summary>
    /// Whether the item looks like it arrived from somewhere rather than being taken here.
    /// A heuristic, and only ever used to order candidates — never to delete anything.
    /// </summary>
    public static bool IsReceivedCopy(PhotoAsset photo)
    {
        foreach (var marker in ReceivedMarkers)
        {
            if (photo.FolderPath.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return photo.FileName.Contains(WhatsAppFileMarker, StringComparison.OrdinalIgnoreCase);
    }
}
