using SQLite;

namespace SortAndDelete.Models;

/// <summary>One decision the user made about one photo. This is what makes progress resumable.</summary>
[Table("reviews")]
public sealed class ReviewRecord
{
    [PrimaryKey]
    public string PhotoId { get; set; } = "";

    [Indexed]
    public string MonthKey { get; set; } = "";

    public SwipeDecision Decision { get; set; }

    public DateTime DecidedAt { get; set; }

    /// <summary>
    /// When the photo was actually moved to the system trash (Recently Deleted on iOS).
    /// Null while the photo is still only in the in-app bin and can be restored instantly.
    /// </summary>
    public DateTime? CommittedAt { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Album name for <see cref="SwipeDecision.Moved"/> decisions.</summary>
    public string? TargetAlbum { get; set; }

    /// <summary>
    /// The storage volume the photo was on when it went to the bin (<see cref="PhotoAsset.VolumeName"/>),
    /// so that emptying the bin can tell a photo on an SD card that has been taken out, which the
    /// library no longer lists, from one that is gone. Null for other decisions, on iOS, and in
    /// records written before this was kept.
    /// </summary>
    public string? VolumeName { get; set; }
}
