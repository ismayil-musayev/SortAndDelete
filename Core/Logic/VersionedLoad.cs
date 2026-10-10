namespace SortAndDelete.Logic;

/// <summary>
/// A value loaded on demand and kept until it is marked stale or invalidated, one load at a time.
/// A read waits for a value at least as new as the last mark or invalidation before it began, and
/// shares the load in progress with the other reads: a mark during a load sends only the reads
/// after it to the next load, so a read waits for two loads at most, however often marks come.
/// Meant for one thread, or one synchronization context such as the UI thread: nothing here locks,
/// and a load keeps its value after its await, on the context of the read that started it.
/// </summary>
public sealed class VersionedLoad<T>(Func<Task<T>> load) where T : class
{
    private T? _value;
    private int _loadedVersion = -1;

    // Bumped by every mark and invalidation; a read wants a load at least this new.
    private int _version;

    // Bumped by every invalidation: a load that began before one doesn't keep its value.
    private int _invalidations;

    private Task<(T Value, int Version)>? _loading;

    /// <summary>
    /// The value the latest load kept, also once it is marked stale; null before the first load,
    /// and after an invalidation until a load that began after it ends.
    /// </summary>
    public T? Value => _value;

    /// <summary>
    /// When the latest load began, on <see cref="ChangeDebounce.Now"/>; null before the first. The
    /// load reads what it loads then or later.
    /// </summary>
    public TimeSpan? LoadStartedAt { get; private set; }

    /// <summary>
    /// The value, at least as new as the last mark or invalidation before this read: the value kept
    /// when it is, otherwise the value of the load in progress if that load began after the mark,
    /// or else of the load after it. A failed load throws to every read waiting for it, and the
    /// next read starts another.
    /// </summary>
    public async Task<T> GetAsync()
    {
        int wanted = _version;
        if (_value is { } kept && _loadedVersion >= wanted)
            return kept;

        while (true)
        {
            var loading = _loading ??= LoadAsync();
            (T Value, int Version) loaded;
            try
            {
                loaded = await loading;
            }
            finally
            {
                if (_loading == loading)
                    _loading = null;
            }

            if (loaded.Version >= wanted)
                return loaded.Value;
        }
    }

    /// <summary>Makes later reads load again; until a load ends, <see cref="Value"/> stays as it was.</summary>
    public void MarkStale() => _version++;

    /// <summary>Drops the value and makes later reads load again.</summary>
    public void Invalidate()
    {
        _value = null;
        _version++;
        _invalidations++;
    }

    private async Task<(T Value, int Version)> LoadAsync()
    {
        int version = _version, invalidations = _invalidations;
        LoadStartedAt = ChangeDebounce.Now;
        var value = await load();
        if (invalidations == _invalidations)
        {
            _value = value;
            _loadedVersion = version;
        }
        return (value, version);
    }
}
