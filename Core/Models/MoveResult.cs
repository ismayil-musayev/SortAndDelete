namespace SortAndDelete.Models;

public enum MoveOutcome
{
    Moved,

    /// <summary>The user backed out of a system dialog or a question, so there is nothing to report.</summary>
    Cancelled,

    Failed,
}

/// <summary>What became of a move: the item's id afterwards, or why it did not happen.</summary>
public sealed record MoveResult
{
    public required MoveOutcome Outcome { get; init; }

    /// <summary>
    /// The item's id after the move: unchanged when moved in place, new when copied. Null when it
    /// was copied and Android can't say yet which item the copy became.
    /// </summary>
    public string? PhotoId { get; init; }

    /// <summary>Why the move failed, in words for the user.</summary>
    public string? Problem { get; init; }

    /// <summary>Something the user should know although the item was moved, in words for the user.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The copy is in the album, but the original is still where it was too (Android kept it
    /// despite the user's consent), so the original stays to be decided on.
    /// </summary>
    public bool OriginalKept { get; init; }

    public static MoveResult Moved(string? photoId) => new() { Outcome = MoveOutcome.Moved, PhotoId = photoId };

    /// <summary>Copied into the album, with the original left where it was too; <paramref name="note"/> says so.</summary>
    public static MoveResult CopiedWithOriginalKept(string? photoId, string note) =>
        new() { Outcome = MoveOutcome.Moved, PhotoId = photoId, Note = note, OriginalKept = true };

    public static MoveResult Cancelled { get; } = new() { Outcome = MoveOutcome.Cancelled };

    public static MoveResult Failed(string problem) => new() { Outcome = MoveOutcome.Failed, Problem = problem };
}

/// <summary>What a move may need from the screen that started it.</summary>
public sealed class MoveFeedback
{
    /// <summary>How far copying a large item has got, from 0 to 1. Small items report nothing.</summary>
    public IProgress<double>? Progress { get; init; }

    /// <summary>
    /// Asks the user whether to go on although something can't be kept (title, message, the text
    /// of the button that goes on). Asked before the original is touched: false leaves both as they
    /// were. Without it the move goes on.
    /// </summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; init; }
}
