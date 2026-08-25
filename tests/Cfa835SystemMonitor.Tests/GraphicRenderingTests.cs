namespace Cfa835SystemMonitor.Tests;

public sealed class GraphicRenderingTests
{
    [Fact]
    public void QuantizeNeverProducesTheRleEscapeByte()
    {
        // Command 40 subcommand 2 uses 0x03 as its RLE marker, so no pixel may ever equal it.
        for (int value = 0; value <= 255; value++)
        {
            byte quantized = GrayscaleImage.Quantize(value);
            Assert.NotEqual(0x03, quantized);
            Assert.Equal(0, quantized % 8);
        }
    }

    [Fact]
    public void ComposeSkipsFieldsWhoseTextDidNotChange()
    {
        LayoutPage page = Page(
            Field(LayoutFieldSource.CpuUtilization, "0.0", 0, 0, 60, 16),
            Field(LayoutFieldSource.NetworkRx, "0.0", 0, 20, 60, 16));
        FrameComposer composer = Composer();

        Assert.Equal(2, composer.Compose(page, Context(cpu: 10, rx: 1), force: false).Count);
        Assert.Empty(composer.Compose(page, Context(cpu: 10, rx: 1), force: false));

        FieldUpdate changed = Assert.Single(composer.Compose(page, Context(cpu: 42, rx: 1), force: false));
        Assert.Equal(0, changed.Y);
    }

    [Fact]
    public void ForceRedrawsEveryFieldRegardlessOfTheCache()
    {
        LayoutPage page = Page(
            Field(LayoutFieldSource.CpuUtilization, "0.0", 0, 0, 60, 16),
            Field(LayoutFieldSource.NetworkRx, "0.0", 0, 20, 60, 16));
        FrameComposer composer = Composer();

        _ = composer.Compose(page, Context(cpu: 10, rx: 1), force: false);
        Assert.Equal(2, composer.Compose(page, Context(cpu: 10, rx: 1), force: true).Count);
    }

    [Fact]
    public void ResetForgetsTheCache()
    {
        LayoutPage page = Page(Field(LayoutFieldSource.CpuUtilization, "0.0", 0, 0, 60, 16));
        FrameComposer composer = Composer();

        _ = composer.Compose(page, Context(cpu: 10), force: false);
        composer.Reset();
        Assert.Single(composer.Compose(page, Context(cpu: 10), force: false));
    }

    [Fact]
    public void UpdatePixelsAreExactlyTheFieldRectangle()
    {
        LayoutPage page = Page(Field(LayoutFieldSource.CpuUtilization, "0.0", 12, 7, 60, 16));

        FieldUpdate update = Assert.Single(Composer().Compose(page, Context(cpu: 10), force: true));

        Assert.Equal((12, 7, 60, 16), (update.X, update.Y, update.Width, update.Height));
        Assert.Equal(60 * 16, update.Pixels.Length);
    }

    [Fact]
    public void BackgroundShowsThroughWhereNoGlyphIsDrawn()
    {
        byte[] background = GrayscaleImage.Blank();
        Array.Fill(background, (byte)0x40);
        LayoutPage page = Page(Field(LayoutFieldSource.Literal, null, 0, 0, 80, 16, text: " "));

        FieldUpdate update = Assert.Single(
            Composer(("Main", background)).Compose(page, Context(), force: true));

        Assert.All(update.Pixels, pixel => Assert.Equal(0x40, pixel));
    }

    [Fact]
    public void GlyphsAreDrawnWithTheConfiguredShade()
    {
        LayoutPage page = Page(Field(LayoutFieldSource.Literal, null, 0, 0, 80, 16, text: "AA", shade: 128));

        FieldUpdate update = Assert.Single(Composer().Compose(page, Context(), force: true));

        Assert.Contains(GrayscaleImage.Quantize(128), update.Pixels);
        Assert.DoesNotContain(update.Pixels, pixel => pixel > GrayscaleImage.Quantize(128));
    }

