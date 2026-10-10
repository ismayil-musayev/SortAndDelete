using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

/// <summary>
/// Pictures that share a layout and differ in what is written on them, drawn here and
/// fingerprinted the way the app fingerprints a photo: from a thumbnail 32 pixels across. At
/// that size the writing is gone, so "Same photo" finds them as alike as a re-sent copy is to its
/// original. What still tells the two cases apart is the size: a re-sent copy is a smaller one.
/// These hold that a look-alike of the same size is grouped but not ticked, and that a resized
/// copy still is.
/// </summary>
public class SameSizeLookalikeTests
{
    private static readonly DuplicateRules SamePhoto = DuplicateRules.For(DuplicateLevel.SamePhoto);

    // ---------- Drawing ----------

    /// <summary>An RGB picture to draw on, small enough to make by the dozen.</summary>
    private sealed class Canvas(int width, int height)
    {
        public int Width => width;
        public int Height => height;
        public byte[] Rgb { get; } = new byte[width * height * 3];

        public void Blend(int x, int y, (int R, int G, int B) colour, double alpha)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            int i = ((y * width) + x) * 3;
            Rgb[i] = (byte)Math.Round((Rgb[i] * (1 - alpha)) + (colour.R * alpha));
            Rgb[i + 1] = (byte)Math.Round((Rgb[i + 1] * (1 - alpha)) + (colour.G * alpha));
            Rgb[i + 2] = (byte)Math.Round((Rgb[i + 2] * (1 - alpha)) + (colour.B * alpha));
        }

        public void Fill(double left, double top, double right, double bottom, (int R, int G, int B) colour, double alpha = 1)
        {
            for (int y = (int)(top * height); y < (int)(bottom * height); y++)
            {
                for (int x = (int)(left * width); x < (int)(right * width); x++)
                    Blend(x, y, colour, alpha);
            }
        }

