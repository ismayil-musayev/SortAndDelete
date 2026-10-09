using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class ChangeDebounceTests
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(10);

    /// <summary>The moment <paramref name="milliseconds"/> after the clock's start.</summary>
    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static ChangeDebounce Debounce() => new(QuietPeriod, LongestWait);

    [Fact]
    public void Nothing_is_due_without_a_notification()
    {
        var debounce = Debounce();

        Assert.Null(debounce.DueAt);
        Assert.False(debounce.TryTake(At(60_000), out _));
    }

    [Fact]
    public void A_notification_is_taken_a_quiet_period_later()
    {
        var debounce = Debounce();

        debounce.Notify(At(1_000));

        Assert.Equal(At(2_500), debounce.DueAt);
        Assert.False(debounce.TryTake(At(2_499), out _));
        Assert.True(debounce.TryTake(At(2_500), out var last));
        Assert.Equal(At(1_000), last);
        Assert.Null(debounce.DueAt);
    }

    [Fact]
    public void A_burst_is_taken_once_a_quiet_period_after_its_last_notification()
    {
        // Two screenshots a second apart, each inserted pending, then written, then published.
        var debounce = Debounce();
        foreach (int ms in new[] { 0, 40, 180, 1_200, 1_240, 1_380 })
            debounce.Notify(At(ms));

        Assert.False(debounce.TryTake(At(1_680), out _)); // when the first screenshot alone was due
        Assert.Equal(At(2_880), debounce.DueAt);
        Assert.True(debounce.TryTake(At(2_880), out var last));
        Assert.Equal(At(1_380), last);
        Assert.False(debounce.TryTake(At(60_000), out _));
    }

    [Fact]
    public void Notifications_that_keep_coming_are_taken_a_longest_wait_after_the_first()
    {
        // A long copy in another app: a notification every second, never a quiet period apart.
        var debounce = Debounce();
        for (int ms = 0; ms < 10_000; ms += 1_000)
            debounce.Notify(At(ms));

        Assert.Equal(At(10_000), debounce.DueAt);
        Assert.True(debounce.TryTake(At(10_000), out var last));
        Assert.Equal(At(9_000), last);
    }

    [Fact]
    public void The_longest_wait_counts_from_the_first_notification_of_each_burst()
    {
        var debounce = Debounce();
        for (int ms = 0; ms < 10_000; ms += 1_000)
            debounce.Notify(At(ms));
        Assert.True(debounce.TryTake(At(10_000), out _));

        for (int ms = 10_500; ms < 20_000; ms += 1_000)
            debounce.Notify(At(ms));

        Assert.Equal(At(20_500), debounce.DueAt);
        Assert.True(debounce.TryTake(At(20_500), out var last));
        Assert.Equal(At(19_500), last);
    }

    [Fact]
    public void Clearing_drops_the_burst()
    {
        var debounce = Debounce();
        debounce.Notify(At(0));

        debounce.Clear();

        Assert.Null(debounce.DueAt);
        Assert.False(debounce.TryTake(At(60_000), out _));
    }

    [Fact]
    public void A_burst_needs_a_reload_while_no_load_has_begun() =>
        Assert.True(ChangeDebounce.NeedsReload(At(1_000), loadStartedAt: null));

    [Theory]
    [InlineData(500, true)] // began before the last notification: it can have missed the change
    [InlineData(1_000, true)] // began at the same moment
    [InlineData(1_001, false)] // began after it: it read the library with the change in it
    public void A_burst_needs_a_reload_unless_a_load_began_after_its_last_notification(int loadStartedMs, bool needsReload) =>
        Assert.Equal(needsReload, ChangeDebounce.NeedsReload(At(1_000), At(loadStartedMs)));

    [Fact]
    public void The_load_after_the_apps_own_move_absorbs_the_moves_notifications()
    {
        // The move's MediaStore writes, then the gallery is invalidated and loaded again.
        var debounce = Debounce();
        debounce.Notify(At(0));
        debounce.Notify(At(150));
        var loadStartedAt = At(400);

        Assert.True(debounce.TryTake(At(1_650), out var last));
        Assert.False(ChangeDebounce.NeedsReload(last, loadStartedAt));
    }

    [Fact]
    public void A_change_while_a_load_runs_needs_another_load()
    {
        var loadStartedAt = At(0);
        var debounce = Debounce();
        debounce.Notify(At(300));

        Assert.True(debounce.TryTake(At(1_800), out var last));
        Assert.True(ChangeDebounce.NeedsReload(last, loadStartedAt));
    }
}
