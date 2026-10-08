using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class MonthAggregatorTests
{
    private static PhotoAsset Photo(string id, string takenAt) => new()
    {
        Id = id,
        TakenAt = DateTime.Parse(takenAt),
    };

    [Fact]
    public void Groups_by_month_newest_first_with_progress()
    {
        List<PhotoAsset> photos =
        [
            Photo("jul1", "2026-07-05"),
            Photo("jul2", "2026-07-10"),
            Photo("may1", "2026-05-09"),
        ];

        var groups = MonthAggregator.Build(photos, new HashSet<string> { "jul1" });

        Assert.Equal(2, groups.Count);
        Assert.Equal("2026-07", groups[0].MonthKey); // newest month first
        Assert.Equal("2026-05", groups[1].MonthKey);

        Assert.Equal(2, groups[0].TotalCount);
        Assert.Equal(1, groups[0].ReviewedCount);
        Assert.Equal(0.5, groups[0].Progress);
        Assert.False(groups[0].IsDone);

        Assert.False(groups[1].IsDone);
        Assert.Equal(0, groups[1].Progress);
    }

    [Fact]
    public void Cover_is_the_newest_photo_of_the_month()
    {
        List<PhotoAsset> photos =
        [
            Photo("old", "2026-07-01"),
            Photo("new", "2026-07-30"),
        ];

        var groups = MonthAggregator.Build(photos, new HashSet<string>());

        Assert.Equal("new", Assert.Single(groups).CoverPhotoId);
    }

    [Fact]
    public void Fully_reviewed_month_is_done()
    {
        List<PhotoAsset> photos = [Photo("a", "2026-06-01"), Photo("b", "2026-06-02")];

        var groups = MonthAggregator.Build(photos, new HashSet<string> { "a", "b" });

        Assert.True(Assert.Single(groups).IsDone);
    }

    [Fact]
    public void DisplayFor_falls_back_to_raw_key_when_unparsable()
    {
        Assert.Equal("garbage", MonthGroup.DisplayFor("garbage"));
    }
}
