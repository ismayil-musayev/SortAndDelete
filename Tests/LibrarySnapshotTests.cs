using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class LibrarySnapshotTests
{
    private static PhotoAsset Item(string id, string folder = "DCIM/Camera/") => new() { Id = id, FolderPath = folder };

    [Fact]
    public void An_item_the_query_returned_twice_is_kept_once_where_it_first_came()
    {
        // A query that ran while another app deleted photos returned "1" and "2" twice.
        PhotoAsset first = Item("1"), second = Item("2"), third = Item("3");
        PhotoAsset[] rows = [first, second, Item("1"), third, Item("2")];

        var snapshot = new LibrarySnapshot(rows, _ => []);

        Assert.Equal([first, second, third], snapshot.Photos);
        Assert.Same(first, snapshot.Find("1"));
        Assert.Same(second, snapshot.Find("2"));
        Assert.Null(snapshot.Find("4"));
    }

    [Fact]
    public void The_albums_come_from_the_items_kept()
    {
        PhotoAsset[] rows = [Item("1"), Item("2", "Pictures/Trips/"), Item("1"), Item("3")];

        var snapshot = new LibrarySnapshot(rows, FolderFilter.FoldersOf);

        Assert.Equal([2, 1], snapshot.Albums.Select(album => album.Count));
        Assert.Equal(["DCIM/Camera/", "Pictures/Trips/"], snapshot.Albums.Select(album => album.Id));
    }
}
