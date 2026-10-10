using System.Collections.Frozen;
using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// Turns a scanned library into duplicate groups at a chosen <see cref="DuplicateLevel"/>.
///
/// Byte-identical files are merged at every level, so "exact copies" is a floor the other
/// levels build on rather than a separate mode. Everything past that runs through
/// <see cref="DuplicateRules"/>.
///
/// Grouping is transitive: if A matches B and B matches C, all three land together even
/// when A and C were never compared. That is what you want for re-encodes of one original,
/// and it is also why the thresholds stay tight — a loose one lets a chain wander.
/// </summary>
public static class DuplicateGrouper
{
    public static List<DuplicateGroup> Group(IReadOnlyList<ScannedPhoto> items, DuplicateLevel level) =>
        Group(items, DuplicateRules.For(level));

    /// <summary>
    /// Grouping against an explicit rule set rather than a named level. The calibration
    /// harness uses this to sweep a threshold through the real pipeline instead of
    /// reimplementing the checks and measuring something subtly different.
    /// </summary>
    public static List<DuplicateGroup> Group(IReadOnlyList<ScannedPhoto> items, DuplicateRules rules)
    {
        var parent = new int[items.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        MergeIdenticalFiles(items, parent);

        // Negative at the exact level, where byte equality is the whole rule.
        if (rules.MaxDistance >= 0)
            MergeLookalikes(items, parent, rules);

        return Collect(items, parent);
    }

    /// <summary>The exact tier: same bytes, same file, no judgement involved.</summary>
    private static void MergeIdenticalFiles(IReadOnlyList<ScannedPhoto> items, int[] parent)
    {
        var firstWithHash = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            if (TrustedHash(items[i]) is not { } hash)
                continue;

            if (firstWithHash.TryGetValue(hash, out int first))
                Union(parent, first, i);
            else
                firstWithHash[hash] = i;
        }
    }

    private static void MergeLookalikes(IReadOnlyList<ScannedPhoto> items, int[] parent, DuplicateRules rules)
    {
        MergeStills(items, parent, rules);

        // Only the re-encode level asks anything of video. "Adjusted", "same moment" and
        // "similar" are questions about photographs that do not mean anything for a clip.
        if (rules.IncludesVideo)
            MergeVideos(items, parent, rules);
    }

    private static void MergeStills(IReadOnlyList<ScannedPhoto> items, int[] parent, DuplicateRules rules)
    {
        // Pigeonhole: two hashes within N differing bits must agree exactly on at least one
        // of N+1 equal slices. Bucketing by each slice in turn therefore surfaces every real
        // match while comparing a tiny fraction of the pairs a full sweep would.
        int bands = rules.MaxDistance + 1;

        for (int band = 0; band < bands; band++)
        {
            var buckets = new Dictionary<ulong, List<int>>();

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Signature is not { } signature)
                    continue;

                // Below the detail floor an image carries no distinguishing structure, so it
                // would land in a bucket with every other flat image and match all of them.
                // Dropping those here is both the correctness guard and the reason buckets
                // stay small.
                if (signature.Detail < rules.MinDetail)
                    continue;

                Bucket(buckets, BandKey(signature.DHash, band, bands), i);

                if (rules.AllowRotation)
                {
                    Bucket(buckets, BandKey(signature.DHash90, band, bands), i);
                    Bucket(buckets, BandKey(signature.DHash180, band, bands), i);
                    Bucket(buckets, BandKey(signature.DHash270, band, bands), i);
                }
            }