        /// <summary>Averaged down to another size, as a careful resize does.</summary>
        public Canvas Resized(int toWidth, int toHeight)
        {
            var target = new Canvas(toWidth, toHeight);
            for (int ty = 0; ty < toHeight; ty++)
            {
                int y0 = ty * height / toHeight, y1 = Math.Max(y0 + 1, (ty + 1) * height / toHeight);
                for (int tx = 0; tx < toWidth; tx++)
                {
                    int x0 = tx * width / toWidth, x1 = Math.Max(x0 + 1, (tx + 1) * width / toWidth);
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            for (int x = x0; x < x1; x++)
                                sum += Rgb[(((y * width) + x) * 3) + c];
                        }
                        target.Rgb[(((ty * toWidth) + tx) * 3) + c] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
                    }
                }
            }
            return target;
        }

        /// <summary>The fingerprint the scan would take: squared to the 32-pixel thumbnail first.</summary>
        public ImageSignature Signature() =>
            SignatureBuilder.Build(Resized(SignatureBuilder.GridSize, SignatureBuilder.GridSize).Rgb);
    }

    private static readonly (int R, int G, int B) White = (255, 255, 255);
    private static readonly (int R, int G, int B) Black = (0, 0, 0);

    /// <summary>
    /// Text as far as a thumbnail can tell: each character a block of dots of its own, in white
    /// with a dark shadow, centred on the picture like the test pictures' file names.
    /// </summary>
    private static void Label(Canvas canvas, string text)
    {
        double cell = 0.045, tall = 0.107;
        double left = 0.5 - (text.Length * cell / 2), top = 0.5 - (tall / 2);

        foreach (var (colour, shift) in new[] { (Colour: Black, Shift: 0.005), (Colour: White, Shift: 0.0) })
        {
            for (int i = 0; i < text.Length; i++)
            {
                ulong dots = (ulong)text[i] * 0x9E3779B97F4A7C15UL;
                dots ^= dots >> 29;
                for (int row = 0; row < 7; row++)
                {
                    for (int column = 0; column < 5; column++)
                    {
                        if (((dots >> ((row * 5) + column)) & 1) == 0)
                            continue;

                        double x = left + (i * cell) + (column * cell / 6) + shift;
                        double y = top + (row * tall / 7) + shift;
                        canvas.Fill(x, y, x + (cell / 6), y + (tall / 7), colour);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A picture like the ones the device pass labelled its files with: a blue gradient, a faint
    /// pattern over it (rings or triangles), and the name in big letters.
    /// </summary>
    private static Canvas Labelled(string name, bool rings = true)
    {
        var canvas = new Canvas(300, 225);
        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                double t = (((double)x / canvas.Width) + ((double)y / canvas.Height)) / 2;
                canvas.Blend(x, y, ((int)(16 + (63 * t)), (int)(48 + (131 * t)), (int)(107 + (139 * t))), 1);
            }
        }

        if (rings)
        {
            for (int y = 0; y < canvas.Height; y++)
            {
                for (int x = 0; x < canvas.Width; x++)
                {
                    double distance = Math.Sqrt(Math.Pow(x - 150, 2) + Math.Pow(y - 112.5, 2));
                    if ((distance - 20) % 27.5 is >= 0 and < 2.5 && distance >= 20)
                        canvas.Blend(x, y, White, 0.35);
                }
            }
        }
        else
        {
            for (int i = 0; i < 8; i++)
            {
                for (int y = 150; y < canvas.Height; y++)
                {
                    double half = 18.75 * (y - 150) / 75.0;
                    for (int x = (int)((i * 37.5) + 18.75 - half); x < (int)((i * 37.5) + 18.75 + half); x++)
                        canvas.Blend(x, y, White, 0.27);
                }
            }
        }

        Label(canvas, name);
        return canvas;
    }

    /// <summary>
    /// One screen of an app with other values on it: a heading, ten rows of a grey caption over a
    /// line of black text about as long as the last screen's (an amount, a date), and a button.
    /// </summary>
    private static Canvas FormScreen(int seed)
    {
        var random = new Random(seed);
        var canvas = new Canvas(270, 600);
        canvas.Fill(0, 0, 1, 1, (250, 250, 250));
        canvas.Fill(0.055, 0.05, 0.6, 0.075, Black, 0.8);
        for (int row = 0; row < 10; row++)
        {
            double top = 0.133 + (row * 0.079);
            canvas.Fill(0.055, top, 0.3, top + 0.012, (128, 128, 128), 0.6);
            canvas.Fill(0.055, top + 0.021, 0.36 + (random.NextDouble() * 0.05), top + 0.036, Black, 0.55);
            canvas.Fill(0.055, top + 0.0625, 0.945, top + 0.064, (211, 211, 211));
        }
        canvas.Fill(0.055, 0.925, 0.945, 0.975, (33, 150, 243));
        return canvas;
    }

    /// <summary>Something with a photograph's variety: soft blobs of colour over a gradient.</summary>
    private static Canvas Photograph(int seed)
    {
        var random = new Random(seed);
        var canvas = new Canvas(400, 300);
        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
                canvas.Blend(x, y, (40 + (x / 4), 90 + (y / 3), 160 - (x / 5)), 1);
        }

        for (int blob = 0; blob < 14; blob++)
        {
            int cx = random.Next(400), cy = random.Next(300), rx = random.Next(25, 110), ry = random.Next(20, 80);
            var colour = (random.Next(256), random.Next(256), random.Next(256));
            for (int y = cy - ry; y <= cy + ry; y++)
            {
                for (int x = cx - rx; x <= cx + rx; x++)
                {
                    double inside = 1 - (Math.Pow((double)(x - cx) / rx, 2) + Math.Pow((double)(y - cy) / ry, 2));
                    if (inside > 0)
                        canvas.Blend(x, y, colour, Math.Min(0.85, inside * 1.6));
                }
            }
        }
        return canvas;
    }

    /// <summary>The picture as a messenger re-sends it: smaller, and a little off in every pixel.</summary>
    private static Canvas Resent(Canvas original, int width, int height)
    {
        var copy = original.Resized(width, height);
        var random = new Random(7);
        for (int i = 0; i < copy.Rgb.Length; i++)
            copy.Rgb[i] = (byte)Math.Clamp(copy.Rgb[i] + random.Next(-3, 4), 0, 255);
        return copy;
    }

    // ---------- What the scan makes of them ----------

    private static ScannedPhoto Scanned(string id, Canvas picture, int width, int height, long size, string? contentHash = null) => new()
    {
        Photo = new PhotoAsset
        {
            Id = id,
            Width = width,
            Height = height,
            SizeBytes = size,
            TakenAt = new DateTime(2026, 7, 1, 12, 0, 0),
            FolderPath = "DCIM/Camera/",
            FileName = $"{id}.jpg",
        },
        Signature = picture.Signature(),
        ContentHash = contentHash,
    };

    /// <summary>The ids ticked for the bin as the groups first go on screen at "Same photo".</summary>
    private static string[] TickedAtSamePhoto(params ScannedPhoto[] library)
    {
        var groups = DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto);
        return [.. TickCarryOver.Apply(null, groups, SamePhoto.PreselectsKeeper)
            .SelectMany(group => group.Items)
            .Where(item => item.IsTicked)
            .Select(item => item.PhotoId)
            .Order()];
    }

    private static void AssertLooksTheSame(ImageSignature a, ImageSignature b)
    {
        Assert.True(a.Detail >= SamePhoto.MinDetail && b.Detail >= SamePhoto.MinDetail, $"detail {a.Detail:F1} / {b.Detail:F1}");
        Assert.True(a.DistanceTo(b) <= SamePhoto.MaxDistance, $"gradient hash {a.DistanceTo(b)} bits apart");
        Assert.True(a.CoarseDistanceTo(b) <= SamePhoto.MaxCoarseDistance, $"coarse hash {a.CoarseDistanceTo(b)} bits apart");
        Assert.True(a.HistogramOverlap(b) >= SamePhoto.MinHistogramOverlap, $"colours overlap {a.HistogramOverlap(b):F2}");
    }

    // ---------- Tests ----------

    /// <summary>
    /// The pair the device pass found ticked: one background, one pattern, another name on it.
    /// The fingerprints can't tell them apart, so they are one group. Nothing in it is ticked.
    /// </summary>
    [Fact]
    public void Two_labelled_pictures_of_one_size_are_grouped_but_not_ticked()
    {
        var first = Labelled("db35_k09.jpg");
        var second = Labelled("db35_s01.jpg");
        AssertLooksTheSame(first.Signature(), second.Signature());

        ScannedPhoto[] library =
        [
            Scanned("k09", first, 1200, 900, 118_800),
            Scanned("s01", second, 1200, 900, 117_900),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.False(group.IsExact);
        Assert.Equal(["k09", "s01"], group.SameSizeLookalikes.Order());
        Assert.Empty(TickedAtSamePhoto(library));
    }

    /// <summary>The same for one screen of an app with other values on it, as two receipts are.</summary>
    [Fact]
    public void Two_screens_of_one_layout_with_other_values_are_grouped_but_not_ticked()
    {
        var first = FormScreen(1);
        var second = FormScreen(2);
        AssertLooksTheSame(first.Signature(), second.Signature());

        ScannedPhoto[] library =
        [
            Scanned("receipt1", first, 1080, 2400, 240_000),
            Scanned("receipt2", second, 1080, 2400, 236_000),
        ];

        Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.Empty(TickedAtSamePhoto(library));
    }

    /// <summary>
    /// What the level is for is untouched: the copy a messenger made is a smaller picture, and
    /// arrives ticked beside its original.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_photograph_re_sent_smaller_is_still_ticked(int seed)
    {
        var original = Photograph(seed);
        var copy = Resent(original, 160, 120);
        AssertLooksTheSame(original.Signature(), copy.Signature());

        ScannedPhoto[] library =
        [
            Scanned("original", original, 4000, 3000, 5_000_000),
            Scanned("copy", copy, 1600, 1200, 400_000),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.Equal("original", group.Keeper.Id);
        Assert.Empty(group.SameSizeLookalikes);
        Assert.Equal(["copy"], TickedAtSamePhoto(library));
    }

    /// <summary>
    /// A copy re-sent twice gives two files of one size. When they are identical files of each
    /// other they are certainly one picture, and both go on being ticked.
    /// </summary>
    [Fact]
    public void Identical_files_of_one_size_beside_their_original_are_still_ticked()
    {
        var original = Photograph(1);
        var copy = Resent(original, 160, 120);

        ScannedPhoto[] library =
        [
            Scanned("original", original, 4000, 3000, 5_000_000),
            Scanned("copy1", copy, 1600, 1200, 400_000, contentHash: "ABC"),
            Scanned("copy2", copy, 1600, 1200, 400_000, contentHash: "ABC"),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.Empty(group.SameSizeLookalikes);
        Assert.Equal(["copy1", "copy2"], TickedAtSamePhoto(library));
    }

    /// <summary>
    /// Two files of one size that are not identical files may be two re-encodes of one picture,
    /// or two pictures. Nothing here says which, so neither is ticked; the resized one still is.
    /// </summary>
    [Fact]
    public void Of_a_group_only_the_copies_that_share_a_size_without_being_identical_are_left_unticked()
    {
        var original = Photograph(2);

        ScannedPhoto[] library =
        [
            Scanned("original", original, 4000, 3000, 5_000_000),
            Scanned("small", Resent(original, 160, 120), 1600, 1200, 400_000),
            Scanned("medium1", Resent(original, 200, 150), 2000, 1500, 700_000, contentHash: "ABC"),
            Scanned("medium2", Resent(original, 200, 150), 2000, 1500, 690_000, contentHash: "DEF"),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.Equal(["medium1", "medium2"], group.SameSizeLookalikes.Order());
        Assert.Equal(["small"], TickedAtSamePhoto(library));
    }

    /// <summary>A size MediaStore doesn't know can't be told from the keeper's, so it isn't ticked either.</summary>
    [Fact]
    public void A_look_alike_of_unknown_size_is_not_ticked()
    {
        var original = Photograph(3);

        ScannedPhoto[] library =
        [
            Scanned("original", original, 4000, 3000, 5_000_000),
            Scanned("copy", Resent(original, 160, 120), 0, 0, 400_000),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.Equal(["copy", "original"], group.SameSizeLookalikes.Order());
        Assert.Empty(TickedAtSamePhoto(library));
    }

    /// <summary>
    /// Nor is a copy of known size beside a keeper whose size isn't known: the keeper may be that
    /// very size, and a different picture.
    /// </summary>
    [Fact]
    public void A_look_alike_beside_a_member_of_unknown_size_is_not_ticked()
    {
        var original = Photograph(4);

        ScannedPhoto[] library =
        [
            Scanned("of-unknown-size", original, 0, 0, 5_000_000),
            Scanned("known", Resent(original, 160, 120), 1600, 1200, 400_000),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.Equal(["known", "of-unknown-size"], group.SameSizeLookalikes.Order());
        Assert.Empty(TickedAtSamePhoto(library));
    }

    /// <summary>Identical files are certain whatever their size says: every extra is ticked.</summary>
    [Fact]
    public void A_group_of_identical_files_is_ticked_as_before()
    {
        var picture = Labelled("db35_k09.jpg");

        ScannedPhoto[] library =
        [
            Scanned("1", picture, 1200, 900, 118_800, contentHash: "ABC"),
            Scanned("2", picture, 1200, 900, 118_800, contentHash: "ABC"),
            Scanned("3", picture, 1200, 900, 118_800, contentHash: "ABC"),
        ];

        var group = Assert.Single(DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto));
        Assert.True(group.IsExact);
        Assert.Empty(group.SameSizeLookalikes);
        Assert.Equal(2, TickedAtSamePhoto(library).Length);
    }

    /// <summary>What the user ticks in such a group is theirs, and comes back with the group.</summary>
    [Fact]
    public void A_tick_the_user_put_on_a_same_size_look_alike_is_carried_over()
    {
        ScannedPhoto[] library =
        [
            Scanned("k09", Labelled("db35_k09.jpg"), 1200, 900, 118_800),
            Scanned("s01", Labelled("db35_s01.jpg"), 1200, 900, 117_900),
        ];
        var groups = DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto);
        var shown = TickCarryOver.Apply(null, groups, preselect: true);
        string extra = shown[0].Items.Single(item => item.PhotoId != shown[0].KeeperId).PhotoId;

        List<GroupTicks> ticked = [shown[0] with
        {
            Items = [.. shown[0].Items.Select(item => item with { IsTicked = item.PhotoId == extra })],
        }];
        var again = TickCarryOver.Apply(ticked, DuplicateGrouper.Group(library, DuplicateLevel.SamePhoto), preselect: true);

        Assert.Equal([extra], again[0].Items.Where(item => item.IsTicked).Select(item => item.PhotoId));
    }
}
