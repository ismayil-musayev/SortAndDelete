using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class FolderFilterTests
{
    /// <summary>A gallery folder as Android lists it, named by its relative path.</summary>
    private static AlbumInfo Folder(string relativePath, int count) => new()
    {
        Id = relativePath,
        Name = FolderFilter.NameFor(relativePath),
        Count = count,
        Subtitle = relativePath,
    };

    private const string BrowsedId = "saf:content://com.android.externalstorage.documents/tree/primary%3AScans";

    /// <summary>A folder picked in the system folder browser: its count is never looked up.</summary>
    private static readonly AlbumInfo Browsed = new()
    {
        Id = BrowsedId,
        Name = "Scans",
        Count = -1,
    };

    private static readonly AlbumInfo[] Gallery =
    [
        Folder("/", 45),
        Folder("DCIM/Camera/", 120),
        Folder("Pictures/Old/", 0), // still on disk, emptied
        Folder("Pictures/Travel/", 8),
        Browsed,
    ];

    private static List<string> Labels(FolderFilter.Row row) => row.Chips.Select(chip => chip.Label).ToList();

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("//")]
    public void The_storage_root_is_called_Main_storage(string relativePath)
    {
        Assert.True(FolderFilter.IsStorageRoot(relativePath));
        Assert.Equal("Main storage", FolderFilter.NameFor(relativePath));
    }

    [Theory]
    [InlineData("Bluetooth/", "Bluetooth")]
    [InlineData("Bluetooth", "Bluetooth")]
    [InlineData("Pictures/Travel/", "Travel")]
    [InlineData("Pictures/Travel", "Travel")]
    [InlineData("DCIM/Camera/2024/", "2024")]
    [InlineData("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/", "WhatsApp Images")]
    public void A_folder_is_called_by_its_last_folder(string relativePath, string name)
    {
        Assert.False(FolderFilter.IsStorageRoot(relativePath));
        Assert.Equal(name, FolderFilter.NameFor(relativePath));
    }

    [Fact]
    public void With_nothing_selected_All_is_selected()
    {
        var row = FolderFilter.Build(Gallery, favoriteCount: 0, selectedFolderId: null);

        Assert.Equal(["All folders", "Main storage · 45", "Camera · 120", "Travel · 8"], Labels(row));
        Assert.Equal(["/", "DCIM/Camera/", "Pictures/Travel/"], row.Chips.Skip(1).Select(chip => chip.FolderId));
        Assert.Equal("All folders", Assert.Single(row.Chips, chip => chip.IsSelected).Label);
        Assert.Null(row.Chips[0].FolderId);
        Assert.Null(row.SelectedFolderId);
        Assert.False(row.FellBack);
    }

    [Fact]
    public void Favourites_get_a_chip_after_All_only_while_there_are_some()
    {
        AlbumInfo[] albums = [Folder("DCIM/Camera/", 120)];

        var none = FolderFilter.Build(albums, favoriteCount: 0, selectedFolderId: null);
        var some = FolderFilter.Build(albums, favoriteCount: 3, selectedFolderId: null);

        Assert.Equal(["All folders", "Camera · 120"], Labels(none));
        Assert.Equal(["All folders", "⭐ Favourites · 3", "Camera · 120"], Labels(some));
        Assert.Equal(FolderFilter.FavoritesFolderId, some.Chips[1].FolderId);
    }

    [Fact]
    public void Empty_and_browsed_folders_get_no_chip()
    {
        AlbumInfo[] albums = [Folder("Pictures/Old/", 0), Browsed, Folder("DCIM/Camera/", 120)];

        var row = FolderFilter.Build(albums, favoriteCount: 0, selectedFolderId: null);

        Assert.Equal(["All folders", "Camera · 120"], Labels(row));
    }

    [Theory]
    [InlineData("Pictures/Travel/")]
    [InlineData("/")]
    [InlineData(FolderFilter.FavoritesFolderId)]
    public void A_selection_stays_while_its_folder_is_listed(string selected)
    {
        var row = FolderFilter.Build(Gallery, favoriteCount: 3, selected);

        Assert.Equal(selected, Assert.Single(row.Chips, chip => chip.IsSelected).FolderId);
        Assert.Equal(selected, row.SelectedFolderId);
        Assert.False(row.FellBack);
    }

    [Theory]
    [InlineData("Bluetooth/")] // gone from the gallery: its photos moved, or went to the system trash
    [InlineData("Pictures/Old/")] // still on disk, but empty
    [InlineData(BrowsedId)] // picked in the system browser: its count is unknown
    public void A_selection_without_a_chip_falls_back_to_All(string selected)
    {
        var row = FolderFilter.Build(Gallery, favoriteCount: 3, selected);

        Assert.Equal("All folders", Assert.Single(row.Chips, chip => chip.IsSelected).Label);
        Assert.Null(row.SelectedFolderId);
        Assert.True(row.FellBack);
    }

    [Fact]
    public void A_selection_in_another_case_keeps_its_chip()
    {
        // Shared storage ignores case: "pictures/travel/" is the listed "Pictures/Travel/".
        var row = FolderFilter.Build(Gallery, favoriteCount: 0, "pictures/travel/");

        Assert.Equal("Pictures/Travel/", Assert.Single(row.Chips, chip => chip.IsSelected).FolderId);
        Assert.Equal("Pictures/Travel/", row.SelectedFolderId);
        Assert.False(row.FellBack);
    }

    [Fact]
    public void No_folder_is_not_the_storage_root()
    {
        // "" is no relative path at all, so in no folder; "/" is the storage root, listed here.
        var row = FolderFilter.Build(Gallery, favoriteCount: 0, "");

        Assert.Equal("All folders", Assert.Single(row.Chips, chip => chip.IsSelected).Label);
        Assert.Null(row.SelectedFolderId);
        Assert.True(row.FellBack);
    }

    [Theory]
    [InlineData("DCIM/Camera/", "dcim/Camera/", true)]
    [InlineData("Pictures/Travel/", "PICTURES/TRAVEL/", true)]
    [InlineData("/", "/", true)]
    [InlineData("/", "", false)]
    [InlineData("DCIM/Camera/", "DCIM/Camera", false)]
    [InlineData("DCIM/Camera/", "DCIM/Camera2/", false)]
    public void Folder_ids_compare_ignoring_case_and_otherwise_exactly(string id, string other, bool same)
    {
        Assert.Equal(same, FolderFilter.IdComparer.Equals(id, other));
        Assert.Equal(same, FolderFilter.IdComparer.Equals(other, id));
        if (same)
            Assert.Equal(FolderFilter.IdComparer.GetHashCode(id), FolderFilter.IdComparer.GetHashCode(other));
    }

    [Fact]
    public void Favourites_fall_back_to_All_once_none_are_left()
    {
        var row = FolderFilter.Build(Gallery, favoriteCount: 0, FolderFilter.FavoritesFolderId);

        Assert.DoesNotContain(row.Chips, chip => chip.FolderId == FolderFilter.FavoritesFolderId);
        Assert.Equal("All folders", Assert.Single(row.Chips, chip => chip.IsSelected).Label);
        Assert.Null(row.SelectedFolderId);
        Assert.True(row.FellBack);
    }

    [Fact]
    public void Labels_give_the_name_then_the_count()
    {
        AlbumInfo[] albums =
        [
            Folder("/", 45),
            Folder("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/", 1234),
        ];

        var row = FolderFilter.Build(albums, favoriteCount: 7, selectedFolderId: null);

        Assert.Equal(["All folders", "⭐ Favourites · 7", "Main storage · 45", "WhatsApp Images · 1234"], Labels(row));
    }

    [Fact]
    public void The_storage_root_comes_first()
    {
        // In no particular order, as MediaStore and the walk of the folders on disk find them.
        AlbumInfo[] found =
        [
            Folder("Pictures/Travel/", 8),
            Folder("Movies/", 3),
            Folder("/", 45),
            Folder("DCIM/Camera/", 120),
            Folder("Pictures/Abc/", 2),
        ];

        var row = FolderFilter.Build(FolderFilter.InDisplayOrder(found), favoriteCount: 2, selectedFolderId: null);

        Assert.Equal(
            ["All folders", "⭐ Favourites · 2", "Main storage · 45", "Abc · 2", "Camera · 120", "Movies · 3", "Travel · 8"],
            Labels(row));
    }

    [Fact]
    public void Folders_of_the_same_name_follow_by_path()
    {
        var ordered = FolderFilter.InDisplayOrder([Folder("Pictures/Travel/", 8), Folder("DCIM/Travel/", 3)]);

        Assert.Equal(["DCIM/Travel/", "Pictures/Travel/"], ordered.Select(folder => folder.Id));
    }
}
