namespace SortAndDelete.Models;

/// <summary>
/// The library as one load read it: every photo and video once, by id, and the albums from the
/// same load.
/// </summary>
public sealed class LibrarySnapshot
{
    private readonly Dictionary<string, PhotoAsset> _byId;

    /// <summary>
    /// The items of <paramref name="photos"/> in the order the query returned them, each id kept
    /// where it first comes: a MediaStore query that runs while another app changes the library
    /// can return an item twice. The albums are those <paramref name="albumsOf"/> gives for the
    /// items kept.
    /// </summary>
    public LibrarySnapshot(IEnumerable<PhotoAsset> photos, Func<IReadOnlyList<PhotoAsset>, IReadOnlyList<AlbumInfo>> albumsOf)
    {
        List<PhotoAsset> kept = [.. photos.DistinctBy(photo => photo.Id)];
        Photos = kept;
        _byId = kept.ToDictionary(photo => photo.Id);
        Albums = albumsOf(kept);
    }

    /// <summary>Every photo and video, in the order the query returned them.</summary>
    public IReadOnlyList<PhotoAsset> Photos { get; }

    /// <summary>The albums from the same load.</summary>
    public IReadOnlyList<AlbumInfo> Albums { get; }

    /// <summary>The item with <paramref name="id"/>, or null when this load didn't find it.</summary>
    public PhotoAsset? Find(string id) => _byId.GetValueOrDefault(id);
}
