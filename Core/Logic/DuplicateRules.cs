using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// What each <see cref="DuplicateLevel"/> demands before two items are called duplicates.
///
/// The levels are not one threshold at five settings — each turns different checks on and
/// off. "Same photo" leans hard on colour agreement because a re-encode cannot change
/// colours; "adjusted" has to drop that same check, because changing colours is precisely
/// what a filter does. Loosening a number would never express that.
/// </summary>
public sealed record DuplicateRules
{
    /// <summary>Differing bits allowed in the gradient hash, out of 64.</summary>
    public required int MaxDistance { get; init; }

    /// <summary>Differing bits allowed in the coarse-structure hash. <see cref="int.MaxValue"/> skips the check.</summary>
    public required int MaxCoarseDistance { get; init; }

    /// <summary>Colour agreement demanded, 0 to 1. Zero skips the check.</summary>
    public required double MinHistogramOverlap { get; init; }

    /// <summary>
    /// Brightness spread both items must clear. Flat, dark and blank-page images fall below
    /// it, and they are excluded rather than matched — down there every image resembles every
    /// other one and the hashes carry no information to tell them apart.
    /// </summary>
    /// <remarks>
    /// Measured: solid colours, a blank page and a dark frame all score under 0.5 and hash
    /// identically to each other, while the least-detailed of 100 real photographs scored 16.
    /// The floors below sit in that gap. Some genuinely dim photographs will fall under it and
    /// simply never be offered as duplicates, which is the failure worth having.
    /// </remarks>
    public required double MinDetail { get; init; }

    /// <summary>Whether quarter turns count as the same picture.</summary>
    public required bool AllowRotation { get; init; }

    /// <summary>
    /// How far apart the two may have been taken, when that is part of the question.
    /// Only the burst and session levels use it — a re-encoded copy carries the date it was
    /// received, not the date the shot was taken, so time says nothing there.
    /// </summary>
    public required TimeSpan? MaxTimeApart { get; init; }

    /// <summary>
    /// Whether the UI may preselect everything except the keeper. Only the levels we can
    /// stand behind do this; the looser ones surface groups and leave the choosing to the user.
    /// </summary>
    public required bool PreselectsKeeper { get; init; }

    /// <summary>
    /// Whether clips are compared perceptually at this level. Only re-encoding means anything
    /// for a video; "adjusted", "same moment" and "similar" are questions about photographs.
    /// Byte-identical clips are still caught everywhere — that tier does not consult this.
    /// </summary>
    public required bool IncludesVideo { get; init; }

    /// <summary>
    /// Fractional difference tolerated between the two aspect ratios. Rescaling preserves
    /// shape, so a mismatch here means a different framing whatever the hashes say; the
    /// slack absorbs rounding on odd pixel dimensions.
    /// </summary>
    public const double AspectTolerance = 0.02;

    public static DuplicateRules For(DuplicateLevel level) => level switch
    {
        // Nothing perceptual runs at this level — byte equality decides, and it is applied
        // at every other level too.
        DuplicateLevel.Exact => new DuplicateRules
        {
            MaxDistance = -1,
            MaxCoarseDistance = -1,
            MinHistogramOverlap = 0,
            MinDetail = 0,
            AllowRotation = false,
            MaxTimeApart = null,
            PreselectsKeeper = true,
            IncludesVideo = true,
        },

        // Re-encoding shifts a hash by very little, so this stays tight and demands that both
        // hashes and the colours all agree.
        //
        // Measured over 100 photographs against manufactured shares (down to 800px at quality
        // 50) and manufactured near-misses (10% and 20% tighter crops, a 5% pan):
        //
        //             gradient hash        coarse hash
        //   copies    up to 10 (p99 8)     up to 6
        //   crops     down to 6            down to 8
        //
        // The gradient hash alone cannot separate those — they overlap between 6 and 10. The
        // coarse hash leaves a one-bit gap at 7, and that is what the threshold below sits on.
        // Two hashes that fail differently, both required to agree: the gradient one finds
        // candidates, the coarse one is the wall. Together: every share found, no crop admitted.
        DuplicateLevel.SamePhoto => new DuplicateRules
        {
            MaxDistance = 8,
            MaxCoarseDistance = 7,
            MinHistogramOverlap = 0.80,
            MinDetail = 12,
            AllowRotation = false,
            MaxTimeApart = null,
            PreselectsKeeper = true,
            IncludesVideo = true,
        },

        // Filters and exposure tweaks move colours wholesale, so the histogram check comes off
        // and the gradient threshold widens to cover the extra drift they cause.
        //
        // The coarse wall stays at 7, which bounds what this level can do: warm, faded and
        // brightened copies land inside it, but a heavy contrast push drifts as far as 20 bits
        // — past where a differently-framed photograph sits — so those are missed rather than
        // risked. Rotation is handled by matching a single orientation across both hashes.
        DuplicateLevel.Adjusted => new DuplicateRules
        {
            MaxDistance = 10,
            MaxCoarseDistance = 7,
            MinHistogramOverlap = 0,
            MinDetail = 12,
            AllowRotation = true,
            MaxTimeApart = null,
            PreselectsKeeper = false,
            IncludesVideo = false,
        },

        // Burst frames differ in real content — someone blinked — so the pixels alone would
        // be a coin flip. The reliable signal is that they were taken seconds apart.
        DuplicateLevel.SameMoment => new DuplicateRules
        {
            MaxDistance = 12,
            MaxCoarseDistance = int.MaxValue,
            MinHistogramOverlap = 0.60,
            MinDetail = 14,
            AllowRotation = false,
            MaxTimeApart = TimeSpan.FromSeconds(10),
            PreselectsKeeper = false,
            IncludesVideo = false,
        },

        // Deliberately broad. This one is for browsing what a session produced, not for
        // deciding anything, which is why nothing is ever preselected here.
        DuplicateLevel.Similar => new DuplicateRules
        {
            MaxDistance = 16,
            MaxCoarseDistance = int.MaxValue,
            MinHistogramOverlap = 0.50,
            MinDetail = 16,
            AllowRotation = false,
            MaxTimeApart = TimeSpan.FromMinutes(10),
            PreselectsKeeper = false,
            IncludesVideo = false,
        },

        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}
