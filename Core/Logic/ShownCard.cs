namespace SortAndDelete.Logic;

/// <summary>
/// The card on the review deck, and which photo a press on the deck's buttons decides.
///
/// A decision belongs to the photo the user was looking at when the finger went down, and to no
/// other. The deck's order is not that: it changes when a photo comes back out of the bin, on an
/// undo and after every decision, while the next picture can take a second to decode. So this
/// keeps what is on the card, apart from the deck: the photo whose picture and label were put on
/// it together, since when a frame showing them has been drawn, and since when the page is in
/// front (a page sliding off it covers the card and still lets presses through to it). A press
/// decides that photo only when it began after all of that had held for the time it takes to
/// see a picture; any earlier press was aimed at something else, and decides nothing.
///
/// Times are milliseconds on one monotonic clock, the caller's.
/// </summary>
/// <param name="minimumLookMs">
/// How long the card must have been up, and in view, before a press can be about it: less than
/// anyone takes to react to a picture, so only presses that can't have been about it are refused.
/// </param>
public sealed class ShownCard(long minimumLookMs)
{
    private long? _drawnAt;
    private long? _pageArrivedAt;

    /// <summary>The photo whose picture and label are on the card; null while the card is off.</summary>
    public string? PhotoId { get; private set; }

    /// <summary>Whether a frame showing that photo's card has been drawn.</summary>
    public bool IsDrawn => PhotoId is not null && _drawnAt is not null;

    /// <summary>
    /// The photo's picture and label have been handed to the card, in one go. Nothing shows them
    /// yet: see <see cref="Drawn"/>.
    /// </summary>
    public void Put(string photoId)
    {
        PhotoId = photoId;
        _drawnAt = null;
    }

    /// <summary>
    /// A frame showing <paramref name="photoId"/>'s card has been drawn. Ignored when another
    /// photo has been put on the card since, or none is on it: that frame is not what is up now.
    /// </summary>
    public void Drawn(string photoId, long at)
    {
        if (PhotoId == photoId && _drawnAt is null)
            _drawnAt = at;
    }

    /// <summary>The card has gone: decided, set aside, or its photo has left the deck.</summary>
    public void TakeOff()
    {
        PhotoId = null;
        _drawnAt = null;
    }

    /// <summary>The page is in front, and whatever slid off it has gone.</summary>
    public void PageArrived(long at) => _pageArrivedAt = at;

    /// <summary>Another page is in front of the card.</summary>
    public void PageLeft() => _pageArrivedAt = null;

    /// <summary>
    /// The photo a press that began at <paramref name="pressBeganAt"/> decides: the one on the
    /// card, if the card had been drawn and in view for the minimum look by then. Null when the
    /// press decides nothing.
    /// </summary>
    public string? DecidedBy(long pressBeganAt)
    {
        if (PhotoId is null || _drawnAt is not { } drawnAt || _pageArrivedAt is not { } arrivedAt)
            return null;

        long inViewSince = Math.Max(drawnAt, arrivedAt);
        return pressBeganAt - inViewSince >= minimumLookMs ? PhotoId : null;
    }
}

/// <summary>What the deck's card does when the deck under it has changed.</summary>
public enum CardChange
{
    /// <summary>The photo on the card is still the front of the deck: the card stays as it is.</summary>
    Stay,

    /// <summary>
    /// The front of the deck is another photo than the one on the card, or no card is up: the
    /// card (if any) goes at once, and the front's comes up when its picture is ready. The card
    /// is the front of the deck or it is off; it never shows a photo from further back.
    /// </summary>
    Replace,

    /// <summary>The deck is empty: the card goes.</summary>
    Done,
}

/// <summary>The review deck's rules for its card, its order and the pictures it keeps decoded.</summary>
public static class DeckCard
{
    /// <summary>What the card does now that the deck is <paramref name="deck"/>, front first.</summary>
    /// <param name="shown">The photo on the card, null when the card is off.</param>
    public static CardChange After(string? shown, IReadOnlyList<string> deck)
    {
        if (deck.Count == 0)
            return CardChange.Done;
        return shown is not null && shown == deck[0] ? CardChange.Stay : CardChange.Replace;
    }

    /// <summary>
    /// The deck after the library and the decisions were read again, on coming back from the bin
    /// or the album picker: the photos still to review (<paramref name="fresh"/>), in the order
    /// the deck had them, with the ones that are new to it (restored from the bin) next in line.
    /// Next in line is behind the card that is up: the photo on the card stays at the front, so
    /// the card never changes under the user except by what they do to it. A tap is aimed at
    /// what is on screen, and a card swapped in a moment before it lands would take a decision
    /// meant for the one it replaced. With no card up, or its photo gone from the deck, the new
    /// ones go to the front.
    /// </summary>
    /// <param name="deck">The deck as it was, front first.</param>
    /// <param name="fresh">The photos still to review now, as the library and the decisions have them.</param>
    /// <param name="shown">The photo on the card, null when the card is off.</param>
    public static List<T> Redeal<T>(IReadOnlyList<T> deck, IReadOnlyList<T> fresh, Func<T, string> idOf, string? shown)
    {
        var freshIds = fresh.Select(idOf).ToHashSet(StringComparer.Ordinal);
        var known = deck.Select(idOf).ToHashSet(StringComparer.Ordinal);

        var staying = deck.Where(item => freshIds.Contains(idOf(item))).ToList();
        var arriving = fresh.Where(item => !known.Contains(idOf(item)));

        var redealt = new List<T>(fresh.Count);
        int front = shown is null ? -1 : staying.FindIndex(item => idOf(item) == shown);
        if (front >= 0)
        {
            redealt.Add(staying[front]);
            staying.RemoveAt(front);
        }
        redealt.AddRange(arriving);
        redealt.AddRange(staying);
        return redealt;
    }

    /// <summary>
    /// The photos whose pictures the deck keeps decoded, most wanted first: the one on the card,
    /// the front of the deck and the one behind it, so that each comes up in one step with its
    /// label, and the one last decided, so that Undo does too. A decoded picture is large, so
    /// nothing else is kept.
    /// </summary>
    public static IReadOnlyList<string> PicturesToKeep(string? shown, IReadOnlyList<string> deck, string? lastDecided)
    {
        var keep = new List<string>(4);
        Add(shown);
        Add(deck.Count > 0 ? deck[0] : null);
        Add(deck.Count > 1 ? deck[1] : null);
        Add(lastDecided);
        return keep;

        void Add(string? photoId)
        {
            if (photoId is not null && !keep.Contains(photoId))
                keep.Add(photoId);
        }
    }
}