    [Fact]
    public void AlignmentPlacesTheInkLeftCentreAndRight()
    {
        const int Width = 80;
        int[] firstInkColumn = new int[3];
        LayoutAlign[] alignments = [LayoutAlign.Left, LayoutAlign.Center, LayoutAlign.Right];

        for (int index = 0; index < alignments.Length; index++)
        {
            LayoutPage page = Page(
                Field(LayoutFieldSource.Literal, null, 0, 0, Width, 16, text: "AA", align: alignments[index]));
            FieldUpdate update = Assert.Single(Composer().Compose(page, Context(), force: true));
            firstInkColumn[index] = FirstInkColumn(update, Width);
        }

        Assert.Equal(0, firstInkColumn[0]);
        Assert.True(firstInkColumn[0] < firstInkColumn[1], "centre must start right of left");
        Assert.True(firstInkColumn[1] < firstInkColumn[2], "right must start right of centre");
        // BlockGlyphSource makes each glyph sizePx/2 wide, so "AA" at 16px occupies 16 of 80 columns.
        Assert.Equal(Width - 16, firstInkColumn[2]);
    }

    [Fact]
    public void NullMetricsFallBackToTheConfiguredPlaceholder()
    {
        LayoutPage page = Page(Field(LayoutFieldSource.CpuTemperature, "0'C'", 0, 0, 80, 16, fallback: "N/A"));
        FrameComposer composer = Composer();

        _ = composer.Compose(page, Context(temperature: null), force: true);
        // A different null snapshot still formats to "N/A", so nothing needs redrawing.
        Assert.Empty(composer.Compose(page, Context(temperature: null), force: false));
        Assert.Single(composer.Compose(page, Context(temperature: 61), force: false));
    }

    [Fact]
    public void ComposeFullFrameWritesEveryFieldIntoTheFrame()
    {
        LayoutPage page = Page(Field(LayoutFieldSource.Literal, null, 10, 20, 40, 16, text: "AA"));

        byte[] frame = Composer().ComposeFullFrame(page, Context());

        Assert.Equal(GrayscaleImage.Width * GrayscaleImage.Height, frame.Length);
        bool inkInsideField = false;
        for (int y = 20; y < 36; y++)
        {
            for (int x = 10; x < 50; x++)
            {
                inkInsideField |= frame[(y * GrayscaleImage.Width) + x] > 0;
            }
        }

        Assert.True(inkInsideField, "the field should have written ink into the frame");
        Assert.Equal(0, frame[0]);
    }

    [Fact]
    public void RequiredSizesCoversPageFieldsAndShutdownTemplates()
    {
        LayoutDocument layout = new()
        {
            Pages =
            [
                Page(Field(LayoutFieldSource.CpuUtilization, "0.0", 0, 0, 60, 16, sizePx: 21)),
                new LayoutPage { Id = "Shutdown", Kind = "shutdown", Fields = [] }
            ]
        };
        layout.Validate(AppContext.BaseDirectory);

        HashSet<int> sizes = FrameComposer.RequiredSizes(layout).ToHashSet();

        Assert.Contains(21, sizes);
        foreach (ShutdownUiState state in Enum.GetValues<ShutdownUiState>())
        {
            foreach (LayoutField field in ShutdownTemplates.For(state))
            {
                Assert.Contains(field.SizePx, sizes);
            }
        }
    }

