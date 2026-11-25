using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

/// <summary>Which parts of the configuration a reload changed; used for the operator-facing log line.</summary>
[Flags]
public enum ReloadChanges
{
    None = 0,
    DisplayMode = 1,
    Layout = 2,
    Device = 4,
    Sampling = 8,
    Thermal = 16,
    Shutdown = 32,
    Display = 64,
    Reload = 128
}

/// <summary>What the open connection was last initialised to. Default (null mode) means unknown.</summary>
public readonly record struct DeviceDisplay(DisplayMode? Mode, bool GammaCorrection);

/// <summary>The device commands needed to bring the panel in line with the current configuration.</summary>
public enum DisplayTransition
{
    /// <summary>Text mode, nothing to do: the text writer's row cache rewrites whatever changed.</summary>
    None,

    /// <summary>Graphic mode already initialised with the same gamma: a fresh writer repaints the whole frame through the buffer.</summary>
    RepaintOnly,

    /// <summary>Clear, enable manual flush with the layout's gamma, then repaint.</summary>
    InitializeGraphic,

    /// <summary>Clear the composited artwork and return to automatic flush before text rows are written.</summary>
    RestoreText
}

/// <summary>
/// The configuration the render loop is currently driving, and the only place it changes. Swaps are
/// plain reference assignments made on the render loop thread between frames; the page controller
/// (shared with the serial reader thread and the handover hook) is reconfigured in place, never
/// replaced.
/// </summary>
public sealed class MonitorRuntimeState
{
    private readonly PageController _pages;

    public MonitorRuntimeState(ConfigSnapshot initial, PageController pages)
    {
        Snapshot = initial;
        _pages = pages;
        GraphicEverActive = initial.Graphic is not null;
    }

    public ConfigSnapshot Snapshot { get; private set; }
    public MonitorOptions Options => Snapshot.Options;
    public GraphicRuntime? Graphic => Snapshot.Graphic;

    /// <summary>Increments on every applied snapshot, so the loop knows the device must be reconciled.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Set once graphic mode has ever been active in this process. The panel may still hold artwork
    /// (and manual-flush mode) from it, so leaving or cleaning up must clear the graphic buffer.
    /// </summary>
    public bool GraphicEverActive { get; private set; }

    /// <summary>
    /// Switches to <paramref name="candidate"/>, or returns null -- changing nothing -- when the page
    /// controller refuses because the shutdown dialog is open or a handover is in progress.
    /// </summary>
    public ReloadChanges? TryApply(ConfigSnapshot candidate)
    {
        if (!_pages.TryReconfigure(
            candidate.Options.Display,
            candidate.Options.Shutdown,
            candidate.Graphic?.Layout.Descriptors()))
        {
            return null;
        }

        ReloadChanges changes = Compare(Snapshot, candidate);
        Snapshot = candidate;
        Generation++;
        if (candidate.Graphic is not null)
        {
            GraphicEverActive = true;
        }

        return changes;
    }

    public static ReloadChanges Compare(ConfigSnapshot before, ConfigSnapshot after)
    {
        ReloadChanges changes = ReloadChanges.None;
        MonitorOptions a = before.Options;
        MonitorOptions b = after.Options;
        if (a.Display.ResolvedMode != b.Display.ResolvedMode)
        {
            changes |= ReloadChanges.DisplayMode;
        }

        if (after.Graphic is not null)
        {
            // A graphic snapshot always carries a freshly loaded layout and artwork.
            changes |= ReloadChanges.Layout;
        }

        if (a.Device != b.Device)
        {
            changes |= ReloadChanges.Device;
        }

        if (a.Sampling != b.Sampling)
        {
            changes |= ReloadChanges.Sampling;
        }

        if (a.Thermal != b.Thermal)
        {
            changes |= ReloadChanges.Thermal;
        }

        if (a.Shutdown != b.Shutdown)
        {
            changes |= ReloadChanges.Shutdown;
        }

        if (a.Display != b.Display)
        {
            changes |= ReloadChanges.Display;
        }

        if (a.Reload != b.Reload)
        {
            changes |= ReloadChanges.Reload;
        }

        return changes;
    }

    /// <summary>
    /// Reconciles what the open connection was initialised to with what the configuration needs.
    /// Reconciling (rather than replaying a one-off command list) means a transition that failed
    /// halfway is simply repeated on the fresh connection after the reconnect.
    /// </summary>
    public static DisplayTransition PlanTransition(DeviceDisplay applied, GraphicRuntime? graphic, bool graphicEverActive)
    {
        if (graphic is not null)
        {
            return applied.Mode == DisplayMode.Graphic && applied.GammaCorrection == graphic.Layout.GammaCorrection
                ? DisplayTransition.RepaintOnly
                : DisplayTransition.InitializeGraphic;
        }

        if (applied.Mode == DisplayMode.Graphic)
        {
            return DisplayTransition.RestoreText;
        }

        // Unknown (a fresh connection): text mode has never cleared the panel on connect, and still
        // does not -- unless graphic mode ran earlier in this process and may have left artwork.
        return applied.Mode is null && graphicEverActive ? DisplayTransition.RestoreText : DisplayTransition.None;
    }

    /// <summary>The connection state once the transition planned for <paramref name="graphic"/> succeeded.</summary>
    public static DeviceDisplay After(GraphicRuntime? graphic) =>
        graphic is not null
            ? new DeviceDisplay(DisplayMode.Graphic, graphic.Layout.GammaCorrection)
            : new DeviceDisplay(DisplayMode.Text, false);
}

public static class DisplayTransitions
{
    /// <summary>
    /// Issues the device commands for <paramref name="transition"/> and returns the graphic writer to
    /// render with (null in text mode). Only wire sequences the monitor already used are emitted:
    /// <see cref="GraphicScreenWriter.InitializeAsync"/> for graphic, and the clear plus
    /// automatic-flush restore that --hardware-test uses when it leaves graphic mode.
    /// </summary>
    public static async Task<GraphicScreenWriter?> ExecuteAsync(
        DisplayTransition transition,
        Cfa835Device device,
        GraphicRuntime? graphic,
        ScreenWriter screen,
        ILogger<GraphicScreenWriter> logger,
        CancellationToken cancellationToken)
    {
        switch (transition)
        {
            case DisplayTransition.RepaintOnly when graphic is not null:
                // New writer + new composer: the first render repaints background and every field
                // into the buffer and flushes once, so the panel switches without a blank frame.
                return new GraphicScreenWriter(graphic.Composer, graphic.Layout, logger);
            case DisplayTransition.InitializeGraphic when graphic is not null:
                GraphicScreenWriter writer = new(graphic.Composer, graphic.Layout, logger);
                await writer.InitializeAsync(device, cancellationToken).ConfigureAwait(false);
                return writer;
            case DisplayTransition.RestoreText:
                // Command 31 text writes bypass the graphic buffer and must never be mixed with a
                // composited frame: clear both buffers and leave manual-flush mode first.
                await device.ClearDisplayAsync(cancellationToken).ConfigureAwait(false);
                await device.SetGraphicOptionsAsync(manualFlush: false, gammaCorrection: false, cancellationToken)
                    .ConfigureAwait(false);
                screen.Reset();
                return null;
            default:
                return null;
        }
    }
}
