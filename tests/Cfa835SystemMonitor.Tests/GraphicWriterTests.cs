using Microsoft.Extensions.Logging.Abstractions;

namespace Cfa835SystemMonitor.Tests;

public sealed class GraphicWriterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T08:00:00Z");

    [Fact]
    public async Task InitializeClearsThenEnablesManualFlushWithTheLayoutGamma()
    {
        (FakeTransport transport, Cfa835Device device) = await OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime runtime = Runtime(Layout(gamma: true, Page("Main")));

        await Writer(runtime).InitializeAsync(device, CancellationToken.None);

        Assert.Equal(["cmd:0x06", "cmd:0x28/0"], transport.Log);
        Assert.Equal([0, 0x03], transport.Commands[^1].Data);
    }

    [Fact]
    public async Task FirstRenderPaintsTheBackgroundEveryFieldAndFlushesOnce()
    {
        (FakeTransport transport, Cfa835Device device) = await OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime runtime = Runtime(Layout(gamma: true, Page("Main", fields: 2)));

        await Writer(runtime).RenderAsync(device, runtime.Page("Main"), Context(), Now, CancellationToken.None);

        Assert.Equal(["stream:0x28", "stream:0x28", "stream:0x28", "cmd:0x28/1"], transport.Log);
    }

    [Fact]
    public async Task UnchangedFrameSendsNothing()
    {
        (FakeTransport transport, Cfa835Device device) = await OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime runtime = Runtime(Layout(gamma: true, Page("Main", fields: 2)));
        GraphicScreenWriter writer = Writer(runtime);
        await writer.RenderAsync(device, runtime.Page("Main"), Context(), Now, CancellationToken.None);
        transport.Log.Clear();

        await writer.RenderAsync(device, runtime.Page("Main"), Context(), Now.AddSeconds(1), CancellationToken.None);

        Assert.Empty(transport.Log);
    }

    [Fact]
    public async Task PageChangeForcesAFullRepaint()
    {
        (FakeTransport transport, Cfa835Device device) = await OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime runtime = Runtime(Layout(gamma: true, Page("Main", fields: 1), Page("Net", fields: 2)));
        GraphicScreenWriter writer = Writer(runtime);
        await writer.RenderAsync(device, runtime.Page("Main"), Context(), Now, CancellationToken.None);
        transport.Log.Clear();

        await writer.RenderAsync(device, runtime.Page("Net"), Context(), Now.AddSeconds(1), CancellationToken.None);

        Assert.Equal(["stream:0x28", "stream:0x28", "stream:0x28", "cmd:0x28/1"], transport.Log);
    }

    [Fact]
    public async Task FullRepaintRecursAfterTheConfiguredInterval()
    {
        (FakeTransport transport, Cfa835Device device) = await OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime runtime = Runtime(Layout(gamma: true, Page("Main", fields: 1)));
        GraphicScreenWriter writer = Writer(runtime);
        await writer.RenderAsync(device, runtime.Page("Main"), Context(), Now, CancellationToken.None);
        transport.Log.Clear();

        await writer.RenderAsync(device, runtime.Page("Main"), Context(), Now.AddSeconds(61), CancellationToken.None);

        Assert.Equal(["stream:0x28", "stream:0x28", "cmd:0x28/1"], transport.Log);
    }

    [Fact]
    public async Task AFreshWriterRepaintsWithoutClearingTheDisplay()
    {
        // This is what a same-mode layout reload relies on: a new runtime + writer repaints the whole
        // frame through the graphic buffer (one flush) instead of blanking the panel first.
        (FakeTransport transport, Cfa835Device device) = await OpenAsync();
        await using Cfa835Device _ = device;
        GraphicRuntime before = Runtime(Layout(gamma: true, Page("Main", fields: 1)));
        await Writer(before).RenderAsync(device, before.Page("Main"), Context(), Now, CancellationToken.None);
        transport.Log.Clear();

        GraphicRuntime after = Runtime(Layout(gamma: true, Page("Main", fields: 2)));
        await Writer(after).RenderAsync(device, after.Page("Main"), Context(), Now.AddSeconds(1), CancellationToken.None);

        Assert.DoesNotContain("cmd:0x06", transport.Log);
        Assert.Equal(["stream:0x28", "stream:0x28", "stream:0x28", "cmd:0x28/1"], transport.Log);
    }

    [Fact]
    public void VerifyRendersAcceptsEveryPageAndShutdownState()
    {
        GraphicRuntime runtime = Runtime(Layout(gamma: true, Page("Main", fields: 2), ShutdownPage("Bye")));

        runtime.VerifyRenders();
    }

    [Fact]
    public void VerifyRendersRejectsALayoutWhoseGlyphSizeIsMissing()
    {
        // GdiGlyphSource throws for a size it has no atlas for; that used to surface only inside the
        // render loop, where it would drop and re-open the COM port on every frame.
        LayoutDocument layout = Layout(gamma: true, Page("Main", fields: 1));
        GraphicRuntime runtime = GraphicRuntime.Create(
            layout, new MissingSizeGlyphSource(missing: 12), "Test", new Dictionary<string, byte[]>());

        InvalidDataException error = Assert.Throws<InvalidDataException>(runtime.VerifyRenders);
        Assert.Contains("'Main'", error.Message, StringComparison.Ordinal);
        Assert.Contains("12px", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRendersCoversEveryShutdownSubState()
    {
        // The built-in countdown template uses 26px; a source missing only that size must be caught.
        LayoutDocument layout = Layout(gamma: true, Page("Main", fields: 1), ShutdownPage("Bye"));
        GraphicRuntime runtime = GraphicRuntime.Create(
            layout, new MissingSizeGlyphSource(missing: 26), "Test", new Dictionary<string, byte[]>());

        InvalidDataException error = Assert.Throws<InvalidDataException>(runtime.VerifyRenders);
        Assert.Contains("CountingDown", error.Message, StringComparison.Ordinal);
    }

    internal static async Task<(FakeTransport Transport, Cfa835Device Device)> OpenAsync()
    {
        FakeTransport transport = new();
        Cfa835Device device = new(transport, NullLogger<Cfa835Device>.Instance);
        await device.OpenAsync("COM9", enableKeyReports: false, CancellationToken.None);
        transport.Log.Clear();
        transport.Commands.Clear();
        transport.Streams.Clear();
        return (transport, device);
    }

    internal static GraphicRuntime Runtime(LayoutDocument layout) =>
        GraphicRuntime.Create(layout, new BlockGlyphSource(), "Block", new Dictionary<string, byte[]>());

    internal static GraphicScreenWriter Writer(GraphicRuntime runtime) =>
        new(runtime.Composer, runtime.Layout, NullLogger<GraphicScreenWriter>.Instance);

    internal static LayoutDocument Layout(bool gamma, params LayoutPage[] pages)
    {
        LayoutDocument layout = new() { Pages = pages, GammaCorrection = gamma, FullRepaintSeconds = 60 };
        layout.Validate(AppContext.BaseDirectory);
        return layout;
    }

    internal static LayoutPage Page(string id, int fields = 1) => new()
    {
        Id = id,
        Kind = "normal",
        Fields = Enumerable.Range(0, fields).Select(index => new LayoutField
        {
            Source = "cpu.utilization",
            Format = "0.0",
            X = 0,
            Y = index * 16,
            Width = 120,
            Height = 14,
            SizePx = 12
        }).ToArray()
    };

    internal static LayoutPage ShutdownPage(string id) => new() { Id = id, Kind = "shutdown" };

    internal static FieldContext Context(double cpu = 12.5) => new(
        MetricSnapshot.Empty(Now) with { CpuPercent = cpu },
        AutoCycle: false,
        ShutdownUiState.Idle,
        ConfirmYesSelected: false,
        PendingSeconds: 30,
        RemainingSeconds: 30);

    /// <summary>Behaves like <see cref="GdiGlyphSource"/> asked for a size its atlas was not built for.</summary>
    private sealed class MissingSizeGlyphSource(int missing) : IGlyphSource
    {
        private readonly BlockGlyphSource _inner = new();

        public int CellHeight(int sizePx) => _inner.CellHeight(Check(sizePx));

        public int InkTop(int sizePx) => _inner.InkTop(Check(sizePx));

        public int InkHeight(int sizePx) => _inner.InkHeight(Check(sizePx));

        public Glyph Get(int sizePx, char character) => _inner.Get(Check(sizePx), character);

        private int Check(int sizePx) => sizePx == missing
            ? throw new InvalidOperationException($"No glyph atlas was built for size {sizePx}px.")
            : sizePx;
    }
}
