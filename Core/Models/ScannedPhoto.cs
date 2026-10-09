namespace SortAndDelete.Models;

/// <summary>One library item plus whatever the duplicate scan managed to derive from it.</summary>
public sealed class ScannedPhoto
{
    public required PhotoAsset Photo { get; init; }

    /// <summary>
    /// The still picture's fingerprint, or null when it was never taken — an unreadable or
    /// unsupported file. Always null for videos, which carry <see cref="Frames"/> instead.
    /// </summary>
    public ImageSignature? Signature { get; init; }

    /// <summary>
    /// Fingerprints of frames sampled across a video: one slot per
    /// <c>VideoFingerprint.SamplePositions</c> entry, in that order, null where that frame
    /// couldn't be read. The gap is kept so two clips' frames still pair up by position. Null
    /// for stills, for a clip with no readable frame, and for videos whose duration is unique in
    /// the library — nothing else is the same length, so nothing can be a re-encode of it and
    /// pulling frames out would be wasted work.
    /// </summary>
    public IReadOnlyList<ImageSignature?>? Frames { get; init; }

    /// <summary>
    /// Hash of the file bytes, or null when it was never computed. Only items sharing an exact
    /// byte length are hashed, since nothing else can possibly be byte-identical. The scan leaves
    /// it null for an edited item (<see cref="PhotoAsset.IsEdited"/>), and grouping ignores one
    /// regardless.
    /// </summary>
    public string? ContentHash { get; init; }
}
