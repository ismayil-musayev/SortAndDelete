using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class FolderWalkCacheTests
{
    private static readonly string[] OnDisk = ["Pictures/Travel/", "Pictures/Old/", "DCIM/Camera/"];

    /// <summary>A walk that finds <paramref name="found"/>, counts its runs, and can act while it runs.</summary>
    private sealed class Walk(params string[] found)
    {
        public int Runs { get; private set; }

        /// <summary>What happens on other threads while the walk runs.</summary>
        public Action? Meanwhile { get; init; }

        public IReadOnlyList<string> Run()
        {
            Runs++;
            Meanwhile?.Invoke();
            return found;
        }
    }

    [Fact]
    public void The_first_list_walks_and_later_lists_reuse_the_walk()
    {
        var cache = new FolderWalkCache();
        var walk = new Walk(OnDisk);

        Assert.Equal(OnDisk, cache.Folders(walk.Run));
        Assert.Equal(OnDisk, cache.Folders(walk.Run));
        Assert.Equal(1, walk.Runs);
    }

    [Fact]
    public void A_folder_made_after_the_walk_is_listed_once_the_walk_is_invalidated()
    {
        var cache = new FolderWalkCache();
        cache.Folders(new Walk(OnDisk).Run);
        var later = new Walk([.. OnDisk, "Pictures/Trip/"]);

        Assert.Equal(OnDisk, cache.Folders(later.Run));
        Assert.Equal(0, later.Runs);

        cache.Invalidate();

        Assert.Equal([.. OnDisk, "Pictures/Trip/"], cache.Folders(later.Run));
        Assert.Equal(1, later.Runs);
    }

    [Fact]
    public void A_walk_that_began_before_an_invalidation_answers_its_list_but_is_not_kept()
    {
        var cache = new FolderWalkCache();
        var stale = new Walk(OnDisk) { Meanwhile = cache.Invalidate };

        Assert.Equal(OnDisk, cache.Folders(stale.Run));

        var fresh = new Walk("Pictures/Travel/");
        Assert.Equal(["Pictures/Travel/"], cache.Folders(fresh.Run));
        Assert.Equal(1, fresh.Runs);
    }

    [Fact]
    public void A_stale_walk_does_not_replace_the_walk_after_the_invalidation()
    {
        var cache = new FolderWalkCache();
        var fresh = new Walk("Pictures/Travel/");
        var stale = new Walk(OnDisk)
        {
            // The app went to the background and back, and another list walked again.
            Meanwhile = () =>
            {
                cache.Invalidate();
                cache.Folders(fresh.Run);
            },
        };

        cache.Folders(stale.Run);

        Assert.Equal(["Pictures/Travel/"], cache.Folders(new Walk(OnDisk).Run));
        Assert.Equal(1, fresh.Runs);
    }

    [Fact]
    public void Lists_and_invalidations_on_many_threads_always_give_a_whole_walk()
    {
        var cache = new FolderWalkCache();

        Parallel.For(0, 3_000, i =>
        {
            if (i % 10 == 0)
                cache.Invalidate();
            else
                Assert.Equal(OnDisk, cache.Folders(new Walk(OnDisk).Run));
        });
    }
}
