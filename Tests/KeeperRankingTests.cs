using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class KeeperRankingTests
{
    private static PhotoAsset Photo(
        string id,
        string folder = "DCIM/Camera/",
        string? fileName = null,
        int width = 4000,
        int height = 3000,
        long size = 5_000_000,
        bool favorite = false,
        string takenAt = "2026-07-01T12:00:00") => new()
    {
        Id = id,
        FolderPath = folder,
        FileName = fileName ?? $"{id}.jpg",
        Width = width,
        Height = height,
        SizeBytes = size,
        IsFavorite = favorite,
        TakenAt = DateTime.Parse(takenAt),
    };

    [Theory]
    [InlineData("Pictures/WhatsApp/WhatsApp Images/")]
    [InlineData("WhatsApp/Media/WhatsApp Images/")]
    [InlineData("Bluetooth/")]
    [InlineData("Download/")]
    [InlineData("Telegram/")]
    public void Copies_that_arrived_from_elsewhere_are_recognised(string folder)
    {
        Assert.True(KeeperRanking.IsReceivedCopy(Photo("x", folder: folder)));
        Assert.False(KeeperRanking.IsReceivedCopy(Photo("x", folder: "DCIM/Camera/")));
    }

    [Fact]
    public void WhatsApp_is_recognised_from_the_file_name_alone()
    {
        // Moved out of its folder, but WhatsApp still stamps the name.
        var moved = Photo("x", folder: "Pictures/Saved/", fileName: "IMG-20260115-WA0007.jpg");

        Assert.True(KeeperRanking.IsReceivedCopy(moved));
    }

    [Fact]
    public void The_photo_that_was_taken_here_beats_the_one_that_arrived()
    {
        List<PhotoAsset> group =
        [
            Photo("received", folder: "Pictures/WhatsApp/WhatsApp Images/"),
            Photo("taken", folder: "DCIM/Camera/"),
        ];

        Assert.Equal("taken", KeeperRanking.Best(group).Id);
    }

    [Fact]
    public void A_favourite_is_kept_wherever_it_lives()
    {
        List<PhotoAsset> group =
        [
            Photo("plain", folder: "DCIM/Camera/"),
            Photo("starred", folder: "Pictures/WhatsApp/WhatsApp Images/", favorite: true),
        ];

        Assert.Equal("starred", KeeperRanking.Best(group).Id);
    }

    [Fact]
    public void Between_equals_the_bigger_picture_wins()
    {
        List<PhotoAsset> group =
        [
            Photo("small", width: 1600, height: 1200, size: 900_000),
            Photo("full", width: 4000, height: 3000, size: 5_000_000),
        ];

        Assert.Equal("full", KeeperRanking.Best(group).Id);
    }

    [Fact]
    public void Same_pixels_falls_through_to_file_size()
    {
        List<PhotoAsset> group =
        [
            Photo("recompressed", size: 400_000),
            Photo("original", size: 5_000_000),
        ];

        Assert.Equal("original", KeeperRanking.Best(group).Id);
    }

    [Fact]
    public void Otherwise_identical_copies_still_pick_the_same_keeper_every_run()
    {
        List<PhotoAsset> group = [Photo("b"), Photo("c"), Photo("a")];

        Assert.Equal("a", KeeperRanking.Best(group).Id);
        Assert.Equal("a", KeeperRanking.Best(Enumerable.Reverse(group)).Id);
    }
}
