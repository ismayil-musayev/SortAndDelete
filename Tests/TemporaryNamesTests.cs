using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class TemporaryNamesTests
{
    [Fact]
    public void Generated_names_are_temporary()
    {
        for (int i = 0; i < 1000; i++)
        {
            var name = TemporaryNames.New();
            Assert.True(TemporaryNames.IsTemporary(name), name);
        }
    }

    [Fact]
    public void A_name_is_the_prefix_then_the_guid_in_N_format_then_tmp()
    {
        var id = Guid.Parse("01234567-89AB-CDEF-0123-456789ABCDEF");

        Assert.Equal(".sortanddelete-0123456789abcdef0123456789abcdef.tmp", TemporaryNames.For(id));
        Assert.True(TemporaryNames.IsTemporary(TemporaryNames.For(id)));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void Names_of_edge_guids_are_temporary(string id) =>
        Assert.True(TemporaryNames.IsTemporary(TemporaryNames.For(Guid.Parse(id))));

    [Theory]
    // wrong length
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcde.tmp")] // 31 digits
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef0.tmp")] // 33 digits
    [InlineData(".sortanddelete-01234567-89ab-cdef-0123-456789abcdef.tmp")] // "D" format
    // not 32 lowercase hex digits
    [InlineData(".sortanddelete-0123456789ABCDEF0123456789ABCDEF.tmp")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdeF.tmp")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdeg.tmp")]
    [InlineData(".sortanddelete-{0123456789abcdef0123456789abcd}.tmp")]
    [InlineData(".sortanddelete-\uFF10123456789abcdef0123456789abcdef.tmp")] // fullwidth zero
    // extra suffixes
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef.tmp.jpg")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef.tmp ")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef.tmp\n")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef (1).tmp")]
    // other prefixes
    [InlineData("x.sortanddelete-0123456789abcdef0123456789abcdef.tmp")]
    [InlineData("sortanddelete-0123456789abcdef0123456789abcdef.tmp")]
    [InlineData("_sortanddelete-0123456789abcdef0123456789abcdef.tmp")]
    [InlineData(".SortAndDelete-0123456789abcdef0123456789abcdef.tmp")]
    // other extensions
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef.TMP")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef.jpg")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdefxtmp")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef.temp")]
    [InlineData(".sortanddelete-0123456789abcdef0123456789abcdef")]
    // the bare prefix and look-alikes
    [InlineData(".sortanddelete-")]
    [InlineData(".sortanddelete-.tmp")]
    [InlineData(".sortanddelete-decoy.tmp")]
    [InlineData(".sortanddelete-notours.tmp")]
    [InlineData("")]
    public void Anything_else_is_not_temporary(string name) =>
        Assert.False(TemporaryNames.IsTemporary(name));

    [Fact]
    public void Null_is_not_temporary() =>
        Assert.False(TemporaryNames.IsTemporary(null));
}
