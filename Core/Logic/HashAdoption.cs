using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// What becomes of the content hashes cached before rows carried the file's stamp
/// (<see cref="ContentHashRecord.NeedsStampFrom"/>), among the items that share a byte length
/// with another.
///
/// Such a hash describes the file as it was when it was taken, and an Android rotation since
/// would have kept the size, so it was never checked against the file as it is now. Where it
/// can't make an identical pair it is adopted: it keeps its value and takes the file's stamp, so
/// the next in-place edit shows. Where it could, all such hashes of that length are read again,
/// once: another current hash of the length is the same, or a file of the length is being read
/// anyway and might turn out to be.
/// </summary>
public sealed record HashAdoption(IReadOnlyList<ContentHashRecord> Adopted, IReadOnlyList<PhotoAsset> Reread)
{
    public static HashAdoption For(
        IReadOnlyList<PhotoAsset> collisions, IReadOnlyDictionary<string, ContentHashRecord> rows)
    {
        var adopted = new List<ContentHashRecord>();
        var reread = new List<PhotoAsset>();

        foreach (var length in collisions.GroupBy(p => p.SizeBytes))
        {
            var current = new List<(PhotoAsset Photo, ContentHashRecord Row)>();
            bool reading = false;
            foreach (var photo in length)
            {
                if (rows.TryGetValue(photo.Id, out var row) && row.IsCurrentFor(photo))
                    current.Add((photo, row));
                else
                    reading = true; // no hash yet, or a stale one: the scan reads this file
            }

            var unstamped = current.Where(item => item.Row.NeedsStampFrom(item.Photo)).ToList();
            if (unstamped.Count == 0)
                continue;

            // Two current hashes alike, one of them unstamped, claim an identical pair that only
            // a fresh read can confirm.
            reading |= current
                .GroupBy(item => item.Row.Hash, StringComparer.Ordinal)
                .Any(same => same.Count() > 1 && same.Any(item => item.Row.NeedsStampFrom(item.Photo)));

            if (reading)
                reread.AddRange(unstamped.Select(item => item.Photo));
            else
                adopted.AddRange(unstamped.Select(item => ContentHashRecord.From(item.Photo, item.Row.Hash)));
        }

        return new HashAdoption(adopted, reread);
    }
}
