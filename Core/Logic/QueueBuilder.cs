using SortAndDelete.Models;

namespace SortAndDelete.Logic;

public enum DeckMode
{
    /// <summary>One calendar month, oldest first (the default review flow).</summary>
    Month,

    /// <summary>All unreviewed items, largest file first — fastest way to free space.</summary>
    Biggest,
}

/// <summary>Builds the deck queues. Reviewed photos are always excluded — resume comes for free.</summary>
public static class QueueBuilder
{
    public static List<PhotoAsset> ForMonth(IEnumerable<PhotoAsset> photos, IReadOnlySet<string> reviewedIds, string monthKey) =>
        [.. photos
            .Where(p => p.MonthKey == monthKey && !reviewedIds.Contains(p.Id))
            .OrderBy(p => p.TakenAt)];

    public static List<PhotoAsset> Biggest(IEnumerable<PhotoAsset> photos, IReadOnlySet<string> reviewedIds) =>
        [.. photos
            .Where(p => !reviewedIds.Contains(p.Id) && p.SizeBytes > 0)
            .OrderByDescending(p => p.SizeBytes)];
}
