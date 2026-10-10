using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class TickCarryOverTests
{
    /// <summary>A file's stamp, as Android's DATE_MODIFIED gives it.</summary>
    private static readonly DateTime Written = new(2026, 7, 1, 12, 0, 5, DateTimeKind.Utc);

    /// <summary>
    /// A photo as a scan finds it. Each call makes a new one, as a reloaded library does, so
    /// only what it says can match what was on screen before.
    /// </summary>
    private static PhotoAsset Photo(
        string id,
        long size = 5_000_000,
        DateTime modifiedAt = default,
        DateTime? fileModifiedAt = null,
        bool edited = false) => new()
    {
        Id = id,
        SizeBytes = size,
        ModifiedAt = modifiedAt,
        FileModifiedAt = fileModifiedAt ?? Written,
        IsEdited = edited,
    };

    /// <summary>A group as the grouper makes it: the first photo is the one to keep.</summary>
    private static DuplicateGroup Group(params PhotoAsset[] photos) =>
        new() { Items = photos, Keeper = photos[0], IsExact = false };

    /// <summary>The groups as they stand on screen once the user has set these ticks themselves.</summary>
    private static List<GroupTicks> After(List<GroupTicks> shown, params (string Id, bool Ticked)[] taps) =>
        [.. shown.Select(group => group with
        {
            Items = [.. group.Items.Select(item =>
                taps.Any(tap => tap.Id == item.PhotoId)
                    ? item with { IsTicked = taps.First(tap => tap.Id == item.PhotoId).Ticked }
                    : item)],
        })];

    private static string[] Ticked(List<GroupTicks> shown) =>
        [.. shown.SelectMany(group => group.Items).Where(item => item.IsTicked).Select(item => item.PhotoId).Order()];

    // ---------- The level's own ticks ----------

    [Fact]
    public void A_level_that_preselects_ticks_everything_but_the_keeper()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: true);

        Assert.Equal(["2", "3"], Ticked(shown));
        Assert.Equal("1", Assert.Single(shown).KeeperId);
        Assert.Equal(["1", "2", "3"], shown[0].Items.Select(item => item.PhotoId).ToArray());
    }

    [Fact]
    public void A_level_that_leaves_the_choice_to_the_user_ticks_nothing()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"))], preselect: false);

        Assert.Empty(Ticked(shown));
    }

    /// <summary>Ranking can't tell an edited item from its original, so the app never picks it for the bin.</summary>
    [Fact]
    public void An_edited_item_is_never_ticked_by_the_level()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2", edited: true), Photo("3"))], preselect: true);

        Assert.Equal(["3"], Ticked(shown));
    }

    // ---------- Carried over ----------

    /// <summary>
    /// The case this exists for: the user unticks one copy and ticks another, looks at a picture
    /// in another app, and comes back to a rescan that found nothing new.
    /// </summary>
    [Fact]
    public void An_unchanged_group_keeps_its_ticks_and_unticks()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: true);
        var before = After(shown, ("1", true), ("2", false));

        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: true);

        Assert.Equal(["1", "3"], Ticked(rescanned));
    }

    [Fact]
    public void A_tick_set_where_the_level_ticks_nothing_is_kept_too()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2")), Group(Photo("3"), Photo("4"))], preselect: false);
        var before = After(shown, ("4", true));

        // The groups come back in another order, as their sizes decide.
        var rescanned = TickCarryOver.Apply(before, [Group(Photo("3"), Photo("4")), Group(Photo("1"), Photo("2"))], preselect: false);

        Assert.Equal(["4"], Ticked(rescanned));
    }

    /// <summary>
    /// The user hasn't seen the newcomer, so what they ticked among the old copies isn't taken
    /// as said of these.
    /// </summary>
    [Fact]
    public void A_tick_is_not_carried_into_a_group_that_gained_a_member()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"))], preselect: false);
        var before = After(shown, ("2", true));

        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: false);

        Assert.Empty(Ticked(rescanned));
    }

    [Fact]
    public void A_tick_is_not_carried_into_a_group_that_lost_a_member()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: false);
        var before = After(shown, ("2", true), ("3", true));

        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), Photo("2"))], preselect: false);

        Assert.Empty(Ticked(rescanned));
    }

    /// <summary>Two groups a new match joined into one are a group the user hasn't seen either.</summary>
    [Fact]
    public void Ticks_are_not_carried_into_groups_that_merged()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2")), Group(Photo("3"), Photo("4"))], preselect: false);
        var before = After(shown, ("2", true), ("4", true));

        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), Photo("2"), Photo("3"), Photo("4"))], preselect: false);

        Assert.Empty(Ticked(rescanned));
    }

    // ---------- Left unticked ----------

    /// <summary>
    /// The user keeps B beside the keeper and sends A to the bin. The group comes back without
    /// A, and a level that ticks by itself would tick B again: the one copy they chose to keep,
    /// and on every rescan after that.
    /// </summary>
    [Fact]
    public void An_item_left_unticked_stays_unticked_when_another_leaves_its_group()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("K"), Photo("A"), Photo("B"))], preselect: true);
        var before = After(shown, ("B", false));

        var regrouped = TickCarryOver.Apply(before, [Group(Photo("K"), Photo("B"))], preselect: true);

        Assert.Empty(Ticked(regrouped));
    }

    /// <summary>The newcomer is put to the user as the level would; what they left unticked is not.</summary>
    [Fact]
    public void An_item_left_unticked_stays_unticked_when_another_joins_its_group()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"))], preselect: true);
        var before = After(shown, ("2", false));

        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: true);

        Assert.Equal(["3"], Ticked(rescanned));
    }

    /// <summary>
    /// iOS moves the modification time for as little as favouriting. A tick wouldn't outlive
    /// that, but leaving the copy unticked is the safe side whatever it looks like now. The
    /// tick beside it is carried: its own photo and the group's members are as they were.
    /// </summary>
    [Fact]
    public void An_item_left_unticked_stays_unticked_though_its_photo_changed()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: true);
        var before = After(shown, ("2", false));

        var touched = Photo("2", modifiedAt: Written.AddHours(1));
        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), touched, Photo("3"))], preselect: true);

        Assert.Equal(["3"], Ticked(rescanned));
    }

    // ---------- Another keeper ----------

    /// <summary>
    /// The level's ticks followed from the copy it suggested keeping. With another one suggested
    /// now, say because the user favourited it, carrying them over would leave that one ticked
    /// for the bin. Nor is anything kept unticked: the old keeper was unticked for being the
    /// keeper, and what the user left unticked was left so beside that keeper.
    /// </summary>
    [Fact]
    public void A_group_with_another_keeper_falls_back_to_the_level_s_ticks()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: true);
        var before = After(shown, ("3", false));

        var rescanned = TickCarryOver.Apply(before, [Group(Photo("2"), Photo("1"), Photo("3"))], preselect: true);

        Assert.Equal(["1", "3"], Ticked(rescanned));
    }

    /// <summary>
    /// The same where two groups became one: only what was left unticked under the keeper the
    /// merged group has now stays unticked. The other group's keeper and its unticked copy are
    /// put to the user as the level would.
    /// </summary>
    [Fact]
    public void In_groups_that_merged_only_the_keeper_s_own_group_stays_unticked()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2")), Group(Photo("3"), Photo("4"))], preselect: true);
        var before = After(shown, ("2", false), ("4", false));

        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), Photo("2"), Photo("3"), Photo("4"))], preselect: true);

        Assert.Equal(["3", "4"], Ticked(rescanned));
    }

    // ---------- A ticked photo that changed ----------

    /// <summary>
    /// On iOS an edit is saved beside the original and flagged, and a ticked copy that has become
    /// one must not stay ticked on the strength of how it looked before. The rest of the group
    /// keep what they had.
    /// </summary>
    [Fact]
    public void An_item_edited_since_is_not_kept_ticked()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: true);
        var before = After(shown, ("3", false));

        var edited = Photo("2", modifiedAt: Written.AddHours(1), edited: true);
        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), edited, Photo("3"))], preselect: true);

        Assert.Empty(Ticked(rescanned));
    }

    /// <summary>
    /// Android rewrites the file itself, which only its stamp or its size shows. What the user
    /// ticked was the picture as it was, so it goes back to the level's own.
    /// </summary>
    [Theory]
    [InlineData(5_000_000, 60)] // rotated in place: the same size, a later stamp
    [InlineData(4_200_000, 0)]  // another file under the same id
    public void An_item_whose_file_changed_since_is_not_kept_ticked(long sizeNow, int minutesLater)
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"), Photo("3"))], preselect: false);
        var before = After(shown, ("2", true), ("3", true));

        var changed = Photo("2", size: sizeNow, fileModifiedAt: Written.AddMinutes(minutesLater));
        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), changed, Photo("3"))], preselect: false);

        Assert.Equal(["3"], Ticked(rescanned));
    }

    /// <summary>iOS moves the modification time for as little as favouriting, and for a tick that is a change too.</summary>
    [Fact]
    public void A_ticked_item_whose_modification_time_moved_goes_back_to_the_level_s_tick()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2"))], preselect: false);
        var before = After(shown, ("2", true));

        var touched = Photo("2", modifiedAt: Written.AddHours(1));
        var rescanned = TickCarryOver.Apply(before, [Group(Photo("1"), touched)], preselect: false);

        Assert.Empty(Ticked(rescanned));
    }

    /// <summary>
    /// What is carried is what was last on screen and nothing older. An item that dropped out
    /// of the groups took its tick with it, so it comes back with the level's own.
    /// </summary>
    [Fact]
    public void An_item_no_longer_in_any_group_is_dropped()
    {
        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), Photo("2")), Group(Photo("3"), Photo("4"))], preselect: false);
        var before = After(shown, ("2", true), ("4", true));

        // 4 has gone, to the bin from another screen say, and 3 alone is no group.
        var without = TickCarryOver.Apply(before, [Group(Photo("1"), Photo("2"))], preselect: false);

        Assert.Equal(["2"], Ticked(without));
        Assert.DoesNotContain(without.SelectMany(group => group.Items), item => item.PhotoId is "3" or "4");

        // Restored, it is in a group again with nothing ticked.
        var restored = TickCarryOver.Apply(without, [Group(Photo("1"), Photo("2")), Group(Photo("3"), Photo("4"))], preselect: false);

        Assert.Equal(["2"], Ticked(restored));
    }

    /// <summary>What goes on screen records each photo as it is now, for the next rescan to compare with.</summary>
    [Fact]
    public void Each_item_is_noted_as_the_photo_stands()
    {
        var photo = Photo("2", size: 123, modifiedAt: Written.AddDays(1), fileModifiedAt: Written.AddDays(2), edited: true);

        var shown = TickCarryOver.Apply(null, [Group(Photo("1"), photo)], preselect: false);

        Assert.Equal(new PhotoVersion(123, Written.AddDays(1), Written.AddDays(2), IsEdited: true), shown[0].Items[1].Version);
        Assert.Equal(PhotoVersion.Of(photo), shown[0].Items[1].Version);
    }
}
