using SQLite;

namespace SortAndDelete.Models;

/// <summary>
/// A cached fingerprint. Computing one means decoding an image, so the scan pays for each
/// item once and every later run — and every change of level — reads these instead.
/// </summary>
[Table("signatures")]
public sealed class SignatureRecord
{
    /// <summary>The photo id for a still, <c>id#frame</c> for one frame of a video.</summary>
    [PrimaryKey]
    public string Key { get; set; } = "";

    [Indexed]
    public string PhotoId { get; set; } = "";

    /// <summary>0 for a still; the sample position's index for a video frame.</summary>
    public int FrameIndex { get; set; }

    /// <summary>
    /// Change token. Ids get reused when a file is replaced in place, so a fingerprint is
    /// only trusted while the size and capture date it was taken from still hold.
    /// </summary>
    public long SizeBytes { get; set; }

    public long TakenAtTicks { get; set; }

    // Hashes are unsigned but SQLite integers are signed, so they round-trip through an
    // unchecked cast — same 64 bits, reinterpreted.
    public long DHash { get; set; }
    public long DHash90 { get; set; }
    public long DHash180 { get; set; }
    public long DHash270 { get; set; }
    public long PHash { get; set; }
    public long PHash90 { get; set; }
    public long PHash180 { get; set; }
    public long PHash270 { get; set; }

    public byte[] Histogram { get; set; } = [];

    public double Detail { get; set; }

    public static string KeyFor(string photoId, int frameIndex) =>
        frameIndex == 0 ? photoId : $"{photoId}#{frameIndex}";

    public static SignatureRecord From(string photoId, int frameIndex, PhotoAsset photo, ImageSignature signature) => new()
    {
        Key = KeyFor(photoId, frameIndex),
        PhotoId = photoId,
        FrameIndex = frameIndex,
        SizeBytes = photo.SizeBytes,
        TakenAtTicks = photo.TakenAt.Ticks,
        DHash = unchecked((long)signature.DHash),
        DHash90 = unchecked((long)signature.DHash90),
        DHash180 = unchecked((long)signature.DHash180),
        DHash270 = unchecked((long)signature.DHash270),
        PHash = unchecked((long)signature.PHash),
        PHash90 = unchecked((long)signature.PHash90),
        PHash180 = unchecked((long)signature.PHash180),
        PHash270 = unchecked((long)signature.PHash270),
        Histogram = signature.Histogram,
        Detail = signature.Detail,
    };

    public ImageSignature ToSignature() => new()
    {
        DHash = unchecked((ulong)DHash),
        DHash90 = unchecked((ulong)DHash90),
        DHash180 = unchecked((ulong)DHash180),
        DHash270 = unchecked((ulong)DHash270),
        PHash = unchecked((ulong)PHash),
        PHash90 = unchecked((ulong)PHash90),
        PHash180 = unchecked((ulong)PHash180),
        PHash270 = unchecked((ulong)PHash270),
        Histogram = Histogram,
        Detail = Detail,
    };

    /// <summary>Whether this fingerprint still describes the file it was taken from.</summary>
    public bool IsCurrentFor(PhotoAsset photo) =>
        SizeBytes == photo.SizeBytes && TakenAtTicks == photo.TakenAt.Ticks;
}

/// <summary>
/// A file's byte hash. Only computed for items that share an exact byte length with another —
/// nothing else can possibly be byte-identical — so this table stays small.
/// </summary>
[Table("content_hashes")]
public sealed class ContentHashRecord
{
    [PrimaryKey]
    public string PhotoId { get; set; } = "";

    public long SizeBytes { get; set; }

    public string Hash { get; set; } = "";
}
