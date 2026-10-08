namespace SortAndDelete.Logic;

public static class StreakCalculator
{
    public readonly record struct Result(int ReviewedToday, int StreakDays);

    /// <summary>
    /// Streak = consecutive days with at least one decision, counting backwards from
    /// today (or from yesterday when today has none yet, so a streak isn't "lost"
    /// before the day is over).
    /// </summary>
    public static Result Compute(IEnumerable<DateTime> decidedAtLocal, DateOnly today)
    {
        var dates = decidedAtLocal.Select(DateOnly.FromDateTime).ToList();
        var days = dates.ToHashSet();

        int reviewedToday = dates.Count(d => d == today);

        var cursor = days.Contains(today) ? today : today.AddDays(-1);
        int streak = 0;
        while (days.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return new Result(reviewedToday, streak);
    }
}
