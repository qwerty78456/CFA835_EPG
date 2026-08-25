using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cfa835SystemMonitor;

public enum LayoutFieldSource
{
    Literal,
    DateTime,
    CpuUtilization,
    CpuTemperature,
    SystemTemperature,
    NetworkRx,
    NetworkTx,
    NetworkTotal,
    AutoCycle,
    ShutdownPendingSeconds,
    ShutdownRemainingSeconds,
    ShutdownConfirm
}

public enum LayoutAlign
{
    Left,
    Center,
    Right
}

public enum LayoutPageKind
{
    Normal,
    Shutdown
}

/// <summary>
/// One positioned text box. The rectangle is both the drawing area and the unit of transfer: the
/// composer crops the page background to it, draws the value on top, and pushes exactly these pixels.
/// </summary>
public sealed class LayoutField
{
    public string Source { get; init; } = "literal";
    public string? Format { get; init; }
    public string? Text { get; init; }
    public string Fallback { get; init; } = "N/A";
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Align { get; init; } = "left";
    public int SizePx { get; init; } = 16;
    public string? Font { get; init; }

    /// <summary>
    /// Greyscale level the glyphs are drawn with (0-255, quantized to the panel's 32 shades).
    /// Anti-aliased edges blend between the background pixel and this value, so dark text on a light
    /// background is just a low number here.
    /// </summary>
    public int Shade { get; init; } = 248;

    [JsonIgnore]
    public LayoutFieldSource ResolvedSource { get; private set; }

    [JsonIgnore]
    public LayoutAlign ResolvedAlign { get; private set; }

    private static readonly Dictionary<string, LayoutFieldSource> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["literal"] = LayoutFieldSource.Literal,
        ["datetime"] = LayoutFieldSource.DateTime,
        ["cpu.utilization"] = LayoutFieldSource.CpuUtilization,
        ["cpu.temperature"] = LayoutFieldSource.CpuTemperature,
        ["system.temperature"] = LayoutFieldSource.SystemTemperature,
        ["net.rx"] = LayoutFieldSource.NetworkRx,
        ["net.tx"] = LayoutFieldSource.NetworkTx,
        ["net.total"] = LayoutFieldSource.NetworkTotal,
        ["autocycle"] = LayoutFieldSource.AutoCycle,
        ["shutdown.pendingSeconds"] = LayoutFieldSource.ShutdownPendingSeconds,
        ["shutdown.remaining"] = LayoutFieldSource.ShutdownRemainingSeconds,
        ["shutdown.confirm"] = LayoutFieldSource.ShutdownConfirm
    };

    public static IEnumerable<string> KnownSources => Sources.Keys;

    public void Validate(string pageId, int index)
    {
        string where = $"layout page '{pageId}' field {index}";

        if (!Sources.TryGetValue(Source, out LayoutFieldSource source))
        {
            throw new InvalidDataException(
                $"{where}: unknown source '{Source}'. Valid values: {string.Join(", ", Sources.Keys.Order())}.");
        }

        ResolvedSource = source;
        ResolvedAlign = Align.ToLowerInvariant() switch
        {
            "left" => LayoutAlign.Left,
            "center" or "centre" => LayoutAlign.Center,
            "right" => LayoutAlign.Right,
            _ => throw new InvalidDataException($"{where}: align must be left, center or right.")
        };

        if (Width < 1 || Height < 1 ||
            X < 0 || Y < 0 ||
            X + Width > Cfa835Device.DisplayWidth ||
            Y + Height > Cfa835Device.DisplayHeight)
        {
            throw new InvalidDataException(
                $"{where}: rectangle ({X}, {Y}, {Width}, {Height}) does not fit the " +
                $"{Cfa835Device.DisplayWidth}x{Cfa835Device.DisplayHeight} display.");
        }

        if (SizePx is < 6 or > 72)
        {
            throw new InvalidDataException($"{where}: sizePx must be between 6 and 72.");
        }

        if (Shade is < 0 or > 255)
        {
            throw new InvalidDataException($"{where}: shade must be between 0 and 255.");
        }

        if (ResolvedSource == LayoutFieldSource.Literal && string.IsNullOrEmpty(Text))
        {
            throw new InvalidDataException($"{where}: a literal field requires a non-empty 'text'.");
        }

        ValidateFormat(where);
    }

    private void ValidateFormat(string where)
    {
        if (string.IsNullOrEmpty(Format))
        {
            return;
        }

        try
        {
            switch (ResolvedSource)
            {
                case LayoutFieldSource.DateTime:
                    _ = System.DateTime.Now.ToString(Format, CultureInfo.InvariantCulture);
                    break;
                case LayoutFieldSource.Literal:
                case LayoutFieldSource.AutoCycle:
                case LayoutFieldSource.ShutdownConfirm:
                    break;
                default:
                    _ = 0d.ToString(Format, CultureInfo.InvariantCulture);
                    break;
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{where}: format '{Format}' is not valid for source '{Source}'.", exception);
        }
    }
}

