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
        .ThenBy(folder => folder.Id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The chip row: "All folders", then "⭐ Favourites" while there are favourites, then a chip
    /// for every album with something in it, in the albums' order. Folders picked in the system
    /// browser (count unknown, -1) and empty folders found on disk (0) get none: they would show
    /// nothing. The chip whose folder id is exactly <paramref name="selectedFolderId"/> (null for
    /// All) is selected; when there is none (the folder emptied, or its photos moved), All is.
    /// </summary>
    public static Row Build(IEnumerable<AlbumInfo> albums, int favoriteCount, string? selectedFolderId)
    {
        List<Chip> chips = [new Chip(null, "All folders")];

        // The one virtual album Android exposes system-wide: Samsung/Google gallery "Favourites".
        if (favoriteCount > 0)
            chips.Add(new Chip(FavoritesFolderId, $"⭐ Favourites · {favoriteCount}"));

        foreach (var album in albums.Where(a => a.Count > 0))
            chips.Add(new Chip(album.Id, $"{album.Name} · {album.Count}"));

        int selected = chips.FindIndex(chip => chip.FolderId == selectedFolderId);
        bool fellBack = selected < 0;
        if (fellBack)
            selected = 0; // All

        chips[selected] = chips[selected] with { IsSelected = true };
        return new Row(chips, chips[selected].FolderId, fellBack);
    }

    private static string[] Segments(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
