namespace SortAndDelete.Models;

public enum SwipeDecision
{
    /// <summary>Photo stays in the gallery.</summary>
    Keep = 1,

    /// <summary>Photo is in the in-app bin, waiting to be committed to the system trash.</summary>
    Trash = 2,

    /// <summary>Photo was moved/added to an album.</summary>
    Moved = 3,
}
