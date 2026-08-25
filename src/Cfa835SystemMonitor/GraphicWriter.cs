using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

/// <summary>
/// Everything graphic mode needs that survives a device reconnect: the parsed layout, the glyph
/// atlas and the decoded backgrounds. Built once at start-up because rasterizing fonts and decoding
/// PNGs is far too expensive to repeat inside the reconnect loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphicRuntime
{
    private readonly Dictionary<string, LayoutPage> _pages;

    public LayoutDocument Layout { get; }
    public GdiGlyphSource Glyphs { get; }
    public FrameComposer Composer { get; }

    private GraphicRuntime(LayoutDocument layout, GdiGlyphSource glyphs, FrameComposer composer)
    {
        Layout = layout;
        Glyphs = glyphs;
        Composer = composer;
        _pages = layout.Pages.ToDictionary(page => page.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a page id, falling back to the first page so an unknown id cannot blank the screen.</summary>
    public LayoutPage Page(string id) =>
        _pages.TryGetValue(id, out LayoutPage? page) ? page : Layout.Pages[0];

    public static GraphicRuntime Create(MonitorOptions options, ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger<GraphicRuntime>();
        string path = options.ResolveLayoutPath();
        LayoutDocument layout = LayoutDocument.Load(path);
        logger.LogInformation(
            "Loaded layout '{Path}': {Pages} page(s), refresh {RefreshMs} ms, full repaint every {Repaint}s",
            path,
            layout.Pages.Count,
            layout.RefreshMs,
            layout.FullRepaintSeconds);

        GdiGlyphSource glyphs = GdiGlyphSource.Create(
            layout.FontFamilies, FrameComposer.RequiredSizes(layout), logger);

        Dictionary<string, byte[]> backgrounds = [];
        foreach (LayoutPage page in layout.Pages)
        {
            if (page.BackgroundPath is null)
            {
                continue;
            }

            backgrounds[page.Id] = GrayscaleImage.Load(page.BackgroundPath, layout.InvertBackground);
            logger.LogInformation("Page '{Page}' background: {Path}", page.Id, page.BackgroundPath);
        }

        return new GraphicRuntime(layout, glyphs, new FrameComposer(glyphs, backgrounds));
    }
}

/// <summary>
/// Graphic-mode counterpart of <see cref="ScreenWriter"/>: it diffs at the layout-field level instead
/// of the text-row level and pushes only changed rectangles, then flushes the module's buffer once so
/// a frame never appears half-drawn.
/// </summary>
public sealed class GraphicScreenWriter(
    FrameComposer composer,
    LayoutDocument layout,
    ILogger<GraphicScreenWriter> logger)
{
    private string? _lastPageId;
    private ShutdownUiState? _lastShutdownState;
    private DateTimeOffset _nextFullRepaint = DateTimeOffset.MinValue;

    /// <summary>Prepares a freshly opened device: clear, buffer manually, paint the first frame.</summary>
    public async Task InitializeAsync(Cfa835Device device, CancellationToken cancellationToken)
    {
        await device.ClearDisplayAsync(cancellationToken).ConfigureAwait(false);
        await device.SetGraphicOptionsAsync(
            manualFlush: true,
            gammaCorrection: layout.GammaCorrection,
            cancellationToken).ConfigureAwait(false);
        Reset();
    }

    public void Reset()
    {
        composer.Reset();
        _lastPageId = null;
        _lastShutdownState = null;
        _nextFullRepaint = DateTimeOffset.MinValue;
    }

    public async Task RenderAsync(
        Cfa835Device device,
        LayoutPage page,
        FieldContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // A page or sub-state change means the artwork underneath every field changed too, so the
        // field-level cache is useless and the background has to go out again. The periodic repaint
        // exists because the pixel stream carries no CRC: a corrupted rectangle would otherwise stay
        // on the panel forever.
        bool repaint = _lastPageId != page.Id
            || _lastShutdownState != context.ShutdownState
            || (layout.FullRepaintSeconds > 0 && now >= _nextFullRepaint);

        if (repaint)
        {
            composer.Reset();
            await device.SendImageAsync(
                0,
                0,
                Cfa835Device.DisplayWidth,
                Cfa835Device.DisplayHeight,
                composer.Background(page),
                transparency: false,
                invert: false,
                cancellationToken).ConfigureAwait(false);
            _nextFullRepaint = layout.FullRepaintSeconds > 0
                ? now.AddSeconds(layout.FullRepaintSeconds)
                : DateTimeOffset.MaxValue;
        }

        IReadOnlyList<FieldUpdate> updates = composer.Compose(page, context, repaint);
        foreach (FieldUpdate update in updates)
        {
            await device.SendImageAsync(
                update.X,
                update.Y,
                update.Width,
                update.Height,
                update.Pixels,
                transparency: false,
                invert: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (repaint || updates.Count > 0)
        {
            await device.FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        }

        if (repaint)
        {
            logger.LogDebug("Full repaint of page '{Page}' ({Fields} fields)", page.Id, updates.Count);
        }

        _lastPageId = page.Id;
        _lastShutdownState = context.ShutdownState;
    }
}
