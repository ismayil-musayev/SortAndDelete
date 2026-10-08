namespace SortAndDelete.Models;

/// <summary>
/// How alike two items have to be before they count as duplicates. Each level asks a
/// genuinely different question rather than sitting at a different point on one dial,
/// so each carries its own rule set — see <c>DuplicateRules</c>.
/// </summary>
public enum DuplicateLevel
{
    /// <summary>Byte-identical files. Always applied, whatever level is selected.</summary>
    Exact = 1,

    /// <summary>
    /// The same picture re-encoded: resized, recompressed, or converted. This is what a
    /// photo looks like after WhatsApp, Bluetooth, email or HEIC-to-JPG — no pixel content
    /// changed, only the file did.
    /// </summary>
    SamePhoto = 2,

    /// <summary>The same picture adjusted: filters, brightness, quarter turns, watermarks.</summary>
    Adjusted = 3,

    /// <summary>The same moment: burst shots taken seconds apart.</summary>
    SameMoment = 4,

    /// <summary>Similar shots from one session — browsing, not cleanup.</summary>
    Similar = 5,
}

/// <summary>A set of items considered duplicates of each other, with one nominated to keep.</summary>
public sealed class DuplicateGroup
{
    /// <summary>Every member, keeper first.</summary>
    public required IReadOnlyList<PhotoAsset> Items { get; init; }

    /// <summary>The copy worth keeping — the original where we can tell, see <c>KeeperRanking</c>.</summary>
    public required PhotoAsset Keeper { get; init; }

    /// <summary>
    /// True when every member is byte-identical to the others. These are certain, so the UI
    /// can preselect them; anything matched perceptually is a judgement call.
    /// </summary>
    public required bool IsExact { get; init; }

    /// <summary>Bytes that would come back if everything except the keeper went.</summary>
    public long ReclaimableBytes => Items.Where(p => p.Id != Keeper.Id).Sum(p => p.SizeBytes);

    public int RedundantCount => Items.Count - 1;
}
