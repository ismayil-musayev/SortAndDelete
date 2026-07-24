using SortAndDelete.Models;

namespace SortAndDelete.Logic;

public static class MonthAggregator
{
    /// <summary>Builds month groups (newest month first) with review progress.</summary>
    public static List<MonthGroup> Build(IReadOnlyList<PhotoAsset> photos, IReadOnlySet<string> reviewedIds)
    {
        return photos
            .GroupBy(p => p.MonthKey)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MonthGroup
            {
                MonthKey = g.Key,
                DisplayName = MonthGroup.DisplayFor(g.Key),
                TotalCount = g.Count(),
                ReviewedCount = g.Count(p => reviewedIds.Contains(p.Id)),
                CoverPhotoId = g.OrderByDescending(p => p.TakenAt).First().Id,
            })
            .ToList();
    }
}
