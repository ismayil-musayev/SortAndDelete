using SortAndDelete.Helpers;
using Xunit;

namespace SortAndDelete.Tests;

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(-5, "0 MB")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(38_297, "37.4 KB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    public void Human_formats_sizes(long bytes, string expected) =>
        Assert.Equal(expected, ByteFormat.Human(bytes));

    [Fact]
    public void Human_never_exceeds_terabytes()
    {
        var text = ByteFormat.Human(long.MaxValue);
        Assert.EndsWith("TB", text);
    }
}