public sealed class LayoutPage
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = "normal";
    public string? Background { get; init; }
    public IReadOnlyList<LayoutField> Fields { get; init; } = [];

    /// <summary>
    /// Shutdown pages render three sub-states. Supplying "idle", "confirm" or "countdown" here
    /// overrides the built-in template for that sub-state; anything omitted keeps the default.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LayoutField>> States { get; init; } =
        new Dictionary<string, IReadOnlyList<LayoutField>>();

    [JsonIgnore]
    public LayoutPageKind ResolvedKind { get; private set; }

    [JsonIgnore]
    public string? BackgroundPath { get; private set; }

    public IReadOnlyList<LayoutField> FieldsFor(ShutdownUiState state)
    {
        if (ResolvedKind != LayoutPageKind.Shutdown)
        {
            return Fields;
        }

        string key = state switch
        {
            ShutdownUiState.Confirm => "confirm",
            ShutdownUiState.CountingDown => "countdown",
            _ => "idle"
        };

        return States.TryGetValue(key, out IReadOnlyList<LayoutField>? configured) && configured.Count > 0
            ? configured
            : ShutdownTemplates.For(state);
    }

    public void Validate(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidDataException("every layout page requires a non-empty 'id'.");
        }

        ResolvedKind = Kind.ToLowerInvariant() switch
        {
            "normal" => LayoutPageKind.Normal,
            "shutdown" => LayoutPageKind.Shutdown,
            _ => throw new InvalidDataException($"layout page '{Id}': kind must be 'normal' or 'shutdown'.")
        };

        if (!string.IsNullOrWhiteSpace(Background))
        {
            BackgroundPath = Path.IsPathRooted(Background)
                ? Background
                : Path.Combine(baseDirectory, Background);
            if (!File.Exists(BackgroundPath))
            {
                throw new InvalidDataException($"layout page '{Id}': background '{BackgroundPath}' was not found.");
            }
        }

        for (int index = 0; index < Fields.Count; index++)
        {
            Fields[index].Validate(Id, index);
        }

        foreach ((string state, IReadOnlyList<LayoutField> fields) in States)
        {
            if (state is not ("idle" or "confirm" or "countdown"))
            {
                throw new InvalidDataException(
                    $"layout page '{Id}': state '{state}' must be idle, confirm or countdown.");
            }

            for (int index = 0; index < fields.Count; index++)
            {
                fields[index].Validate($"{Id}/{state}", index);
            }
        }

        if (ResolvedKind == LayoutPageKind.Normal && Fields.Count == 0)
        {
            throw new InvalidDataException($"layout page '{Id}': a normal page needs at least one field.");
        }
    }
}

