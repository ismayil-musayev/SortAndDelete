using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class QueueBuilderTests
{
    private static PhotoAsset Photo(string id, string takenAt, long size = 0) => new()
    {
        Id = id,
        TakenAt = DateTime.Parse(takenAt),
        SizeBytes = size,
    };

    private static readonly HashSet<string> NoReviews = [];

    [Fact]
    public void Month_queue_is_oldest_first_and_skips_reviewed()
    {
        List<PhotoAsset> photos =
        [
            Photo("c", "2026-07-20"),
            Photo("a", "2026-07-01"),
            Photo("b", "2026-07-10"),
            Photo("other", "2026-06-15"),
        ];

        var queue = QueueBuilder.ForMonth(photos, new HashSet<string> { "b" }, "2026-07");

        Assert.Equal(["a", "c"], queue.Select(p => p.Id));
    }

    [Fact]
    public void Reviewed_photos_never_reappear_resume_for_free()
    {
        List<PhotoAsset> photos = [Photo("a", "2026-07-01"), Photo("b", "2026-07-02")];

        var first = QueueBuilder.ForMonth(photos, NoReviews, "2026-07");
        Assert.Equal(2, first.Count);

        // "App restart": rebuilding with the recorded decision resumes at photo b.
        var resumed = QueueBuilder.ForMonth(photos, new HashSet<string> { "a" }, "2026-07");
        Assert.Equal("b", Assert.Single(resumed).Id);
    }

    [Fact]
    public void Biggest_queue_sorts_by_size_and_ignores_unknown_sizes()
    {
        List<PhotoAsset> photos =
        [
            Photo("small", "2026-07-01", size: 100),
            Photo("huge", "2026-07-02", size: 9_000),
            Photo("unknown", "2026-07-03", size: 0),
            Photo("medium", "2026-07-04", size: 500),
        ];

        var queue = QueueBuilder.Biggest(photos, NoReviews);

        Assert.Equal(["huge", "medium", "small"], queue.Select(p => p.Id));
    }

    [Fact]
    public void LocationText_combines_folder_and_file_name()
    {
        var withFolder = new PhotoAsset { Id = "1", FileName = "IMG_1.jpg", FolderPath = "Pictures/Travel/" };
        var withoutFolder = new PhotoAsset { Id = "2", FileName = "IMG_2.jpg" };

        Assert.Equal("Pictures/Travel/IMG_1.jpg", withFolder.LocationText);
        Assert.Equal("IMG_2.jpg", withoutFolder.LocationText);
    }
}
