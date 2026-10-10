namespace SortAndDelete.Models;

public enum MediaKind
{
    Image = 1,
    Video = 2,
}

/// <summary>A photo or video in the device gallery, platform-agnostic.</summary>
public sealed class PhotoAsset
{
    /// <summary>MediaStore ID (Android) or PHAsset.localIdentifier (iOS).</summary>
    public required string Id { get; init; }

    public MediaKind Kind { get; init; } = MediaKind.Image;

    /// <summary>Local time the item was taken (falls back to file time when EXIF is absent).</summary>
    public DateTime TakenAt { get; init; }

    /// <summary>
    /// The platform's last-modification time, in UTC (iOS PHAsset.modificationDate), filled
    /// where an edit can leave the size and capture date unchanged. It also moves on
    /// metadata-only changes such as favouriting. Android leaves it unset and relies on size
    /// and capture date, as before. <see cref="DateTime.MinValue"/>, the default, means not
    /// tracked.
    /// </summary>
    public DateTime ModifiedAt { get; init; }

    /// <summary>
    /// When the bytes of the item's own file last changed, in UTC, where the platform can change
    /// them in place: Android (MediaStore DATE_MODIFIED) rewrites the file for an edit such as a
    /// rotation, which can keep its size. Android always fills it, a DATE_MODIFIED of 0 or NULL
    /// as 1970, which still compares like any other stamp. Unset on iOS, where the original is
    /// never rewritten and an edit is saved beside it (<see cref="IsEdited"/>).
    /// <see cref="DateTime.MinValue"/>, the default, means not tracked.
    /// </summary>
    /// <remarks>
    /// Only the content hash checks it. A fingerprint goes by <see cref="ModifiedAt"/>, which
    /// Android leaves unset: a stale one still shows the right picture, at worst turned the old
    /// way, and checking this as well would re-fingerprint every Android library.
    /// </remarks>
    public DateTime FileModifiedAt { get; init; }

    /// <summary>
    /// True when an edit is kept beside the untouched original (iOS PHAsset.hasAdjustments), so
    /// the original's bytes no longer show what the item looks like and it can never be an
    /// identical file of anything. Always false on Android, where an edit rewrites the file
    /// itself.
    /// </summary>
    public bool IsEdited { get; init; }

    /// <summary>File size in bytes. 0 when unknown (iOS defers the lookup); settable so it can be filled lazily.</summary>
    public long SizeBytes { get; set; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Video duration in milliseconds; 0 for images.</summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// System-wide favorite flag (MediaStore IS_FAVORITE on Android 11+, PHAsset.favorite
    /// on iOS). Samsung/Google gallery "Favourites" map onto this.
    /// </summary>
    public bool IsFavorite { get; init; }

    /// <summary>File name like "IMG_1234.jpg". Empty when the platform doesn't expose it.</summary>
    public string FileName { get; init; } = "";

    /// <summary>Folder path like "Pictures/Travel/" (Android). Empty on iOS — photos live in albums, not folders.</summary>
    public string FolderPath { get; init; } = "";

    /// <summary>Key used to group items by month, e.g. "2026-07".</summary>
    public string MonthKey => TakenAt.ToString("yyyy-MM");

    /// <summary>"Pictures/Travel/IMG_1234.jpg" — or just the file name when there is no folder.</summary>
    public string LocationText =>
        string.IsNullOrEmpty(FolderPath) ? FileName : $"{FolderPath}{FileName}";

    public string DurationText
    {
        get
        {
            if (DurationMs <= 0)
                return "";
            var t = TimeSpan.FromMilliseconds(DurationMs);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
}
