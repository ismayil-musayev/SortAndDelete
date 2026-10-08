namespace SortAndDelete.Models;

/// <summary>An existing album/folder in the device gallery.</summary>
public sealed class AlbumInfo
{
    /// <summary>Relative path like "Pictures/Travel/" (Android) or collection localIdentifier (iOS).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public int Count { get; init; }

    /// <summary>Human-readable location shown under the name (relative path on Android; empty on iOS).</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Negative count = unknown (e.g. browsed folders we don't enumerate).</summary>
    public string CountText => Count switch
    {
        < 0 => "",
        1 => "1 item",
        _ => $"{Count:N0} items",
    };
}
