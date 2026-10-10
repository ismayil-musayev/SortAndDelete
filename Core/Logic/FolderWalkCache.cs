namespace SortAndDelete.Logic;

/// <summary>
/// The folders a walk of the storage found, kept for the lists after it until
/// <see cref="Invalidate"/>. Safe on any thread: lists, the walks they run and invalidations can
/// all overlap. A walk that began before an invalidation still answers the list that ran it, but
/// isn't kept.
/// </summary>
public sealed class FolderWalkCache
{
    private readonly Lock _gate = new();
    private IReadOnlyList<string>? _walked;

    // Bumped by every invalidation: a walk is kept only if none came while it ran.
    private int _generation;

    /// <summary>The folders of the walk kept, or of <paramref name="walk"/> when none is, run on this thread.</summary>
    public IReadOnlyList<string> Folders(Func<IReadOnlyList<string>> walk)
    {
        int generation;
        lock (_gate)
        {
            if (_walked is { } walked)
                return walked;
            generation = _generation;
        }

        var found = walk();
        lock (_gate)
        {
            if (_generation == generation)
                _walked = found;
        }
        return found;
    }

    /// <summary>Drops the walk kept, so the next list walks again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _walked = null;
        }
    }
}
