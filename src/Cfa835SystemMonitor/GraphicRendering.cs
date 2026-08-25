using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

/// <summary>A rasterized character: <see cref="Coverage"/> is per-pixel alpha, not a shade.</summary>
public sealed record Glyph(int Width, int Height, byte[] Coverage);

/// <summary>
/// Supplies rasterized glyphs to <see cref="FrameComposer"/>. Kept behind an interface so the
/// composer can be tested byte-for-byte without GDI+ (see the tests' block-font fake).
/// </summary>
public interface IGlyphSource
{
    int CellHeight(int sizePx);
    int InkTop(int sizePx);
    int InkHeight(int sizePx);
    Glyph Get(int sizePx, char character);
}

public static class GrayscaleImage
{
    public const int Width = Cfa835Device.DisplayWidth;
    public const int Height = Cfa835Device.DisplayHeight;

    /// <summary>
    /// Snaps a byte to a multiple of 8, which guarantees the pixel stream never contains a literal
    /// 0x03 — the RLE escape byte in command 40 subcommand 2.
    /// </summary>
    /// <remarks>
    /// Hardware v2.0 renders 16 shades from the top 4 bits and ignores the rest (the older hardware
    /// v1.3 datasheet documented 32 shades from the top 5 bits), so this keeps one more bit than the
    /// panel resolves. That is harmless on the wire but does make an anti-aliased --layout-preview
    /// PNG marginally smoother than the physical display.
    /// </remarks>
    public static byte Quantize(int value) => (byte)(Math.Clamp(value, 0, 255) & 0xF8);

    public static byte[] Blank() => new byte[Width * Height];

    [SupportedOSPlatform("windows")]
    public static byte[] Load(string path, bool invert)
    {
        using Bitmap source = new(path);
        if (source.Width != Width || source.Height != Height)
        {
            throw new InvalidDataException(
                $"Background '{path}' is {source.Width}x{source.Height}; the CFA835 needs exactly {Width}x{Height}.");
        }

        using Bitmap normalized = new(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(normalized))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        byte[] frame = new byte[Width * Height];
        BitmapData data = normalized.LockBits(
            new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] raw = new byte[data.Stride * Height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int offset = (y * data.Stride) + (x * 4);
                    // Rec.601 luma; the source artwork is line art so exact weights barely matter,
                    // but this keeps coloured logos legible instead of flattening them.
                    int luma = ((raw[offset + 2] * 299) + (raw[offset + 1] * 587) + (raw[offset] * 114)) / 1000;
                    frame[(y * Width) + x] = Quantize(invert ? 255 - luma : luma);
                }
            }
        }
        finally
        {
            normalized.UnlockBits(data);
        }

        return frame;
    }

    [SupportedOSPlatform("windows")]
    public static void SavePng(byte[] frame, string path, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        using Bitmap bitmap = new(Width * scale, Height * scale, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] raw = new byte[data.Stride * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    byte shade = frame[((y / scale) * Width) + (x / scale)];
                    int offset = (y * data.Stride) + (x * 4);
                    raw[offset] = shade;
                    raw[offset + 1] = shade;
                    raw[offset + 2] = shade;
                    raw[offset + 3] = 255;
                }
            }

            Marshal.Copy(raw, 0, data.Scan0, raw.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}