public sealed class LayoutDocument
{
    public int Version { get; init; } = 1;
    public int RefreshMs { get; init; } = 250;
    public int FullRepaintSeconds { get; init; } = 60;
    public bool GammaCorrection { get; init; } = true;
    public bool InvertBackground { get; init; }
    public IReadOnlyList<string> FontFamilies { get; init; } = ["Bahnschrift SemiLight", "Times New Roman"];
    public IReadOnlyList<LayoutPage> Pages { get; init; } = [];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static LayoutDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Layout file '{path}' was not found.");
        }

        LayoutDocument document = JsonSerializer.Deserialize<LayoutDocument>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidDataException($"Layout file '{path}' is empty.");
        document.Validate(Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory);
        return document;
    }

    public void Validate(string baseDirectory)
    {
        if (Version != 1)
        {
            throw new InvalidDataException(
                $"layout.version {Version} is not supported; this build understands version 1.");
        }

        if (RefreshMs is < 100 or > 60_000)
        {
            throw new InvalidDataException("layout.refreshMs must be between 100 and 60000.");
        }

        if (FullRepaintSeconds is < 0 or > 3600)
        {
            throw new InvalidDataException("layout.fullRepaintSeconds must be between 0 (disabled) and 3600.");
        }

        if (FontFamilies.Count == 0 || FontFamilies.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("layout.fontFamilies must list at least one non-empty family name.");
        }

        if (Pages.Count == 0)
        {
            throw new InvalidDataException("layout.pages must contain at least one page.");
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (LayoutPage page in Pages)
        {
            page.Validate(baseDirectory);
            if (!ids.Add(page.Id))
            {
                throw new InvalidDataException($"layout page id '{page.Id}' is duplicated.");
            }
        }

        if (Pages.All(page => page.ResolvedKind == LayoutPageKind.Shutdown))
        {
            throw new InvalidDataException("layout.pages must contain at least one page that is not a shutdown page.");
        }
    }

    public IReadOnlyList<PageDescriptor> Descriptors() =>
        Pages.Select(page => new PageDescriptor(page.Id, page.ResolvedKind == LayoutPageKind.Shutdown)).ToArray();
}

/// <summary>
/// Built-in shutdown sub-state layouts. These reproduce the wording of the text-mode shutdown pages
/// so enabling graphic mode does not change what the operator sees or how the keypad behaves.
/// </summary>
public static class ShutdownTemplates
{
    // FieldsFor runs on every composed frame, so the templates are built and validated once rather
    // than reallocated each tick.
    private static readonly Dictionary<ShutdownUiState, IReadOnlyList<LayoutField>> Cache =
        Enum.GetValues<ShutdownUiState>().ToDictionary(state => state, Build);

    public static IReadOnlyList<LayoutField> For(ShutdownUiState state) => Cache[state];

    private static IReadOnlyList<LayoutField> Build(ShutdownUiState state) => state switch
    {
        // Sizes are deliberately drawn from a small set (12/16/26) so the glyph atlas stays cheap to
        // build; every distinct sizePx in a layout costs one more full rasterization pass.
        ShutdownUiState.Confirm =>
        [
            Literal("SHUTDOWN DEVICE?", 0, 16),
            Bound("shutdown.pendingSeconds", "'Delay: '0's (UP/DN)'", 19, 12),
            Bound("shutdown.confirm", null, 34, 16),
            Literal("ENTER=OK   X=BACK", 53, 12)
        ],
        ShutdownUiState.CountingDown =>
        [
            Literal("SHUTTING DOWN", 2, 16),
            Bound("shutdown.remaining", "'IN '0's'", 22, 26),
            Literal("PRESS X TO CANCEL", 53, 12)
        ],
        _ =>
        [
            Literal("SHUTDOWN", 1, 16),
            Literal("Press ENTER to shut", 21, 12),
            Literal("down this device", 36, 12),
            Bound("shutdown.pendingSeconds", "'Timeout: '0's'", 53, 12)
        ]
    };

    private static LayoutField Literal(string text, int y, int sizePx) => Make("literal", null, text, y, sizePx);

    private static LayoutField Bound(string source, string? format, int y, int sizePx) =>
        Make(source, format, null, y, sizePx);

    private static LayoutField Make(string source, string? format, string? text, int y, int sizePx)
    {
        LayoutField field = new()
        {
            Source = source,
            Format = format,
            Text = text,
            X = 0,
            Y = y,
            Width = Cfa835Device.DisplayWidth,
            Height = Math.Min(sizePx + 2, Cfa835Device.DisplayHeight - y),
            Align = "center",
            SizePx = sizePx
        };
        field.Validate("shutdown", y);
        return field;
    }
}