            foreach (var bucket in buckets.Values)
            {
                for (int a = 0; a < bucket.Count; a++)
                {
                    for (int b = a + 1; b < bucket.Count; b++)
                    {
                        int left = bucket[a];
                        int right = bucket[b];

                        // Already together, transitively or via an earlier band. Skipping
                        // here is what keeps a pathological bucket — a thousand near-identical
                        // frames — from turning into a million confirmation checks.
                        if (left == right || Find(parent, left) == Find(parent, right))
                            continue;

                        if (Matches(items[left], items[right], rules))
                            Union(parent, left, right);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Clips are bucketed by running time rather than by hash. A re-encode keeps its length,
    /// so anything with no neighbour at the same length has no candidates at all — which is
    /// what makes fingerprinting videos affordable.
    /// </summary>
    private static void MergeVideos(IReadOnlyList<ScannedPhoto> items, int[] parent, DuplicateRules rules)
    {
        var buckets = new Dictionary<long, List<int>>();

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Frames is null || items[i].Photo.Kind != MediaKind.Video)
                continue;

            // Carried in its own bucket and the next one, so two clips either side of a
            // boundary still meet.
            long bucket = VideoFingerprint.DurationBucket(items[i].Photo.DurationMs);
            Bucket(buckets, bucket, i);
            Bucket(buckets, bucket + 1, i);
        }

        foreach (var bucket in buckets.Values)
        {
            for (int a = 0; a < bucket.Count; a++)
            {
                for (int b = a + 1; b < bucket.Count; b++)
                {
                    int left = bucket[a];
                    int right = bucket[b];

                    if (Find(parent, left) == Find(parent, right))
                        continue;

                    if (MatchesVideo(items[left], items[right], rules))
                        Union(parent, left, right);
                }
            }
        }
    }

    /// <summary>Every check the level demands of a still, cheapest first.</summary>
    private static bool Matches(ScannedPhoto a, ScannedPhoto b, DuplicateRules rules)
    {
        if (a.Signature is not { } left || b.Signature is not { } right)
            return false;

        if (a.Photo.Kind != b.Photo.Kind)
            return false;

        if (rules.MaxTimeApart is { } window && (a.Photo.TakenAt - b.Photo.TakenAt).Duration() > window)
            return false;

        if (!AspectMatches(a.Photo, b.Photo, rules.AllowRotation))
            return false;

        return SignaturesMatch(left, right, rules);
    }

    private static bool MatchesVideo(ScannedPhoto a, ScannedPhoto b, DuplicateRules rules)
    {
        if (a.Frames is not { } left || b.Frames is not { } right)
            return false;

        if (!VideoFingerprint.DurationsAgree(a.Photo.DurationMs, b.Photo.DurationMs))
            return false;

        if (rules.MaxTimeApart is { } window && (a.Photo.TakenAt - b.Photo.TakenAt).Duration() > window)
            return false;

        if (!AspectMatches(a.Photo, b.Photo, rules.AllowRotation))
            return false;

        return VideoFingerprint.FramesAgree(
            left, right, (x, y) => SignaturesMatch(x, y, rules), rules.MinDetail);
    }

    /// <summary>The hash and colour agreement one pair of fingerprints has to reach.</summary>
    private static bool SignaturesMatch(ImageSignature left, ImageSignature right, DuplicateRules rules)
    {
        if (left.Detail < rules.MinDetail || right.Detail < rules.MinDetail)
            return false;

        var (distance, coarse) = rules.AllowRotation
            ? left.BestAlignedDistances(right)
            : (left.DistanceTo(right), left.CoarseDistanceTo(right));

        if (distance > rules.MaxDistance)
            return false;

        if (rules.MaxCoarseDistance != int.MaxValue && coarse > rules.MaxCoarseDistance)
            return false;

        return rules.MinHistogramOverlap <= 0 || left.HistogramOverlap(right) >= rules.MinHistogramOverlap;
    }

    /// <summary>
    /// Rescaling preserves shape, so two items framed differently are different pictures
    /// however close their hashes land. Unknown dimensions abstain rather than reject.
    /// </summary>
    private static bool AspectMatches(PhotoAsset a, PhotoAsset b, bool allowRotation)
    {
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0)
            return true;

        double left = (double)a.Width / a.Height;
        double right = (double)b.Width / b.Height;

        // A quarter turn swaps the two sides, so the turned copy's ratio is the reciprocal.
        return Close(left, right) || (allowRotation && Close(left, 1 / right));

        static bool Close(double x, double y) =>
            Math.Abs(x - y) <= DuplicateRules.AspectTolerance * Math.Max(x, y);
    }

