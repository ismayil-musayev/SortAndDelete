using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class ShownCardTests
{
    private const long Look = 150;

    /// <summary>A card for <paramref name="photoId"/>, drawn at 1000 on a page that arrived at 500.</summary>
    private static ShownCard Showing(string photoId)
    {
        var card = new ShownCard(Look);
        card.PageArrived(500);
        card.Put(photoId);
        card.Drawn(photoId, 1000);
        return card;
    }

    [Fact]
    public void A_press_decides_the_photo_on_the_card_once_it_has_been_looked_at()
    {
        var card = Showing("p1");

        Assert.Equal("p1", card.DecidedBy(1000 + Look));
        Assert.Equal("p1", card.DecidedBy(5000));
    }

    [Fact]
    public void A_press_that_began_before_the_card_could_be_seen_decides_nothing()
    {
        var card = Showing("p1");

        Assert.Null(card.DecidedBy(999));           // before it was drawn
        Assert.Null(card.DecidedBy(1000));          // as it was drawn
        Assert.Null(card.DecidedBy(1000 + Look - 1)); // sooner than anyone reacts to a picture
    }

    /// <summary>
    /// The press is judged by when it began, not by when it is handled: a press from before the
    /// card was up stays refused however late the click arrives.
    /// </summary>
    [Fact]
    public void A_press_from_before_the_card_was_up_is_refused_however_late_it_is_handled()
    {
        var card = new ShownCard(Look);
        card.PageArrived(0);
        card.Put("p2");

        long pressBeganAt = 900; // while the last card was still flying out
        card.Drawn("p2", 1000);  // the busy UI thread draws the next card, then gets to the click

        Assert.Null(card.DecidedBy(pressBeganAt));
    }

    [Fact]
    public void Nothing_is_decided_while_the_card_is_put_but_not_drawn()
    {
        var card = new ShownCard(Look);
        card.PageArrived(0);
        card.Put("p1");

        Assert.False(card.IsDrawn);
        Assert.Null(card.DecidedBy(10_000));
    }

    [Fact]
    public void Nothing_is_decided_while_the_card_is_off()
    {
        var card = Showing("p1");
        card.TakeOff();

        Assert.Null(card.PhotoId);
        Assert.Null(card.DecidedBy(10_000));
    }

    /// <summary>
    /// The device pass's failure: label and counter switched to the next photo while the card
    /// still pictured the one just binned, and a second tap binned the photo nobody had seen.
    /// Here a photo is on the card only with its picture, and a new one starts unseen.
    /// </summary>
    [Fact]
    public void Putting_the_next_photo_on_the_card_starts_its_look_from_nothing()
    {
        var card = Showing("p1");

        card.Put("p2");
        Assert.Equal("p2", card.PhotoId);
        Assert.Null(card.DecidedBy(10_000)); // not drawn yet: the old picture may still be up

        card.Drawn("p2", 2000);
        Assert.Null(card.DecidedBy(2000 + Look - 1));
        Assert.Equal("p2", card.DecidedBy(2000 + Look));
    }

    [Fact]
    public void A_frame_drawn_for_another_photo_does_not_count()
    {
        var card = new ShownCard(Look);
        card.PageArrived(0);
        card.Put("p1");
        card.Put("p2");

        card.Drawn("p1", 1000); // the frame of the card that was replaced before it was drawn

        Assert.False(card.IsDrawn);
        Assert.Null(card.DecidedBy(10_000));
    }

    [Fact]
    public void A_frame_drawn_after_the_card_went_does_not_bring_it_back()
    {
        var card = new ShownCard(Look);
        card.PageArrived(0);
        card.Put("p1");
        card.TakeOff();

        card.Drawn("p1", 1000);

        Assert.Null(card.DecidedBy(10_000));
    }

    [Fact]
    public void The_look_counts_from_the_first_frame_drawn_not_from_later_ones()
    {
        var card = Showing("p1");

        card.Drawn("p1", 3000); // the card is redrawn, as on every frame of an animation

        Assert.Equal("p1", card.DecidedBy(1000 + Look));
    }

    /// <summary>
    /// The device pass's failure after a restore: Back from the bin, and a tap 25 to 350 ms later
    /// binned the restored photo, dealt to the front of the deck, while the bin page was still
    /// sliding off the card. A press decides nothing until that page has gone.
    /// </summary>
    [Fact]
    public void Nothing_is_decided_while_another_page_is_in_front()
    {
        var card = Showing("p1");

        card.PageLeft();
        Assert.Null(card.DecidedBy(10_000));

        card.Put("restored");        // the deck is dealt again as the page comes back
        card.Drawn("restored", 20_000);
        Assert.Null(card.DecidedBy(20_500)); // still covered by the page sliding off

        card.PageArrived(20_400);
        Assert.Null(card.DecidedBy(20_400 + Look - 1));
        Assert.Equal("restored", card.DecidedBy(20_400 + Look));
    }

    [Fact]
    public void A_card_already_up_when_the_page_arrives_is_looked_at_from_then()
    {
        var card = new ShownCard(Look);
        card.Put("p1");
        card.Drawn("p1", 100);

        Assert.Null(card.DecidedBy(5000)); // the page has not arrived

        card.PageArrived(1000);
        Assert.Null(card.DecidedBy(1000 + Look - 1));
        Assert.Equal("p1", card.DecidedBy(1000 + Look));
    }

    [Fact]
    public void A_card_drawn_after_the_page_arrived_is_looked_at_from_its_frame()
    {
        var card = Showing("p1"); // page at 500, drawn at 1000

        Assert.Null(card.DecidedBy(500 + Look));
        Assert.Equal("p1", card.DecidedBy(1000 + Look));
    }

    // ---------- What the card does when the deck is dealt again ----------

    [Theory]
    // the photo on the card is still at the front
    [InlineData("a", new[] { "a", "b", "c" }, CardChange.Stay)]
    [InlineData("a", new[] { "a" }, CardChange.Stay)]
    // another photo is at the front (Undo put it there): the card never shows one from further back
    [InlineData("b", new[] { "undone", "b", "c" }, CardChange.Replace)]
    [InlineData("c", new[] { "a", "b", "c" }, CardChange.Replace)]
    // the photo on the card left the deck: decided, moved, or reviewed elsewhere
    [InlineData("a", new[] { "b", "c" }, CardChange.Replace)]
    // no card is on, and there is a front to put on it
    [InlineData(null, new[] { "a", "b" }, CardChange.Replace)]
    // the deck is empty
    [InlineData("a", new string[0], CardChange.Done)]
    [InlineData(null, new string[0], CardChange.Done)]
    public void The_card_is_the_front_of_the_deck_or_off(string? shown, string[] deck, CardChange expected) =>
        Assert.Equal(expected, DeckCard.After(shown, deck));

    // ---------- Dealing the deck again after a visit to the bin ----------

    private static string[] Redeal(string[] deck, string[] fresh, string? shown) =>
        [.. DeckCard.Redeal(deck, fresh, id => id, shown)];

    /// <summary>
    /// The device pass's failure, at its root: a photo restored in the bin was dealt to the front
    /// of the deck, in place of the card the user had left and was about to tap. It goes behind
    /// that card now; the card on screen changes only by what the user does to it.
    /// </summary>
    [Fact]
    public void A_restored_photo_goes_behind_the_card_that_is_up()
    {
        // p1 was binned and restored; p2 has been on the card since.
        Assert.Equal(["p2", "p1", "p3", "p4"], Redeal(["p2", "p3", "p4"], ["p1", "p2", "p3", "p4"], shown: "p2"));
    }

    [Fact]
    public void Several_restored_photos_go_behind_the_card_in_the_librarys_order()
    {
        Assert.Equal(["p3", "p1", "p2", "p4"], Redeal(["p3", "p4"], ["p1", "p2", "p3", "p4"], shown: "p3"));
    }

    [Fact]
    public void With_no_card_up_restored_photos_go_to_the_front()
    {
        Assert.Equal(["p1", "p2", "p3"], Redeal(["p2", "p3"], ["p1", "p2", "p3"], shown: null));

        // the deck had been finished: the restored photo is all there is
        Assert.Equal(["p1"], Redeal([], ["p1"], shown: null));
    }

    [Fact]
    public void A_card_whose_photo_has_left_the_deck_does_not_hold_the_front()
    {
        // p2, on the card, was reviewed elsewhere meanwhile; p1 came back
        Assert.Equal(["p1", "p3"], Redeal(["p2", "p3"], ["p1", "p3"], shown: "p2"));
    }

    [Fact]
    public void Nothing_changed_is_the_same_deck()
    {
        Assert.Equal(["p2", "p3", "p4"], Redeal(["p2", "p3", "p4"], ["p2", "p3", "p4"], shown: "p2"));
    }

    [Fact]
    public void The_deck_keeps_its_own_order_and_drops_what_was_reviewed_elsewhere()
    {
        // "later" had put p2 at the end; p3 was reviewed elsewhere; the library lists by date
        Assert.Equal(["p4", "p5", "p2"], Redeal(["p4", "p3", "p5", "p2"], ["p2", "p4", "p5"], shown: "p4"));
    }

    [Fact]
    public void A_card_that_is_not_the_front_comes_to_the_front_rather_than_change()
    {
        // not a state the deck gets into, but if it did: what is on screen stays what is decided
        Assert.Equal(["p3", "p1", "p2", "p4"], Redeal(["p2", "p3", "p4"], ["p1", "p2", "p3", "p4"], shown: "p3"));
    }

    [Fact]
    public void The_redealt_deck_keeps_the_decks_own_items_for_photos_it_had()
    {
        var old = new[] { (Id: "p2", From: "deck"), (Id: "p3", From: "deck") };
        var fresh = new[] { (Id: "p1", From: "library"), (Id: "p2", From: "library"), (Id: "p3", From: "library") };

        var redealt = DeckCard.Redeal(old, fresh, item => item.Id, shown: "p2");

        Assert.Equal([("p2", "deck"), ("p1", "library"), ("p3", "deck")], redealt);
    }

    [Theory]
    [InlineData("a", new[] { "a", "b", "c", "d" }, "z", new[] { "a", "b", "z" })]
    // the front is another photo than the one on the card: both, and the one behind
    [InlineData("b", new[] { "undone", "b", "c" }, "z", new[] { "b", "undone", "z" })]
    // the card is off after a decision: the front, the one behind, and the one decided (for Undo)
    [InlineData(null, new[] { "b", "c", "d" }, "a", new[] { "b", "c", "a" })]
    [InlineData(null, new[] { "b" }, null, new[] { "b" })]
    [InlineData("a", new[] { "a" }, "a", new[] { "a" })]
    [InlineData(null, new string[0], "a", new[] { "a" })]
    [InlineData(null, new string[0], null, new string[0])]
    public void Only_the_pictures_about_to_be_shown_are_kept(string? shown, string[] deck, string? lastDecided, string[] expected) =>
        Assert.Equal(expected, DeckCard.PicturesToKeep(shown, deck, lastDecided));
}
