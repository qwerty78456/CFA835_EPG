using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

/// <summary>
/// Everything graphic mode needs that survives a device reconnect: the parsed layout, the glyph
/// atlas and the decoded backgrounds. Built once per configuration snapshot -- at start-up, and
/// again off the render loop when a live reload brings a new layout -- because rasterizing fonts
/// and decoding PNGs is far too expensive for the reconnect or render path. Immutable apart from
/// the composer's per-field diff cache, so a whole runtime can be swapped in by reference.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphicRuntime
{
    private readonly Dictionary<string, LayoutPage> _pages;

    public LayoutDocument Layout { get; }
    public IGlyphSource Glyphs { get; }

    /// <summary>The font family the glyph atlas was actually rasterized from.</summary>
    public string FontFamily { get; }
    public FrameComposer Composer { get; }

    private GraphicRuntime(LayoutDocument layout, IGlyphSource glyphs, string fontFamily, FrameComposer composer)
    {
        Layout = layout;
        Glyphs = glyphs;
        FontFamily = fontFamily;
        Composer = composer;
        _pages = layout.Pages.ToDictionary(page => page.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a page id, falling back to the first page so an unknown id cannot blank the screen.</summary>
    public LayoutPage Page(string id) =>
        _pages.TryGetValue(id, out LayoutPage? page) ? page : Layout.Pages[0];

    public static GraphicRuntime Create(MonitorOptions options, ILoggerFactory loggerFactory)
    {
        string path = options.ResolveLayoutPath();
        return Create(LayoutDocument.Load(path), path, loggerFactory);
    }

    /// <summary>Rasterizes the glyph atlas and decodes every page background for an already validated layout.</summary>
    public static GraphicRuntime Create(LayoutDocument layout, string layoutPath, ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger<GraphicRuntime>();
        logger.LogInformation(
            "Loaded layout '{Path}': {Pages} page(s), refresh {RefreshMs} ms, full repaint every {Repaint}s",
            layoutPath,
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

            try
            {
                backgrounds[page.Id] = GrayscaleImage.Load(page.BackgroundPath, layout.InvertBackground);
            }
            catch (Exception exception) when (exception is not InvalidDataException)
            {
                // GDI+ reports a truncated or non-image file as a bare ArgumentException ("Parameter
                // is not valid") that names neither the page nor the file.
                throw new InvalidDataException(
                    $"layout page '{page.Id}': background '{page.BackgroundPath}' could not be decoded as an image ({exception.Message}).",
                    exception);
            }

            logger.LogInformation("Page '{Page}' background: {Path}", page.Id, page.BackgroundPath);
        }

        return Create(layout, glyphs, glyphs.FontFamily, backgrounds);
    }

    /// <summary>Assembles a runtime from parts; lets tests use a GDI-free glyph source.</summary>
    public static GraphicRuntime Create(
        LayoutDocument layout,
        IGlyphSource glyphs,
        string fontFamily,
        IReadOnlyDictionary<string, byte[]> backgrounds) =>
        new(layout, glyphs, fontFamily, new FrameComposer(glyphs, backgrounds));

    /// <summary>
    /// Composes every page -- every shutdown sub-state included -- against both an empty and a
    /// populated metric snapshot, so every field's format and every glyph size is exercised once.
    /// Layout validation is best-effort about formats and glyph sizes; anything it misses would
    /// otherwise throw inside the render loop on every frame, dropping and re-opening the COM port
    /// forever. Uses <see cref="FrameComposer.ComposeFullFrame"/>, which does not touch the diff cache.
    /// </summary>
    public void VerifyRenders()
    {
        DateTimeOffset timestamp = new(2026, 1, 1, 12, 34, 56, TimeSpan.Zero);
        MetricSnapshot[] snapshots =
        [
            MetricSnapshot.Empty(timestamp),
            new MetricSnapshot(
                timestamp,
                99.9,
                [new TemperatureReading("Board", "System", 45.5, false)],
                88.8,
                123.45,
                67.89,
                true,
                true,
                true)
        ];

        foreach (LayoutPage page in Layout.Pages)
        {
            ShutdownUiState[] states = page.ResolvedKind == LayoutPageKind.Shutdown
                ? Enum.GetValues<ShutdownUiState>()
                : [ShutdownUiState.Idle];
            foreach (ShutdownUiState state in states)
            {
                foreach (MetricSnapshot snapshot in snapshots)
                {
                    foreach (bool yes in new[] { false, true })
                    {
                        FieldContext context = new(snapshot, yes, state, yes, 30, 25);
                        try
                        {
                            _ = Composer.ComposeFullFrame(page, context);
                        }
                        catch (Exception exception) when (exception is not InvalidDataException)
                        {
                            throw new InvalidDataException(
                                $"layout page '{page.Id}' ({state}) cannot be rendered: {exception.Message}", exception);
                        }
                    }
                }
            }
        }
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
