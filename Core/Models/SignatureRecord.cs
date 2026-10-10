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
    /// Change token. Ids get reused when a file is replaced in place, and an iOS edit leaves
    /// the original's size and capture date alone, so a fingerprint is only trusted while the
    /// size, capture date and modification time it was taken from still hold.
    /// </summary>
    public long SizeBytes { get; set; }

    public long TakenAtTicks { get; set; }

    /// <summary>
    /// 0 where the platform doesn't track modification. Rows cached before this column existed
    /// read back as 0 too, so on Android, which never tracks it, an upgrade keeps the cache.
    /// </summary>
    public long ModifiedAtTicks { get; set; }

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
        ModifiedAtTicks = photo.ModifiedAt.Ticks,
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
        SizeBytes == photo.SizeBytes
        && TakenAtTicks == photo.TakenAt.Ticks
        && ModifiedAtTicks == photo.ModifiedAt.Ticks;
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

    /// <summary>
    /// The item's <see cref="PhotoAsset.FileModifiedAt"/> when it was hashed. 0 where that isn't
    /// tracked (iOS), and for rows hashed before this column existed, which read back as 0 too.
    /// </summary>
    public long FileModifiedAtTicks { get; set; }

    public string Hash { get; set; } = "";

    /// <summary>
    /// Whether the hash is known to be of the file's own bytes, every one of them. Android hands
    /// an app the photos and videos it didn't create with where they were taken blanked out,
    /// unless the app holds ACCESS_MEDIA_LOCATION and asks for the original; hashed like that, two
    /// files that differ only in their location look identical. A row hashed before this column
    /// existed reads back false, as nothing says how it was read. Such a row proves nothing
    /// (<see cref="ProvesIdentityOf"/>), and only a hash of the original replaces it
    /// (<see cref="Logic.HashAdoption"/>).
    /// </summary>
    public bool Original { get; set; }

    /// <summary>
    /// The row for a hash just taken of <paramref name="photo"/>'s own bytes, or for one known to
    /// be of them that takes the file's stamp.
    /// </summary>
    public static ContentHashRecord From(PhotoAsset photo, string hash) => new()
    {
        PhotoId = photo.Id,
        SizeBytes = photo.SizeBytes,
        FileModifiedAtTicks = photo.FileModifiedAt.Ticks,
        Hash = hash,
        Original = true,
    };

    /// <summary>
    /// Whether this hash still describes the file: the size, and the file's stamp where the row
    /// has one. An Android edit such as a rotation rewrites the file in place and can keep its
    /// size, so only the stamp shows it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="PhotoAsset.ModifiedAt"/>: on iOS the hash is of the untouched original,
    /// which an edit never changes (<see cref="PhotoAsset.IsEdited"/> is what keeps an edited item
    /// out of the exact tier), while that time also moves on metadata changes like favouriting and
    /// would re-read whole originals for nothing. A row without a stamp goes by size alone: an iOS
    /// row never has one, and one hashed before stamping is adopted rather than read again, so an
    /// update doesn't re-read every file that shares a size with another. Only those that could
    /// make an identical pair are read again (<see cref="Logic.HashAdoption"/>).
    /// </remarks>
    public bool IsCurrentFor(PhotoAsset photo) =>
        SizeBytes == photo.SizeBytes
        && (FileModifiedAtTicks == 0 || FileModifiedAtTicks == photo.FileModifiedAt.Ticks);

    /// <summary>
    /// Whether this hash can make <paramref name="photo"/> an identical file of another: it still
    /// describes the file, and it is of the file's own bytes. Two files are identical only when
    /// each has such a hash and the two are equal.
    /// </summary>
    public bool ProvesIdentityOf(PhotoAsset photo) => Original && IsCurrentFor(photo);

    /// <summary>
    /// Whether this row is current but unstamped while the item has a stamp to give it. Saved again
    /// stamped (<see cref="From"/>), it notices an in-place edit from then on; left as it is, it
    /// would be taken at its word for good. Whether it is adopted or read again is
    /// <see cref="Logic.HashAdoption"/>'s call.
    /// </summary>
    public bool NeedsStampFrom(PhotoAsset photo) =>
        FileModifiedAtTicks == 0 && photo.FileModifiedAt.Ticks != 0 && IsCurrentFor(photo);
}
