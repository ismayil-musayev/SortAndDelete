using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class PerceptualHashTests
{
    private const int Size = PerceptualHash.GridSize;

    /// <summary>Builds a grid from a function of the pixel position.</summary>
    private static byte[] Grid(Func<int, int, int> value)
    {
        var grid = new byte[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
                grid[(y * Size) + x] = (byte)Math.Clamp(value(x, y), 0, 255);
        }
        return grid;
    }

    private static byte[] HorizontalRamp() => Grid((x, _) => 40 + (x * 5));

    private static byte[] VerticalRamp() => Grid((_, y) => 40 + (y * 5));

    private static byte[] Noise(int seed)
    {
        var random = new Random(seed);
        return Grid((_, _) => random.Next(256));
    }

    [Fact]
    public void Same_grid_hashes_identically()
    {
        var grid = HorizontalRamp();

        Assert.Equal(PerceptualHash.Difference(grid), PerceptualHash.Difference(HorizontalRamp()));
        Assert.Equal(PerceptualHash.Dct(grid), PerceptualHash.Dct(HorizontalRamp()));
        Assert.Equal(0, PerceptualHash.Distance(PerceptualHash.Difference(grid), PerceptualHash.Difference(grid)));
    }

    /// <summary>
    /// The property the whole feature rests on: a copy that only differs in exposure — or in
    /// how hard it was compressed — has to land on the same bits.
    /// </summary>
    [Fact]
    public void Brightening_every_pixel_leaves_the_gradient_hash_untouched()
    {
        var original = Grid((x, y) => 60 + (x * 2) + (y * 3));
        var brighter = Grid((x, y) => 90 + (x * 2) + (y * 3));

        Assert.Equal(PerceptualHash.Difference(original), PerceptualHash.Difference(brighter));
    }

    [Fact]
    public void Different_structure_hashes_far_apart()
    {
        int gradients = PerceptualHash.Distance(
            PerceptualHash.Difference(HorizontalRamp()),
            PerceptualHash.Difference(VerticalRamp()));

        int noise = PerceptualHash.Distance(
            PerceptualHash.Difference(Noise(1)),
            PerceptualHash.Difference(Noise(2)));

        // Unrelated images average 32 differing bits; anything past the level thresholds
        // (16 at the very loosest) is comfortably "not the same picture".
        Assert.True(gradients > 16, $"horizontal vs vertical ramp was only {gradients} bits apart");
        Assert.True(noise > 16, $"two noise fields were only {noise} bits apart");
    }

    [Fact]
    public void Coarse_hash_also_separates_unrelated_images()
    {
        int distance = PerceptualHash.Distance(
            PerceptualHash.Dct(HorizontalRamp()),
            PerceptualHash.Dct(VerticalRamp()));

        Assert.True(distance > 8, $"DCT hashes were only {distance} bits apart");
    }

    [Fact]
    public void Four_quarter_turns_return_the_original()
    {
        var original = Grid((x, y) => (x * 7) + (y * 3));
        var buffer = new byte[original.Length];
        var scratch = new byte[original.Length];

        PerceptualHash.RotateQuarterTurn(original, buffer);
        PerceptualHash.RotateQuarterTurn(buffer, scratch);
        PerceptualHash.RotateQuarterTurn(scratch, buffer);
        PerceptualHash.RotateQuarterTurn(buffer, scratch);

        Assert.Equal(original, scratch);
    }

    [Fact]
    public void A_quarter_turn_actually_changes_the_picture()
    {
        var original = HorizontalRamp();
        var turned = new byte[original.Length];
        PerceptualHash.RotateQuarterTurn(original, turned);

        int distance = PerceptualHash.Distance(
            PerceptualHash.Difference(original),
            PerceptualHash.Difference(turned));

        Assert.True(distance > 16, $"a quarter turn only moved the hash {distance} bits");
    }

    [Fact]
    public void Wrong_sized_grids_are_rejected()
    {
        var tooSmall = new byte[10];

        Assert.Throws<ArgumentException>(() => PerceptualHash.Difference(tooSmall));
        Assert.Throws<ArgumentException>(() => PerceptualHash.Dct(tooSmall));
    }

    [Fact]
    public void Distance_counts_differing_bits()
    {
        Assert.Equal(0, PerceptualHash.Distance(0xDEADBEEF, 0xDEADBEEF));
        Assert.Equal(64, PerceptualHash.Distance(0, ulong.MaxValue));
        Assert.Equal(3, PerceptualHash.Distance(0b1000, 0b1111));
    }
}
