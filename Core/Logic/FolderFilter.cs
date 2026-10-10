using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// The folder filter on Home: its row of chips, which chip is selected, and what each gallery
/// folder is called there. On Android a gallery folder is a relative path ("Pictures/Travel/").
/// Items at the top of the storage have the relative path "/": the storage root, which the
/// filter calls "Main storage". MediaStore merges folders across volumes by relative path, so the
/// root also holds the items at the top of an SD card.
/// </summary>
public static class FolderFilter
{
    /// <summary>What the filter calls the storage root.</summary>
    public const string StorageRootName = "Main storage";

    /// <summary>Pseudo-folder id for the system-wide favorites flag (Samsung/Google "Favourites").</summary>
    public const string FavoritesFolderId = "sortanddelete:favorites";

    /// <summary>
    /// How folder ids compare: ignoring case, otherwise exactly. Shared storage ignores case, so
    /// "DCIM/Camera/" and "dcim/Camera/" are one folder; "/" and "" are different ids. iOS ids are
    /// generated localIdentifiers, so ignoring case is harmless there.
    /// </summary>
    public static StringComparer IdComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>One chip: the folder it filters to (null for All), its label, and whether it is selected.</summary>
    public readonly record struct Chip(string? FolderId, string Label, bool IsSelected = false);

    /// <summary>The chip row, with exactly one chip selected.</summary>
    /// <param name="Chips">The chips in the order they are shown.</param>
    /// <param name="SelectedFolderId">The folder the filter is on now: the selected chip's, null for All.</param>
    /// <param name="FellBack">Whether the folder asked for has no chip, so All is selected instead.</param>
    public sealed record Row(IReadOnlyList<Chip> Chips, string? SelectedFolderId, bool FellBack);

    /// <summary>
    /// Whether a relative path is the storage root: one with no folder in it, so "/" and "" both
    /// are. It is the root <see cref="MoveRouting.RouteFor"/> never moves anything into.
    /// </summary>
    public static bool IsStorageRoot(string relativePath) => Segments(relativePath).Length == 0;

    /// <summary>
    /// What a folder is called: its last folder ("Pictures/Travel/" gives "Travel"), or
    /// <see cref="StorageRootName"/> for the storage root.
    /// </summary>
    public static string NameFor(string relativePath) =>
        Segments(relativePath) is [.., var last] ? last : StorageRootName;

    /// <summary>
    /// Gallery folders in the order the filter lists them: the storage root first, then by name,
    /// then by path where names repeat ("DCIM/Travel/" before "Pictures/Travel/").
    /// </summary>
    public static List<AlbumInfo> InDisplayOrder(IEnumerable<AlbumInfo> folders) => folders
        .OrderBy(folder => IsStorageRoot(folder.Id) ? 0 : 1)
        .ThenBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(folder => folder.Id, IdComparer)
        .ToList();

    /// <summary>
    /// The folders that hold something, counted from the items in them (on Android every item
    /// carries its folder's relative path), in <see cref="InDisplayOrder"/>. Spellings that differ
    /// only in case are one folder, under the spelling most items use (the first in ordinal order
    /// when tied). Items with an empty or blank relative path are left out.
    /// </summary>
    public static List<AlbumInfo> FoldersOf(IEnumerable<PhotoAsset> photos) => InDisplayOrder(photos
        .Select(photo => photo.FolderPath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .GroupBy(path => path, IdComparer)
        .Select(paths =>
        {
            var id = MostCommon(paths);
            return new AlbumInfo { Id = id, Name = NameFor(id), Count = paths.Count(), Subtitle = id };
        }));

    /// <summary>
    /// The items in the folder <paramref name="folderId"/> (a relative path on Android): those whose
    /// relative path matches it by <see cref="IdComparer"/>, every spelling the folder has.
    /// </summary>
    public static IEnumerable<PhotoAsset> ItemsIn(IEnumerable<PhotoAsset> photos, string folderId) =>
        photos.Where(photo => IdComparer.Equals(photo.FolderPath, folderId));

    /// <summary>
    /// The chip row: "All folders", then "⭐ Favourites" while there are favourites, then a chip
    /// for every album with something in it, in the albums' order. Folders picked in the system
    /// browser (count unknown, -1) and empty folders found on disk (0) get none: they would show
    /// nothing. The chip whose folder id matches <paramref name="selectedFolderId"/> by
    /// <see cref="IdComparer"/> (null for All) is selected, and the selection takes the chip's
    /// spelling; when there is none (the folder emptied, or its photos moved), All is.
    /// </summary>
    public static Row Build(IEnumerable<AlbumInfo> albums, int favoriteCount, string? selectedFolderId)
    {
        List<Chip> chips = [new Chip(null, "All folders")];

        // The one virtual album Android exposes system-wide: Samsung/Google gallery "Favourites".
        if (favoriteCount > 0)
            chips.Add(new Chip(FavoritesFolderId, $"⭐ Favourites · {favoriteCount}"));

        foreach (var album in albums.Where(a => a.Count > 0))
            chips.Add(new Chip(album.Id, $"{album.Name} · {album.Count}"));

        int selected = chips.FindIndex(chip => IdComparer.Equals(chip.FolderId, selectedFolderId));
        bool fellBack = selected < 0;
        if (fellBack)
            selected = 0; // All

        chips[selected] = chips[selected] with { IsSelected = true };
        return new Row(chips, chips[selected].FolderId, fellBack);
    }

    /// <summary>The spelling used most often, the first in ordinal order when tied.</summary>
    private static string MostCommon(IEnumerable<string> spellings) => spellings
        .GroupBy(spelling => spelling, StringComparer.Ordinal)
        .OrderByDescending(same => same.Count())
        .ThenBy(same => same.Key, StringComparer.Ordinal)
        .First().Key;

    private static string[] Segments(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
