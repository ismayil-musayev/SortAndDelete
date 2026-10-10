using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class TrashSettlementTests
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private static IReadOnlySet<string> Set(params string[] ids) => ids.ToHashSet();

    /// <summary>Every item on a volume that is mounted.</summary>
    private static bool? Here(string id) => true;

    /// <summary>Items whose id starts with "sd" are on a card that is out; the rest are on storage that is there.</summary>
    private static bool? CardIsOut(string id) => !id.StartsWith("sd", StringComparison.Ordinal);

    /// <summary>Decisions from before volumes were kept: nothing says where the item was.</summary>
    private static bool? Unknown(string id) => null;

    /// <summary>Settles with full access to the library, as on most phones.</summary>
    private static TrashResult Settle(string[] asked, IReadOnlySet<string> handledByRequest,
        IReadOnlySet<string>? inLibrary, IReadOnlySet<string>? inSystemTrash,
        Func<string, bool?> isOnStorageThatIsThere, bool someStorageIsAway, bool seesWholeLibrary = true) =>
        TrashSettlement.Settle(asked, handledByRequest, inLibrary, inSystemTrash,
            isOnStorageThatIsThere, someStorageIsAway, seesWholeLibrary);

    private static void AssertSettled(TrashResult result,
        string[]? removed = null, string[]? missing = null, string[]? unreachable = null, string[]? left = null)
    {
        Assert.Equal((removed ?? []).Order(), result.Removed.Order());
        Assert.Equal((missing ?? []).Order(), result.Missing.Order());
        Assert.Equal((unreachable ?? []).Order(), result.Unreachable.Order());
        Assert.Equal((left ?? []).Order(), result.Left.Order());
    }

    // ---------- Android 11+: the request moves items to the system trash ----------

    [Fact]
    public void Items_found_in_the_system_trash_were_removed()
    {
        var result = Settle(["a", "b"], handledByRequest: Set("a", "b"), inLibrary: Nothing, inSystemTrash: Set("a", "b"), Here, false);

        AssertSettled(result, removed: ["a", "b"]);
        Assert.True(result.EmptiesBin);
    }

    [Fact]
    public void A_denied_request_leaves_everything_in_the_bin()
    {
        var result = Settle(["a", "b"], Nothing, inLibrary: Set("a", "b"), inSystemTrash: Nothing, Here, false);

        AssertSettled(result, left: ["a", "b"]);
        Assert.False(result.EmptiesBin);
    }

    [Fact]
    public void One_trashed_elsewhere_and_one_denied_are_told_apart()
    {
        var result = Settle(["gone", "denied"], Nothing,
            inLibrary: Set("denied"), inSystemTrash: Set("gone"), Here, false);

        AssertSettled(result, removed: ["gone"], left: ["denied"]);
    }

    /// <summary>
    /// The device pass's failure on Android 13 and 15: an internal photo and a card photo in the
    /// bin, the card taken out, "Move to trash". Android asks about the internal one only. The
    /// card photo was listed IN SYSTEM TRASH and counted as freed, with its file untouched.
    /// </summary>
    [Fact]
    public void A_card_photo_is_not_removed_while_its_card_is_out()
    {
        var result = Settle(["int", "sd"], handledByRequest: Set("int"),
            inLibrary: Nothing, inSystemTrash: Set("int"), CardIsOut, someStorageIsAway: true);

        AssertSettled(result, removed: ["int"], unreachable: ["sd"]);
        Assert.False(result.EmptiesBin);
    }

    [Fact]
    public void A_bin_of_card_photos_only_stays_as_it_is_while_the_card_is_out()
    {
        var result = Settle(["sd1", "sd2"], Nothing,
            inLibrary: Nothing, inSystemTrash: Nothing, CardIsOut, someStorageIsAway: true);

        AssertSettled(result, unreachable: ["sd1", "sd2"]);
    }

    /// <summary>
    /// What the request itself sent to the trash, with the user's consent, counts as removed once
    /// the library has let go of it, also where the trash can't be listed: it was in the library
    /// when the request began, and its storage is still there.
    /// </summary>
    [Fact]
    public void What_the_request_trashed_was_removed_even_where_the_trash_cannot_be_listed()
    {
        AssertSettled(Settle(["a"], handledByRequest: Set("a"), inLibrary: Nothing, inSystemTrash: Nothing, Here, false),
            removed: ["a"]);
        AssertSettled(Settle(["a"], handledByRequest: Set("a"), inLibrary: Nothing, inSystemTrash: null, Here, false),
            removed: ["a"]);
        AssertSettled(Settle(["a"], handledByRequest: Set("a"), inLibrary: Nothing, inSystemTrash: Nothing, Unknown, false),
            removed: ["a"]);
    }

    [Fact]
    public void What_the_request_handled_is_not_removed_while_its_storage_may_be_the_one_that_is_away()
    {
        // Its volume isn't known (or the volumes couldn't be asked), and one is away: it may have
        // left the library with its card rather than by the request.
        var result = Settle(["a"], handledByRequest: Set("a"), inLibrary: Nothing, inSystemTrash: Nothing, Unknown, someStorageIsAway: true);

        AssertSettled(result, unreachable: ["a"]);
    }

    [Fact]
    public void A_card_pulled_while_the_request_ran_does_not_make_its_photos_removed()
    {
        // Sent with the user's consent, but the card went out before Android got to it: the
        // library no longer lists it, the trash doesn't have it, and its volume is away.
        var result = Settle(["sd"], handledByRequest: Set("sd"),
            inLibrary: Nothing, inSystemTrash: Nothing, CardIsOut, someStorageIsAway: true);

        AssertSettled(result, unreachable: ["sd"]);
    }

    // ---------- Android 10: the request deletes for good ----------

    /// <summary>
    /// The device pass's failure on Android 10: the card photo's row is hidden while the card is
    /// out, the request skipped it as "deleted already", deleted the internal photo, and said all
    /// were gone. The card photo was committed and added to "freed" with its file still on the card.
    /// </summary>
    [Fact]
    public void On_Android_10_a_card_photo_is_not_deleted_while_its_card_is_out()
    {
        var result = Settle(["int", "sd"], handledByRequest: Set("int"),
            inLibrary: Nothing, inSystemTrash: Nothing, CardIsOut, someStorageIsAway: true);

        AssertSettled(result, removed: ["int"], unreachable: ["sd"]);
    }

    [Fact]
    public void On_Android_10_only_what_the_request_deleted_and_checked_is_removed()
    {
        // The user backed out of the folder picker: nothing was deleted.
        var backedOut = Settle(["a", "b"], Nothing, inLibrary: Set("a", "b"), inSystemTrash: Nothing, Here, false);
        AssertSettled(backedOut, left: ["a", "b"]);

        // One file couldn't be deleted (its size no longer matched): the other still was.
        var partly = Settle(["ok", "guarded"], handledByRequest: Set("ok"),
            inLibrary: Set("guarded"), inSystemTrash: Nothing, Here, false);
        AssertSettled(partly, removed: ["ok"], left: ["guarded"]);
    }

    // ---------- Items the library doesn't list, for no reason it gives ----------

    [Fact]
    public void An_item_gone_from_storage_that_is_there_is_forgotten_not_counted_as_removed()
    {
        var result = Settle(["mine", "elsewhere"], handledByRequest: Set("mine"),
            inLibrary: Nothing, inSystemTrash: Nothing, Here, false);

        AssertSettled(result, removed: ["mine"], missing: ["elsewhere"]);
        Assert.True(result.EmptiesBin);
    }

    [Fact]
    public void An_item_of_unknown_volume_is_out_of_reach_only_while_some_storage_is_away()
    {
        var away = Settle(["old"], Nothing, Nothing, Nothing, Unknown, someStorageIsAway: true);
        AssertSettled(away, unreachable: ["old"]);

        var allThere = Settle(["old"], Nothing, Nothing, Nothing, Unknown, someStorageIsAway: false);
        AssertSettled(allThere, missing: ["old"]);
    }

    [Fact]
    public void An_item_known_to_be_on_storage_that_is_there_is_not_held_back_by_a_card_that_is_out()
    {
        var result = Settle(["internal"], Nothing, Nothing, Nothing, Here, someStorageIsAway: true);

        AssertSettled(result, missing: ["internal"]);
    }

    /// <summary>
    /// With "selected photos only" (Android 14+), or no access at all, the library lists what the
    /// app may see, and a photo outside that looks deleted. Its decision is neither recorded as
    /// carried out nor forgotten: it stays in the bin until the photo can be seen again.
    /// </summary>
    [Fact]
    public void Without_access_to_the_whole_library_what_is_not_listed_is_out_of_reach_not_gone()
    {
        var result = Settle(["unselected", "selected", "trashed"], handledByRequest: Set("trashed"),
            inLibrary: Set("selected"), inSystemTrash: Set("trashed"), Here, someStorageIsAway: false,
            seesWholeLibrary: false);

        AssertSettled(result, removed: ["trashed"], unreachable: ["unselected"], left: ["selected"]);
    }

    // ---------- Nothing may be recorded on a guess ----------

    [Fact]
    public void What_the_library_still_lists_is_never_removed_whatever_the_request_says()
    {
        var result = Settle(["a"], handledByRequest: Set("a"),
            inLibrary: Set("a"), inSystemTrash: Set("a"), Here, false);

        AssertSettled(result, left: ["a"]);
    }

    [Fact]
    public void When_the_library_cannot_be_asked_nothing_is_taken_for_gone()
    {
        var result = Settle(["handled", "trashed", "other"], handledByRequest: Set("handled"),
            inLibrary: null, inSystemTrash: Set("trashed"), CardIsOut, someStorageIsAway: true);

        AssertSettled(result, left: ["handled", "trashed", "other"]);
    }

    [Fact]
    public void When_the_trash_cannot_be_asked_what_the_request_did_not_handle_is_left()
    {
        var result = Settle(["a", "b"], Nothing, inLibrary: Set("b"), inSystemTrash: null, Here, false);

        AssertSettled(result, left: ["a", "b"]);
    }

    [Fact]
    public void Every_item_asked_for_lands_in_exactly_one_set()
    {
        string[] asked = ["trashed", "kept", "sd", "elsewhere", "deleted", "trashed"];
        var result = Settle(asked, handledByRequest: Set("deleted", "trashed"),
            inLibrary: Set("kept"), inSystemTrash: Set("trashed"), CardIsOut, someStorageIsAway: true);

        AssertSettled(result, removed: ["deleted", "trashed"], missing: ["elsewhere"], unreachable: ["sd"], left: ["kept"]);
        Assert.Equal(asked.Distinct().Count(),
            result.Removed.Count + result.Missing.Count + result.Unreachable.Count + result.Left.Count);
    }

    [Fact]
    public void Nothing_asked_for_is_nothing_done()
    {
        var result = Settle([], Nothing, Nothing, Nothing, Here, false);

        AssertSettled(result);
        Assert.True(result.EmptiesBin);
        Assert.True(TrashResult.Nothing.EmptiesBin);
    }

    [Fact]
    public void The_ready_made_results_put_every_item_in_one_set()
    {
        AssertSettled(TrashResult.AllRemoved(["a", "b"]), removed: ["a", "b"]);
        AssertSettled(TrashResult.AllLeft(["a", "b"]), left: ["a", "b"]);
        Assert.True(TrashResult.AllRemoved(["a"]).EmptiesBin);
        Assert.False(TrashResult.AllLeft(["a"]).EmptiesBin);
    }

    // ---------- Restoring from the system trash ----------

    [Theory]
    // back in the library
    [InlineData(true, false, true, false, true, RestoreOutcome.Restored)]
    [InlineData(true, null, null, true, false, RestoreOutcome.Restored)]
    // still in the trash: the user said no, or Android refused
    [InlineData(false, true, true, false, true, RestoreOutcome.NotRestored)]
    // it couldn't be asked
    [InlineData(null, false, true, false, true, RestoreOutcome.NotRestored)]
    [InlineData(false, null, true, false, true, RestoreOutcome.NotRestored)]
    // in neither, on storage that is there: purged, or removed elsewhere
    [InlineData(false, false, true, false, true, RestoreOutcome.Gone)]
    [InlineData(false, false, true, true, true, RestoreOutcome.Gone)]
    [InlineData(false, false, null, false, true, RestoreOutcome.Gone)]
    // in neither, and its card is out, or it may be on one that is
    [InlineData(false, false, false, true, true, RestoreOutcome.Unreachable)]
    [InlineData(false, false, null, true, true, RestoreOutcome.Unreachable)]
    // in neither, but the app sees selected photos only: it may be outside them
    [InlineData(false, false, true, false, false, RestoreOutcome.Unreachable)]
    public void A_restore_is_settled_by_where_the_item_is_afterwards(
        bool? inLibrary, bool? inSystemTrash, bool? onStorageThatIsThere, bool someStorageIsAway,
        bool seesWholeLibrary, RestoreOutcome expected) =>
        Assert.Equal(expected, TrashSettlement.SettleRestore(
            inLibrary, inSystemTrash, onStorageThatIsThere, someStorageIsAway, seesWholeLibrary));
}