    [Fact]
    public void GdiAtlasGivesSpaceARealAdvance()
    {
        // Regression: GenericTypographic ignores leading/trailing whitespace, so measuring " " alone
        // reported ~0 and every label lost its spaces ("NETWORK Mbps" drew as "NETWORKMbps").
        // An unknown family forces the generic sans-serif fallback, which exists on every Windows box.
        GdiGlyphSource atlas = GdiGlyphSource.Create(
            ["Definitely Not An Installed Font 9471"],
            [16],
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Glyph space = atlas.Get(16, ' ');

        Assert.True(space.Width >= 3, $"a 16px space should be at least 3px wide but was {space.Width}px");
        Assert.All(space.Coverage, pixel => Assert.Equal(0, pixel));
        Assert.True(atlas.InkHeight(16) > 0);
    }

    [Fact]
    public void GdiAtlasGivesEveryDigitTheSameAdvance()
    {
        GdiGlyphSource atlas = GdiGlyphSource.Create(
            ["Definitely Not An Installed Font 9471"],
            [20],
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        int[] widths = Enumerable.Range(0, 10).Select(digit => atlas.Get(20, (char)('0' + digit)).Width).ToArray();

        // Tabular digits keep a clock from jittering and keep each field's dirty rectangle stable.
        Assert.Single(widths.Distinct());
    }

    private static int FirstInkColumn(FieldUpdate update, int width)
    {
        for (int column = 0; column < width; column++)
        {
            for (int row = 0; row < update.Height; row++)
            {
                if (update.Pixels[(row * width) + column] > 0)
                {
                    return column;
                }
            }
        }

        return -1;
    }

    private static FrameComposer Composer(params (string Page, byte[] Pixels)[] backgrounds) =>
        new(new BlockGlyphSource(), backgrounds.ToDictionary(item => item.Page, item => item.Pixels));

    private static FieldContext Context(double cpu = 0, double rx = 0, double? temperature = null) => new(
        MetricSnapshot.Empty(new DateTimeOffset(2026, 8, 25, 14, 3, 7, TimeSpan.Zero)) with
        {
            CpuPercent = cpu,
            ReceiveMbps = rx,
            HottestCpuC = temperature
        },
        AutoCycle: false,
        ShutdownUiState.Idle,
        ConfirmYesSelected: false,
        PendingSeconds: 30,
        RemainingSeconds: 30);

    private static LayoutPage Page(params LayoutField[] fields)
    {
        LayoutPage page = new() { Id = "Main", Kind = "normal", Fields = fields };
        page.Validate(AppContext.BaseDirectory);
        return page;
    }

    private static LayoutField Field(
        LayoutFieldSource source,
        string? format,
        int x,
        int y,
        int width,
        int height,
        string? text = null,
        string fallback = "N/A",
        LayoutAlign align = LayoutAlign.Left,
        int sizePx = 16,
        int shade = 248) => new()
        {
            Source = source switch
            {
                LayoutFieldSource.Literal => "literal",
                LayoutFieldSource.CpuUtilization => "cpu.utilization",
                LayoutFieldSource.CpuTemperature => "cpu.temperature",
                LayoutFieldSource.NetworkRx => "net.rx",
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            },
            Format = format,
            Text = text,
            Fallback = fallback,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Align = align.ToString().ToLowerInvariant(),
            SizePx = sizePx,
            Shade = shade
        };

    /// <summary>
    /// Deterministic stand-in for <see cref="GdiGlyphSource"/>: every non-space character is a solid
    /// block, which keeps composition assertions exact and keeps GDI+ out of the test run.
    /// </summary>
    private sealed class BlockGlyphSource : IGlyphSource
    {
        public int CellHeight(int sizePx) => sizePx;

        public int InkTop(int sizePx) => 1;

        public int InkHeight(int sizePx) => sizePx - 2;

        public Glyph Get(int sizePx, char character)
        {
            int width = Math.Max(1, sizePx / 2);
            byte[] coverage = new byte[width * sizePx];
            if (character != ' ')
            {
                for (int row = InkTop(sizePx); row < InkTop(sizePx) + InkHeight(sizePx); row++)
                {
                    for (int column = 0; column < width; column++)
                    {
                        coverage[(row * width) + column] = 255;
                    }
                }
            }

            return new Glyph(width, sizePx, coverage);
        }
    }
}
