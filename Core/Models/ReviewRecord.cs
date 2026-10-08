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
}
