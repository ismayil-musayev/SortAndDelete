using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class StreakCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 7, 13);

    private static DateTime On(int year, int month, int day, int hour = 12) =>
        new(year, month, day, hour, 0, 0);

    [Fact]
    public void No_activity_means_no_streak()
    {
        var result = StreakCalculator.Compute([], Today);
        Assert.Equal(0, result.ReviewedToday);
        Assert.Equal(0, result.StreakDays);
    }

    [Fact]
    public void Counts_today_and_consecutive_days()
    {
        List<DateTime> decisions =
        [
            On(2026, 7, 13), On(2026, 7, 13, 18), // today ×2
            On(2026, 7, 12),
            On(2026, 7, 11),
        ];

        var result = StreakCalculator.Compute(decisions, Today);

        Assert.Equal(2, result.ReviewedToday);
        Assert.Equal(3, result.StreakDays);
    }

    [Fact]
    public void Gap_breaks_the_streak()
    {
        List<DateTime> decisions =
        [
            On(2026, 7, 13),
            On(2026, 7, 11), // 12th missing
        ];

        var result = StreakCalculator.Compute(decisions, Today);

        Assert.Equal(1, result.StreakDays);
    }

    [Fact]
    public void Streak_survives_until_end_of_day_when_today_has_no_reviews_yet()
    {
        List<DateTime> decisions = [On(2026, 7, 12), On(2026, 7, 11)];

        var result = StreakCalculator.Compute(decisions, Today);

        Assert.Equal(0, result.ReviewedToday);
        Assert.Equal(2, result.StreakDays); // counted from yesterday
    }
}
