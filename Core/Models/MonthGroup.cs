using System.Globalization;

namespace SortAndDelete.Models;

/// <summary>Aggregated review progress for one calendar month of photos.</summary>
public sealed class MonthGroup
{
    public required string MonthKey { get; init; }
    public required string DisplayName { get; init; }
    public int TotalCount { get; init; }
    public int ReviewedCount { get; init; }
    public string? CoverPhotoId { get; init; }

    public double Progress => TotalCount == 0 ? 0 : (double)ReviewedCount / TotalCount;
    public bool IsDone => TotalCount > 0 && ReviewedCount >= TotalCount;
    public string ProgressText => $"{ReviewedCount:N0} of {TotalCount:N0} reviewed";
    public string StatusGlyph => IsDone ? "✓" : "›";

    public static string DisplayFor(string monthKey)
    {
        if (DateTime.TryParseExact(monthKey, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        return monthKey;
    }
}
