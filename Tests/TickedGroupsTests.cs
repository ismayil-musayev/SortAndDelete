using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

/// <summary>
/// The check the duplicates screen makes before it moves what is ticked to the bin, when the
/// gallery has read the library again since the groups went on screen.
/// </summary>
public class TickedGroupsTests
{
    /// <summary>A file's stamp, as Android's DATE_MODIFIED gives it.</summary>
    private static readonly DateTime Written = new(2026, 7, 1, 12, 0, 5, DateTimeKind.Utc);

    /// <summary>
    /// A photo as a load of the library finds it. Each call makes a new one, as a reload does, so
    /// only what it says can match what was on screen.
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

    /// <summary>A group on screen: the first photo is the one to keep, and <paramref name="ticked"/> are ticked.</summary>
    private static GroupTicks Shown(string[] ticked, params PhotoAsset[] photos) =>
        new(photos[0].Id, [.. photos.Select(photo =>
            new ItemTick(photo.Id, PhotoVersion.Of(photo), ticked.Contains(photo.Id)))]);

    /// <summary>The library as a later load reads it.</summary>
    private static Func<string, PhotoAsset?> Library(params PhotoAsset[] photos)
    {
        var snapshot = new LibrarySnapshot(photos, _ => []);
        return snapshot.Find;
    }

    private static bool Hold(IEnumerable<GroupTicks> onScreen, Func<string, PhotoAsset?> library) =>
        TickedGroups.AreAsShown(TickedGroups.Copies(onScreen), library);

    [Fact]
    public void Every_copy_of_a_group_with_a_tick_is_checked_and_no_other_group()
    {
        GroupTicks[] onScreen =
        [
            Shown(["2"], Photo("1"), Photo("2"), Photo("3")),
            Shown([], Photo("4"), Photo("5")),
        ];

        Assert.Equal(["1", "2", "3"], TickedGroups.Copies(onScreen).Select(copy => copy.PhotoId).ToArray());
    }

    [Fact]
    public void Copies_a_reload_finds_as_they_were_shown_hold()
    {
        GroupTicks[] onScreen = [Shown(["2"], Photo("1"), Photo("2"))];

        Assert.True(Hold(onScreen, Library(Photo("1"), Photo("2"))));
    }

    /// <summary>
    /// The case this exists for: the copy to keep was deleted in another app with the screen up,
    /// so the one ticked beside it is the only one left.
    /// </summary>
    [Fact]
    public void A_copy_to_keep_that_has_gone_from_the_library_does_not_hold()
    {
        GroupTicks[] onScreen = [Shown(["2"], Photo("1"), Photo("2"))];

        Assert.False(Hold(onScreen, Library(Photo("2"))));
    }

    /// <summary>Cropped in another app, the copy to keep no longer shows what the ticked one does.</summary>
    [Fact]
    public void A_copy_to_keep_that_was_edited_does_not_hold()
    {
        GroupTicks[] onScreen = [Shown(["2"], Photo("1"), Photo("2"))];

        // Android rewrites the file: another size, or at least another stamp.
        Assert.False(Hold(onScreen, Library(Photo("1", size: 3_100_000), Photo("2"))));
        Assert.False(Hold(onScreen, Library(Photo("1", fileModifiedAt: Written.AddMinutes(4)), Photo("2"))));

        // iOS keeps the edit beside the original, and moves the item's modification time.
        Assert.False(Hold(onScreen, Library(Photo("1", edited: true), Photo("2"))));
        Assert.False(Hold(onScreen, Library(Photo("1", modifiedAt: Written.AddMinutes(4)), Photo("2"))));
    }

    /// <summary>It isn't what was ticked any more, and the count the user agreed to is off.</summary>
    [Fact]
    public void A_ticked_copy_that_has_gone_or_changed_does_not_hold()
    {
        GroupTicks[] onScreen = [Shown(["2"], Photo("1"), Photo("2"))];

        Assert.False(Hold(onScreen, Library(Photo("1"))));
        Assert.False(Hold(onScreen, Library(Photo("1"), Photo("2", fileModifiedAt: Written.AddMinutes(4)))));
    }

    [Fact]
    public void A_copy_left_unticked_beside_the_ticked_one_counts_too()
    {
        GroupTicks[] onScreen = [Shown(["2"], Photo("1"), Photo("2"), Photo("3"))];

        Assert.False(Hold(onScreen, Library(Photo("1"), Photo("2"))));
    }

    /// <summary>
    /// What usually changes a library with the screen up: a screenshot, a download, a photo
    /// deleted that no tick depends on. None of it stops a move.
    /// </summary>
    [Fact]
    public void Changes_to_photos_outside_the_ticked_groups_do_not_matter()
    {
        GroupTicks[] onScreen =
        [
            Shown(["2"], Photo("1"), Photo("2")),
            Shown([], Photo("4"), Photo("5")),
        ];

        // "5" has gone and "4" was edited, where nothing is ticked; "6" is new.
        Assert.True(Hold(onScreen, Library(Photo("1"), Photo("2"), Photo("4", size: 1), Photo("6"))));
    }

    [Fact]
    public void With_nothing_ticked_there_is_nothing_to_check()
    {
        GroupTicks[] onScreen = [Shown([], Photo("1"), Photo("2"))];

        Assert.Empty(TickedGroups.Copies(onScreen));
        Assert.True(Hold(onScreen, Library()));
    }

    /// <summary>
    /// iOS reports no size until it is asked for one, while a copy went on screen with its size
    /// looked up. So the screen fills the sizes in before it asks; left at 0, every copy would
    /// read as changed.
    /// </summary>
    [Fact]
    public void A_size_not_looked_up_yet_reads_as_a_change()
    {
        GroupTicks[] onScreen = [Shown(["2"], Photo("1"), Photo("2"))];
        PhotoAsset first = Photo("1", size: 0), second = Photo("2", size: 0);

        Assert.False(Hold(onScreen, Library(first, second)));

        first.SizeBytes = 5_000_000;
        second.SizeBytes = 5_000_000;
        Assert.True(Hold(onScreen, Library(first, second)));
    }
}
