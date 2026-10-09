namespace SortAndDelete.Logic;

/// <summary>
/// The hidden name a copy made through a folder grant is written under until it has been checked:
/// ".sortanddelete-", a Guid in "N" format (32 lowercase hex digits), ".tmp" — in pattern terms
/// <c>^\.sortanddelete-[0-9a-f]{32}\.tmp\z</c>. Copies cut short are cleaned up by that name, so
/// <see cref="IsTemporary"/> accepts exactly what <see cref="For"/> makes and nothing broader: a
/// file or folder of the user's that merely looks similar must never be deleted.
/// </summary>
public static class TemporaryNames
{
    private const string Prefix = ".sortanddelete-";
    private const string IdFormat = "N"; // 32 hex digits, lowercase, no hyphens or braces
    private const int IdLength = 32;
    private const string Extension = ".tmp";

    /// <summary>A new temporary name.</summary>
    public static string New() => For(Guid.NewGuid());

    /// <summary>The temporary name for <paramref name="id"/>.</summary>
    public static string For(Guid id) => Prefix + id.ToString(IdFormat) + Extension;

    /// <summary>True only for a name <see cref="For"/> makes.</summary>
    public static bool IsTemporary(string? name)
    {
        if (name is null || name.Length != Prefix.Length + IdLength + Extension.Length ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) ||
            !name.EndsWith(Extension, StringComparison.Ordinal))
            return false;

        foreach (char c in name.AsSpan(Prefix.Length, IdLength))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }
        return true;
    }
}
