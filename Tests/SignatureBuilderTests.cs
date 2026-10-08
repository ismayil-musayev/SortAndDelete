using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class SignatureBuilderTests
{
    private const int Size = SignatureBuilder.GridSize;

    /// <summary>Packs an RGB thumbnail from a per-pixel colour function.</summary>
    private static byte[] Thumbnail(Func<int, int, (int R, int G, int B)> colour)
    {
        var rgb = new byte[SignatureBuilder.ExpectedLength];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                var (r, g, b) = colour(x, y);
                int i = ((y * Size) + x) * 3;
                rgb[i] = (byte)Math.Clamp(r, 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(g, 0, 255);
                rgb[i + 2] = (byte)Math.Clamp(b, 0, 255);
            }
        }
        return rgb;
    }

    private static byte[] Rotate(byte[] rgb)
    {
        var turned = new byte[rgb.Length];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int from = ((y * Size) + x) * 3;
                int to = ((x * Size) + (Size - 1 - y)) * 3;
                turned[to] = rgb[from];
                turned[to + 1] = rgb[from + 1];
                turned[to + 2] = rgb[from + 2];
            }
        }
        return turned;
    }

    [Fact]
    public void A_flat_image_reports_no_detail()
    {
        var signature = SignatureBuilder.Build(Thumbnail((_, _) => (128, 128, 128)));

        Assert.Equal(0, signature.Detail, precision: 6);
    }

    /// <summary>
    /// The detail floor is what stops blank documents and dark frames from matching each
    /// other, so a genuinely contrasty image has to score well clear of it.
    /// </summary>
    [Fact]
    public void A_contrasty_image_reports_plenty_of_detail()
    {
        var checkerboard = Thumbnail((x, y) => (x + y) % 2 == 0 ? (10, 10, 10) : (245, 245, 245));

        Assert.True(SignatureBuilder.Build(checkerboard).Detail > 100);
    }

    [Fact]
    public void Histogram_covers_the_whole_thumbnail()
    {
        var signature = SignatureBuilder.Build(Thumbnail((x, y) => (x * 8, y * 8, 128)));

        // Bins hold their share scaled to 0-255, so they add up to a full thumbnail
        // give or take the rounding on each bin.
        int total = signature.Histogram.Sum(b => b);
        Assert.InRange(total, 200, 255);
    }

    [Fact]
    public void One_flat_colour_lands_in_one_histogram_bin()
    {
        var signature = SignatureBuilder.Build(Thumbnail((_, _) => (20, 200, 20)));

        Assert.Equal(1, signature.Histogram.Count(b => b > 0));
    }

    [Fact]
    public void Different_colours_of_the_same_shape_barely_overlap()
    {
        var red = SignatureBuilder.Build(Thumbnail((x, _) => (200, x * 2, x * 2)));
        var blue = SignatureBuilder.Build(Thumbnail((x, _) => (x * 2, x * 2, 200)));

        Assert.True(red.HistogramOverlap(blue) < 0.2);
    }

    [Fact]
    public void Identical_thumbnails_agree_on_everything()
    {
        var a = SignatureBuilder.Build(Thumbnail((x, y) => (x * 7, y * 5, 100)));
        var b = SignatureBuilder.Build(Thumbnail((x, y) => (x * 7, y * 5, 100)));

        Assert.Equal(0, a.DistanceTo(b));
        Assert.Equal(0, a.CoarseDistanceTo(b));
        Assert.Equal(1.0, a.HistogramOverlap(b), precision: 6);
    }

    /// <summary>
    /// The stored quarter-turn hashes have to be the ones a turned copy would actually
    /// produce, or the rotation-tolerant level would never match anything.
    /// </summary>
    [Fact]
    public void Stored_quarter_turns_match_what_a_turned_copy_hashes_to()
    {
        var upright = Thumbnail((x, y) => (x * 7, y * 5, 100));

        var original = SignatureBuilder.Build(upright);
        var turned = SignatureBuilder.Build(Rotate(upright));

        Assert.Equal(original.DHash90, turned.DHash);
        Assert.Equal((0, 0), turned.BestAlignedDistances(original));
    }

    [Fact]
    public void A_turned_copy_is_only_found_when_rotation_is_allowed()
    {
        var upright = Thumbnail((x, y) => (x * 7, y * 5, 100));

        var original = SignatureBuilder.Build(upright);
        var turned = SignatureBuilder.Build(Rotate(upright));

        Assert.True(turned.DistanceTo(original) > 16);
        Assert.Equal((0, 0), turned.BestAlignedDistances(original));
    }

    [Fact]
    public void Wrong_sized_thumbnails_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SignatureBuilder.Build(new byte[100]));
    }
}