/// <summary>
/// Rasterizes every printable ASCII character once per configured size at start-up, then hands out
/// immutable glyph bitmaps. Doing all GDI+ work up front keeps it out of the per-frame path, which
/// matters because the monitor normally runs as a Session 0 Windows service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiGlyphSource : IGlyphSource
{
    private const char FirstCharacter = ' ';
    private const char LastCharacter = '~';

    private sealed record SizeAtlas(int CellHeight, int InkTop, int InkHeight, Glyph[] Glyphs);

    private readonly Dictionary<int, SizeAtlas> _sizes;

    public string FontFamily { get; }

    private GdiGlyphSource(string fontFamily, Dictionary<int, SizeAtlas> sizes)
    {
        FontFamily = fontFamily;
        _sizes = sizes;
    }

    public static GdiGlyphSource Create(IReadOnlyList<string> fontFamilies, IEnumerable<int> sizes, ILogger logger)
    {
        string family = ResolveFamily(fontFamilies, logger);
        Dictionary<int, SizeAtlas> atlases = [];
        foreach (int size in sizes.Distinct().Order())
        {
            atlases[size] = BuildAtlas(family, size);
        }

        logger.LogInformation(
            "Glyph atlas ready: font '{Font}', sizes {Sizes}", family, string.Join(", ", atlases.Keys));
        return new GdiGlyphSource(family, atlases);
    }

    private static string ResolveFamily(IReadOnlyList<string> candidates, ILogger logger)
    {
        using InstalledFontCollection installed = new();
        HashSet<string> available = installed.Families
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in candidates)
        {
            if (available.Contains(candidate))
            {
                return candidate;
            }

            logger.LogWarning("Font '{Font}' is not installed; trying the next family in layout.fontFamilies", candidate);
        }

        string fallback = System.Drawing.FontFamily.GenericSansSerif.Name;
        logger.LogWarning("None of the configured fonts are installed; falling back to '{Font}'", fallback);
        return fallback;
    }

    private static SizeAtlas BuildAtlas(string family, int sizePx)
    {
        using Font font = new(family, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        using Bitmap probe = new(1, 1, PixelFormat.Format32bppArgb);
        using Graphics measure = Graphics.FromImage(probe);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        StringFormat format = StringFormat.GenericTypographic;
        int cellHeight = (int)Math.Ceiling(font.GetHeight(measure)) + 2;

        float[] advances = new float[LastCharacter - FirstCharacter + 1];
        for (char character = FirstCharacter; character <= LastCharacter; character++)
        {
            advances[character - FirstCharacter] =
                measure.MeasureString(character.ToString(), font, PointF.Empty, format).Width;
        }

        // GenericTypographic deliberately ignores leading and trailing whitespace, so measuring " "
        // on its own reports ~0 and every space in a label would collapse to one pixel. Measuring the
        // space between two glyphs instead recovers its real advance.
        float spaceAdvance = measure.MeasureString("n n", font, PointF.Empty, format).Width
            - measure.MeasureString("nn", font, PointF.Empty, format).Width;
        advances[' ' - FirstCharacter] = Math.Max(spaceAdvance, sizePx * 0.25f);

        // Digits share the widest digit advance so a clock never jitters and each field's dirty
        // rectangle stays the same size from frame to frame.
        float digitAdvance = 0;
        for (char digit = '0'; digit <= '9'; digit++)
        {
            digitAdvance = Math.Max(digitAdvance, advances[digit - FirstCharacter]);
        }

        Glyph[] glyphs = new Glyph[advances.Length];
        int inkTop = cellHeight;
        int inkBottom = -1;

        // One scratch surface for the whole size, sized to the widest glyph: creating a Bitmap and a
        // Graphics per character turns atlas construction into seconds instead of milliseconds.
        int maxCellWidth = Math.Max(1, (int)Math.Ceiling(advances.Max()));
        using Bitmap scratch = new(maxCellWidth + (Padding * 2), cellHeight + (Padding * 2), PixelFormat.Format32bppArgb);
        using Graphics canvas = Graphics.FromImage(scratch);
        canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        for (char character = FirstCharacter; character <= LastCharacter; character++)
        {
            int index = character - FirstCharacter;
            float natural = advances[index];
            float advance = character is >= '0' and <= '9' ? digitAdvance : natural;
            int cellWidth = Math.Max(1, (int)Math.Ceiling(advance));
            float offsetX = character is >= '0' and <= '9' ? (advance - natural) / 2f : 0f;

            glyphs[index] = Rasterize(scratch, canvas, font, format, character, cellWidth, cellHeight, offsetX);
            if (character == ' ')
            {
                continue;
            }

            Glyph glyph = glyphs[index];
            for (int row = 0; row < cellHeight; row++)
            {
                for (int column = 0; column < cellWidth; column++)
                {
                    if (glyph.Coverage[(row * cellWidth) + column] <= 8)
                    {
                        continue;
                    }

                    inkTop = Math.Min(inkTop, row);
                    inkBottom = Math.Max(inkBottom, row);
                    break;
                }
            }
        }

        if (inkBottom < inkTop)
        {
            inkTop = 0;
            inkBottom = cellHeight - 1;
        }

        return new SizeAtlas(cellHeight, inkTop, inkBottom - inkTop + 1, glyphs);
    }

    private const int Padding = 2;

    private static Glyph Rasterize(
        Bitmap scratch,
        Graphics canvas,
        Font font,
        StringFormat format,
        char character,
        int width,
        int height,
        float offsetX)
    {
        canvas.Clear(Color.Black);
        canvas.DrawString(character.ToString(), font, Brushes.White, Padding + offsetX, Padding, format);
        canvas.Flush();

        byte[] coverage = new byte[width * height];
        BitmapData data = scratch.LockBits(
            new Rectangle(0, 0, scratch.Width, scratch.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                int rowStart = (y + Padding) * data.Stride;
                for (int x = 0; x < width; x++)
                {
                    // White-on-black means the green channel already is the coverage value.
                    coverage[(y * width) + x] = Marshal.ReadByte(data.Scan0, rowStart + ((x + Padding) * 4) + 1);
                }
            }
        }
        finally
        {
            scratch.UnlockBits(data);
        }

        return new Glyph(width, height, coverage);
    }

    private SizeAtlas Atlas(int sizePx) => _sizes.TryGetValue(sizePx, out SizeAtlas? atlas)
        ? atlas
        : throw new InvalidOperationException($"No glyph atlas was built for size {sizePx}px.");

    public int CellHeight(int sizePx) => Atlas(sizePx).CellHeight;

    public int InkTop(int sizePx) => Atlas(sizePx).InkTop;

    public int InkHeight(int sizePx) => Atlas(sizePx).InkHeight;

    public Glyph Get(int sizePx, char character)
    {
        SizeAtlas atlas = Atlas(sizePx);
        char clamped = character is >= FirstCharacter and <= LastCharacter ? character : '?';
        return atlas.Glyphs[clamped - FirstCharacter];
    }
}

