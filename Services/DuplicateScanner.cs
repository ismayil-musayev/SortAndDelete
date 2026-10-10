using SortAndDelete.Logic;
using SortAndDelete.Models;

namespace SortAndDelete.Services;

/// <summary>How far along the scan is, for the progress bar.</summary>
public sealed record ScanProgress(string Stage, int Done, int Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>
/// Works out what the library contains, once.
///
/// Two things here cost real time — decoding an image and reading a whole file — so both are
/// avoided wherever the answer is already known:
///
/// <list type="bullet">
/// <item>fingerprints are cached against a change token, so a second run does almost nothing;</item>
/// <item>files are only hashed when another file has exactly the same byte length, because
/// nothing else can possibly be byte-identical;</item>
/// <item>videos are only sampled when another clip runs for the same length, because nothing
/// else can be a re-encode of them.</item>
/// </list>
///
/// The result feeds <see cref="DuplicateGrouper"/>, which is what actually decides anything.
/// Changing level re-groups; it never rescans.
/// </summary>
public sealed class DuplicateScanner(IPhotoLibraryService library, SignatureStore store)
{
    /// <summary>Decoding several images at once helps; too many just thrashes a phone.</summary>
    private const int Concurrency = 4;

    /// <summary>Fingerprints are flushed this often so a cancelled scan keeps its progress.</summary>
    private const int SaveBatch = 200;

    public async Task<IReadOnlyList<ScannedPhoto>> ScanAsync(
        IReadOnlyList<PhotoAsset> photos,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var cached = await store.GetSignaturesAsync();
        var contentHashes = await store.GetContentHashesAsync();

        var stills = photos.Where(p => p.Kind == MediaKind.Image).ToList();
        var samplable = VideosWorthSampling(photos.Where(p => p.Kind == MediaKind.Video));
        var sizeCollisions = ItemsSharingAByteLength(photos);

        var signatures = new Dictionary<string, ImageSignature>(StringComparer.Ordinal);
        var frames = new Dictionary<string, IReadOnlyList<ImageSignature?>>(StringComparer.Ordinal);

        // Reuse whatever is still valid before deciding what is left to do.
        var stillsToRead = new List<PhotoAsset>();
        foreach (var photo in stills)
        {
            if (Cached(cached, photo, 0) is { } signature)
                signatures[photo.Id] = signature;
            else
                stillsToRead.Add(photo);
        }

        // A clip's frames are slots, one per sample position, null where nothing current is
        // cached. A clip missing a frame reads only that one, not the whole clip over again.
        var videosToRead = new List<(PhotoAsset Video, ImageSignature?[] Known)>();
        int framesToRead = 0;
        foreach (var video in photos.Where(p => p.Kind == MediaKind.Video && samplable.Contains(p.Id)))
        {
            var known = new ImageSignature?[VideoFingerprint.SamplePositions.Length];
            for (int frame = 0; frame < known.Length; frame++)
                known[frame] = Cached(cached, video, frame);

            int missing = known.Count(slot => slot is null);
            if (missing == 0)
            {
                frames[video.Id] = known;
            }
            else
            {
                videosToRead.Add((video, known));
                framesToRead += missing;
            }
        }

        // Hashes cached before files carried a stamp. One that could make an identical pair is
        // read again, as its file may have been rotated since; dropped here, it is hashed below
        // and doesn't count if that fails. The rest keep their value and take the file's stamp.
        var adoption = HashAdoption.For(sizeCollisions, contentHashes);
        foreach (var photo in adoption.Reread)
            contentHashes.Remove(photo.Id);

        try
        {
            await store.SaveContentHashesAsync(adoption.Adopted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // only an optimisation: the rows stay unstamped, and are adopted again next scan
        }

        var toHash = sizeCollisions
            .Where(p => !(contentHashes.TryGetValue(p.Id, out var row) && row.IsCurrentFor(p)))
            .ToList();

        int total = stillsToRead.Count + framesToRead + toHash.Count;
        int done = 0;

        if (total == 0)
        {
            progress?.Report(new ScanProgress("Up to date", 1, 1));
            return Assemble(photos, signatures, frames, contentHashes);
        }

        var pending = new List<SignatureRecord>();

        await RunAsync(stillsToRead, Concurrency, ct, async photo =>
        {
            var pixels = await library.GetThumbnailPixelsAsync(photo.Id, SignatureBuilder.GridSize, ct);
            if (pixels is { Length: SignatureBuilder.ExpectedLength })
            {
                var signature = SignatureBuilder.Build(pixels);
                lock (signatures)
                {
                    signatures[photo.Id] = signature;
                    pending.Add(SignatureRecord.From(photo.Id, 0, photo, signature));
                }
            }
            await AdvanceAsync("Fingerprinting photos");
        });

        await RunAsync(videosToRead, Concurrency, ct, async clip =>
        {
            var (video, known) = clip;

            // Only the empty slots are read, which is what framesToRead counted.
            var sampled = await VideoFingerprint.SampleAsync(known, async frame =>
            {
                var pixels = await library.GetVideoFramePixelsAsync(
                    video.Id, VideoFingerprint.SamplePositions[frame], SignatureBuilder.GridSize, ct);

                ImageSignature? signature = null;
                if (pixels is { Length: SignatureBuilder.ExpectedLength })
                {
                    signature = SignatureBuilder.Build(pixels);
                    lock (signatures)
                        pending.Add(SignatureRecord.From(video.Id, frame, video, signature));
                }

                await AdvanceAsync("Sampling videos");
                return signature;
            });

            if (sampled.Any(slot => slot is not null))
            {
                lock (signatures)
                    frames[video.Id] = sampled;
            }
        });

        var freshHashes = new List<ContentHashRecord>();
        await RunAsync(toHash, Concurrency, ct, async photo =>
        {
            var hash = await library.ComputeContentHashAsync(photo.Id, ct);
            if (hash is not null)
            {
                var record = ContentHashRecord.From(photo, hash);
                lock (freshHashes)
                {
                    freshHashes.Add(record);
                    contentHashes[photo.Id] = record;
                }
            }
            await AdvanceAsync("Checking for identical files");
        });

        // Stopping keeps what was read, so the next scan picks up from there. A save failing on
        // the way out mustn't turn the stop into an error.
        try
        {
            await FlushAsync(force: true);
            await store.SaveContentHashesAsync(freshHashes);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // stopping anyway; what didn't save is read again next time
        }

        ct.ThrowIfCancellationRequested();
        await store.ForgetMissingAsync(photos.Select(p => p.Id).ToHashSet(StringComparer.Ordinal));

        return Assemble(photos, signatures, frames, contentHashes);

        async Task AdvanceAsync(string stage)
        {
            int position = Interlocked.Increment(ref done);
            progress?.Report(new ScanProgress(stage, position, total));
            await FlushAsync(force: false);
        }

        async Task FlushAsync(bool force)
        {
            List<SignatureRecord>? batch = null;
            lock (signatures)
            {
                if (pending.Count > 0 && (force || pending.Count >= SaveBatch))
                {
                    batch = [.. pending];
                    pending.Clear();
                }
            }

            if (batch is not null)
                await store.SaveSignaturesAsync(batch);
        }
    }

    /// <summary>Wipes the cache; the next scan starts over.</summary>
    public Task ResetAsync() => store.ClearAsync();

    private static ImageSignature? Cached(
        Dictionary<string, List<SignatureRecord>> cached, PhotoAsset photo, int frameIndex)
    {
        if (!cached.TryGetValue(photo.Id, out var rows))
            return null;

        var row = rows.FirstOrDefault(r => r.FrameIndex == frameIndex);
        return row is not null && row.IsCurrentFor(photo) ? row.ToSignature() : null;
    }

    /// <summary>
    /// Clips with another of the same running time, which are the only ones that could be a
    /// re-encode of anything. Everything else is skipped without ever being decoded.
    /// </summary>
    private static HashSet<string> VideosWorthSampling(IEnumerable<PhotoAsset> videos)
    {
        var ordered = videos.Where(v => v.DurationMs > 0).OrderBy(v => v.DurationMs).ToList();
        var worthwhile = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < ordered.Count; i++)
        {
            bool hasTwin =
                (i > 0 && VideoFingerprint.DurationsAgree(ordered[i].DurationMs, ordered[i - 1].DurationMs))
                || (i + 1 < ordered.Count && VideoFingerprint.DurationsAgree(ordered[i].DurationMs, ordered[i + 1].DurationMs));

            if (hasTwin)
                worthwhile.Add(ordered[i].Id);
        }

        return worthwhile;
    }

    /// <summary>
    /// Items sharing an exact byte length with another. Byte-identical files must land here,
    /// so hashing this set alone finds every exact duplicate without reading the whole library.
    /// Edited items are left out: they can never be an identical file, and as iOS won't hash
    /// them they would come up again on every scan, which would then never be up to date.
    /// </summary>
    private static List<PhotoAsset> ItemsSharingAByteLength(IReadOnlyList<PhotoAsset> photos) =>
        [.. photos
            .Where(p => p.SizeBytes > 0 && !p.IsEdited)
            .GroupBy(p => p.SizeBytes)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)];

    private static List<ScannedPhoto> Assemble(
        IReadOnlyList<PhotoAsset> photos,
        Dictionary<string, ImageSignature> signatures,
        Dictionary<string, IReadOnlyList<ImageSignature?>> frames,
        Dictionary<string, ContentHashRecord> contentHashes) =>
        [.. photos.Select(photo => new ScannedPhoto
        {
            Photo = photo,
            Signature = signatures.GetValueOrDefault(photo.Id),
            Frames = frames.GetValueOrDefault(photo.Id),

            // A hash from a different file length describes a file that has since changed, and an
            // edited item's describes an original it no longer looks like. Its row is kept, so
            // reverting the edit costs no second read.
            ContentHash = !photo.IsEdited
                && contentHashes.TryGetValue(photo.Id, out var row)
                && row.IsCurrentFor(photo)
                ? row.Hash
                : null,
        })];

    /// <summary>
    /// Runs <paramref name="work"/> over the items, <paramref name="concurrency"/> workers each
    /// taking the next until none are left or the scan is stopped. Stopped, it returns once the
    /// items under way have finished instead of throwing: the scan still saves what was read,
    /// then throws itself. Workers, not a task per item waiting its turn, which a stop would
    /// wake one by one on the UI thread just to throw.
    /// </summary>
    private static async Task RunAsync<T>(
        IReadOnlyList<T> items, int concurrency, CancellationToken ct, Func<T, Task> work)
    {
        if (items.Count == 0)
            return;

        int next = -1;
        var workers = Enumerable.Range(0, Math.Min(concurrency, items.Count)).Select(async _ =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= items.Count)
                        return;

                    try
                    {
                        await work(items[index]);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // The scan was stopped. Any other cancellation is one more unreadable
                        // item, below.
                        throw;
                    }
                    catch
                    {
                        // One unreadable item must not take the scan down with it; it simply ends up
                        // without a fingerprint and never matches anything.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // stopped; see above
            }
        });

        await Task.WhenAll(workers);
    }
}
