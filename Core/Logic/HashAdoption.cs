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
/// the next in-place edit shows. Where it could, it is read again:
///
/// <list type="bullet">
/// <item>one that another current hash of the length equals, as the two already claim an
/// identical pair. The other such hashes of the length are adopted all the same;</item>
/// <item>every one of a length that has a file being read anyway, which might turn out to be
/// what one of them describes, perhaps from before a rotation.</item>
/// </list>
///
/// A read that succeeds stamps the row, so it is made once. One that fails leaves the row
/// unstamped and the item out of that scan's identical files, and the next scan decides about it
/// afresh: it is read again for as long as its hash could still make a pair. An unreadable file
/// with no hash at all gets the same, being tried on every scan while it shares a length.
///
/// The same goes for the hashes not known to be of the file's own bytes
/// (<see cref="ContentHashRecord.Original"/>): cached before rows said so, when Android may have
/// handed over the file with its location blanked. They are read again in the same two cases.
/// Otherwise they are left as they are rather than adopted: such a hash proves nothing, and taken
/// at its word it could later equal the hash of a copy that really has no location.
/// </summary>
/// <remarks>
/// The first case is the narrower on purpose, though its files are read too. A new file's hash
/// could be anything; one read again to confirm a pair comes back as it was unless its file
/// changed, and to be taken for an adopted one it would have to have been edited into exactly
/// what that one was before an edit of its own.
/// </remarks>
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

            // In doubt: never checked against the file as it is now, or perhaps not of its own bytes.
            var inDoubt = current
                .Where(item => !item.Row.Original || item.Row.NeedsStampFrom(item.Photo))
                .ToList();
            if (inDoubt.Count == 0)
                continue;

            // Two current hashes alike claim an identical pair, which for an unstamped one of
            // them only a fresh read can confirm.
            var shared = current
                .GroupBy(item => item.Row.Hash, StringComparer.Ordinal)
                .Where(same => same.Count() > 1)
                .Select(same => same.Key)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (photo, row) in inDoubt)
            {
                if (reading || shared.Contains(row.Hash))
                    reread.Add(photo);
                else if (row.Original)
                    adopted.Add(ContentHashRecord.From(photo, row.Hash));
            }
        }

        return new HashAdoption(adopted, reread);
    }
}