/// <summary>Everything a layout field can be bound to for one frame.</summary>
public sealed record FieldContext(
    MetricSnapshot Snapshot,
    bool AutoCycle,
    ShutdownUiState ShutdownState,
    bool ConfirmYesSelected,
    int PendingSeconds,
    int RemainingSeconds);

/// <summary>One rectangle of pixels ready for command 40 (0x28) subcommand 2.</summary>
public readonly record struct FieldUpdate(int X, int Y, int Width, int Height, byte[] Pixels);

/// <summary>
/// Composites page background + field text on the host. The module only ever receives finished
/// rectangles, which is what lets a field sit on top of arbitrary artwork without transparency tricks.
/// </summary>
public sealed class FrameComposer
{
    private readonly IGlyphSource _glyphs;
    private readonly Dictionary<string, byte[]> _backgrounds;
    private readonly Dictionary<string, string> _lastText = [];

    public FrameComposer(IGlyphSource glyphs, IReadOnlyDictionary<string, byte[]> backgrounds)
    {
        _glyphs = glyphs;
        _backgrounds = new Dictionary<string, byte[]>(backgrounds, StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<int> RequiredSizes(LayoutDocument layout)
    {
        foreach (LayoutPage page in layout.Pages)
        {
            foreach (LayoutField field in page.Fields)
            {
                yield return field.SizePx;
            }

            foreach (IReadOnlyList<LayoutField> fields in page.States.Values)
            {
                foreach (LayoutField field in fields)
                {
                    yield return field.SizePx;
                }
            }

            if (page.ResolvedKind != LayoutPageKind.Shutdown)
            {
                continue;
            }

            foreach (ShutdownUiState state in Enum.GetValues<ShutdownUiState>())
            {
                foreach (LayoutField field in ShutdownTemplates.For(state))
                {
                    yield return field.SizePx;
                }
            }
        }
    }

    public byte[] Background(LayoutPage page) =>
        _backgrounds.TryGetValue(page.Id, out byte[]? background) ? background : GrayscaleImage.Blank();

    /// <summary>Forgets the per-field text cache so the next compose redraws everything.</summary>
    public void Reset() => _lastText.Clear();

    /// <summary>
    /// Returns only the fields whose formatted text changed since the last call, unless
    /// <paramref name="force"/> is set (page switch, reconnect, or the periodic full repaint).
    /// </summary>
    public IReadOnlyList<FieldUpdate> Compose(LayoutPage page, FieldContext context, bool force)
    {
        IReadOnlyList<LayoutField> fields = page.FieldsFor(context.ShutdownState);
        byte[] background = Background(page);
        List<FieldUpdate> updates = [];

        for (int index = 0; index < fields.Count; index++)
        {
            LayoutField field = fields[index];
            string text = Format(field, context);
            string key = $"{page.Id}/{context.ShutdownState}/{index}";
            if (!force && _lastText.TryGetValue(key, out string? previous) && previous == text)
            {
                continue;
            }

            _lastText[key] = text;
            updates.Add(new FieldUpdate(field.X, field.Y, field.Width, field.Height, Render(field, text, background)));
        }

        return updates;
    }

    /// <summary>Builds the whole 244x68 frame; used by --layout-preview and the initial paint.</summary>
    public byte[] ComposeFullFrame(LayoutPage page, FieldContext context)
    {
        byte[] frame = (byte[])Background(page).Clone();
        foreach (LayoutField field in page.FieldsFor(context.ShutdownState))
        {
            byte[] rendered = Render(field, Format(field, context), frame);
            for (int row = 0; row < field.Height; row++)
            {
                Array.Copy(
                    rendered,
                    row * field.Width,
                    frame,
                    ((field.Y + row) * GrayscaleImage.Width) + field.X,
                    field.Width);
            }
        }

        return frame;
    }

    private byte[] Render(LayoutField field, string text, byte[] background)
    {
        byte[] target = new byte[field.Width * field.Height];
        for (int row = 0; row < field.Height; row++)
        {
            Array.Copy(
                background,
                ((field.Y + row) * GrayscaleImage.Width) + field.X,
                target,
                row * field.Width,
                field.Width);
        }

        if (text.Length == 0)
        {
            return target;
        }

        Glyph[] glyphs = new Glyph[text.Length];
        int totalWidth = 0;
        for (int index = 0; index < text.Length; index++)
        {
            glyphs[index] = _glyphs.Get(field.SizePx, text[index]);
            totalWidth += glyphs[index].Width;
        }

        int startX = field.ResolvedAlign switch
        {
            LayoutAlign.Center => (field.Width - totalWidth) / 2,
            LayoutAlign.Right => field.Width - totalWidth,
            _ => 0
        };
        int cellTop = ((field.Height - _glyphs.InkHeight(field.SizePx)) / 2) - _glyphs.InkTop(field.SizePx);
        byte shade = GrayscaleImage.Quantize(field.Shade);

        int penX = startX;
        foreach (Glyph glyph in glyphs)
        {
            for (int row = 0; row < glyph.Height; row++)
            {
                int destinationY = cellTop + row;
                if (destinationY < 0 || destinationY >= field.Height)
                {
                    continue;
                }

                for (int column = 0; column < glyph.Width; column++)
                {
                    int destinationX = penX + column;
                    if (destinationX < 0 || destinationX >= field.Width)
                    {
                        continue;
                    }

                    byte alpha = glyph.Coverage[(row * glyph.Width) + column];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    int offset = (destinationY * field.Width) + destinationX;
                    int blended = target[offset] + (((shade - target[offset]) * alpha) / 255);
                    target[offset] = GrayscaleImage.Quantize(blended);
                }
            }

            penX += glyph.Width;
        }

        return target;
    }

    private static string Format(LayoutField field, FieldContext context) => field.ResolvedSource switch
    {
        LayoutFieldSource.Literal => field.Text ?? string.Empty,
        LayoutFieldSource.DateTime => context.Snapshot.Timestamp.LocalDateTime
            .ToString(field.Format ?? "HH:mm:ss", CultureInfo.InvariantCulture),
        LayoutFieldSource.CpuUtilization => Number(context.Snapshot.CpuPercent, field),
        LayoutFieldSource.CpuTemperature => Number(context.Snapshot.HottestCpuC, field),
        LayoutFieldSource.SystemTemperature => Number(
            context.Snapshot.Temperatures.FirstOrDefault()?.Celsius, field),
        LayoutFieldSource.NetworkRx => Number(context.Snapshot.ReceiveMbps, field),
        LayoutFieldSource.NetworkTx => Number(context.Snapshot.TransmitMbps, field),
        LayoutFieldSource.NetworkTotal => Number(
            context.Snapshot.ReceiveMbps + context.Snapshot.TransmitMbps, field),
        LayoutFieldSource.AutoCycle => context.AutoCycle ? "ON" : "OFF",
        LayoutFieldSource.ShutdownPendingSeconds => Number(context.PendingSeconds, field),
        LayoutFieldSource.ShutdownRemainingSeconds => Number(context.RemainingSeconds, field),
        LayoutFieldSource.ShutdownConfirm => context.ConfirmYesSelected ? ">YES<    NO" : "YES    >NO<",
        _ => string.Empty
    };

    private static string Number(double? value, LayoutField field) =>
        value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString(field.Format ?? "0.0", CultureInfo.InvariantCulture)
            : field.Fallback;
}