    private static List<DuplicateGroup> Collect(IReadOnlyList<ScannedPhoto> items, int[] parent)
    {
        var members = new Dictionary<int, List<ScannedPhoto>>();
        for (int i = 0; i < items.Count; i++)
        {
            int root = Find(parent, i);
            if (!members.TryGetValue(root, out var list))
                members[root] = list = [];
            list.Add(items[i]);
        }

        return [.. members.Values
            .Where(group => group.Count > 1)
            .Select(Build)
            .OrderByDescending(group => group.ReclaimableBytes)
            .ThenByDescending(group => group.RedundantCount)];
    }

    private static DuplicateGroup Build(List<ScannedPhoto> members)
    {
        var photos = members.Select(m => m.Photo).ToList();
        var keeper = KeeperRanking.Best(photos);

        bool isExact = TrustedHash(members[0]) is { } first
            && members.All(m => string.Equals(TrustedHash(m), first, StringComparison.Ordinal));

        return new DuplicateGroup
        {
            Items = [keeper, .. photos.Where(p => p.Id != keeper.Id)],
            Keeper = keeper,
            IsExact = isExact,
            SameSizeLookalikes = isExact ? FrozenSet<string>.Empty : SameSizeLookalikes(members),
        };
    }

    /// <summary>
    /// The members that share their pixel size with another member (see
    /// <see cref="DuplicateGroup.SameSizeLookalikes"/>), unless all of that size are identical
    /// files of one another, and those whose size isn't known. Measured: two 1200x900 pictures
    /// with one background and different labels sat 1 to 2 bits apart in the gradient hash and 2
    /// to 6 in the coarse one, with the same colours, and one form screen with other values 4 to
    /// 8 and 2 to 8. Re-encoded photographs sit as far apart (up to 9 and 6), so no threshold
    /// separates the two; the size does, for pictures from one source.
    /// </summary>
    private static IReadOnlySet<string> SameSizeLookalikes(List<ScannedPhoto> members)
    {
        // A member of unknown size may be the size of any other: nothing then says that any of
        // them is a resized copy, so none is ticked.
        if (members.Any(m => m.Photo.Width <= 0 || m.Photo.Height <= 0))
            return members.Select(m => m.Photo.Id).ToHashSet(StringComparer.Ordinal);

        var lookalikes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var size in members.GroupBy(m => (m.Photo.Width, m.Photo.Height)))
        {
            bool identical = TrustedHash(size.First()) is { } first
                && size.All(m => string.Equals(TrustedHash(m), first, StringComparison.Ordinal));

            if (size.Count() > 1 && !identical)
            {
                foreach (var member in size)
                    lookalikes.Add(member.Photo.Id);
            }
        }
        return lookalikes;
    }

    /// <summary>
    /// The item's byte hash, unless it is edited: then the hash is of an original it no longer
    /// looks like, and can't make it an identical file of anything.
    /// </summary>
    private static string? TrustedHash(ScannedPhoto item) =>
        item.Photo.IsEdited ? null : item.ContentHash;

    /// <summary>The <paramref name="band"/>-th equal slice of the hash, of <paramref name="bands"/> total.</summary>
    private static ulong BandKey(ulong hash, int band, int bands)
    {
        int start = band * 64 / bands;
        int width = ((band + 1) * 64 / bands) - start;
        ulong mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        return (hash >> start) & mask;
    }

    private static void Bucket<TKey>(Dictionary<TKey, List<int>> buckets, TKey key, int index)
        where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var list))
            buckets[key] = list = [];

        // Rotations of one item can hit the same bucket; carrying it once is enough.
        if (list.Count == 0 || list[^1] != index)
            list.Add(index);
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
            index = parent[index] = parent[parent[index]];
        return index;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB)
            parent[rootB] = rootA;
    }
}
