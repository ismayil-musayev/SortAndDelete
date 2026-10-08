namespace SortAndDelete.Helpers;

public static class ByteFormat
{
    public static string Human(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
