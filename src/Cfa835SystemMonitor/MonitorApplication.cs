using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

public sealed class MonitorApplication
{
    private readonly MonitorOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MonitorApplication> _logger;

    public MonitorApplication(MonitorOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MonitorApplication>();
    }

    public async Task<int> RunAsync(string? simulation, CancellationToken cancellationToken)
    {
        if (simulation is not null && !Environment.UserInteractive)
        {
            throw new InvalidOperationException("Simulation is disabled in non-interactive service sessions.");
        }

        IMetricSource baseMetrics = new WindowsMetricSource(_options, _loggerFactory);
        using IMetricSource metrics = simulation is null ? baseMetrics : new SimulationMetricSource(baseMetrics, simulation);
        if (simulation is not null)
        {
            _logger.LogWarning("Simulation mode active: {Simulation}", simulation);
        }

        CfaDeviceLocator locator = new(_options.Device);

        // Layout parsing, font rasterization and PNG decoding happen once, outside the reconnect
        // loop: none of it depends on the device and all of it is expensive.
        GraphicRuntime? graphic = _options.Display.ResolvedMode == DisplayMode.Graphic
            ? GraphicRuntime.Create(_options, _loggerFactory)
            : null;

        PageController pages = new(
            _options.Display,
            _options.Shutdown,
            new WindowsShutdownExecutor(_loggerFactory.CreateLogger<WindowsShutdownExecutor>()),
            graphic?.Layout.Descriptors());
        int retrySeconds = 1;

        while (!cancellationToken.IsCancellationRequested)
        {
            ICfaTransport transport = new SerialCfaTransport(_loggerFactory.CreateLogger<SerialCfaTransport>());
            await using Cfa835Device device = new(transport, _loggerFactory.CreateLogger<Cfa835Device>());
            ScreenWriter screen = new();
            GraphicScreenWriter? graphicScreen = graphic is null
                ? null
                : new GraphicScreenWriter(
                    graphic.Composer, graphic.Layout, _loggerFactory.CreateLogger<GraphicScreenWriter>());
            LedStateMachine leds = new(_options.Thermal);
            int forceRender = 1;

            try
            {
                string port = locator.ResolvePort();
                string version = await device.OpenAsync(port, enableKeyReports: true, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Connected to {Version} on {Port}", version, port);
                retrySeconds = 1;

                if (graphicScreen is not null)
                {
                    await graphicScreen.InitializeAsync(device, cancellationToken).ConfigureAwait(false);
                }

                device.KeyPressed += key =>
                {
                    if (pages.HandleKey(key, DateTimeOffset.Now))
                    {
                        Interlocked.Exchange(ref forceRender, 1);
                        _logger.LogInformation("Key {Key}: page {Page}, auto-cycle {Auto}", key, pages.Category, pages.AutoCycle);
                    }
                };

                DateTimeOffset nextDisplay = DateTimeOffset.MinValue;
                while (device.IsOpen && !cancellationToken.IsCancellationRequested)
                {
                    DateTimeOffset now = DateTimeOffset.Now;
                    MetricSnapshot snapshot = metrics.Sample(now);
                    if (pages.Tick(now))
                    {
                        Interlocked.Exchange(ref forceRender, 1);
                    }

                    if (now >= nextDisplay || Interlocked.Exchange(ref forceRender, 0) == 1)
                    {
                        if (graphic is not null && graphicScreen is not null)
                        {
                            FieldContext context = new(
                                snapshot,
                                pages.AutoCycle,
                                pages.ShutdownState,
                                pages.ConfirmYesSelected,
                                pages.PendingSeconds,
                                pages.RemainingSeconds(now));
                            await graphicScreen.RenderAsync(
                                device,
                                graphic.Page(pages.CurrentPageId),
                                context,
                                now,
                                cancellationToken).ConfigureAwait(false);
                            nextDisplay = now.AddMilliseconds(graphic.Layout.RefreshMs);
                        }
                        else
                        {
                            await screen.RenderAsync(device, pages.Render(snapshot, _options.Thermal), cancellationToken).ConfigureAwait(false);
                            nextDisplay = now.AddMilliseconds(_options.Sampling.DisplayMs);
                        }
                    }

                    IReadOnlyList<LedColor> states = leds.Evaluate(snapshot);
                    for (int led = 0; led < states.Count; led++)
                    {
                        await device.SetLedAsync(led, states[led].Green, states[led].Red, cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Delay(_options.Sampling.ActivityMs, cancellationToken).ConfigureAwait(false);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("CFA835 connection ended.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Monitor connection failed; retrying in {Seconds}s", retrySeconds);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(3));
                try
                {
                    if (device.IsOpen)
                    {
                        await device.BlankAndTurnOffAsync(cleanup.Token, graphic is not null).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not clear the CFA835 during shutdown");
                }

                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false);
            retrySeconds = Math.Min(30, retrySeconds * 2);
        }

        _logger.LogInformation("CFA835 monitor stopped");
        return 0;
    }

    /// <summary>
    /// Whether this process can open ring-0 helper devices such as PawnIO. The service runs as
    /// LocalSystem and is always elevated; interactive diagnostic runs frequently are not.
    /// </summary>
    private static bool IsElevated()
    {
        try
        {
            using System.Security.Principal.WindowsIdentity identity =
                System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Pushes the first layout page to the panel, then outlines every field rectangle so box
    /// alignment against the artwork can be judged on real hardware.
    /// </summary>
    private async Task GraphicHardwareStepAsync(Cfa835Device device, CancellationToken cancellationToken)
    {
        GraphicRuntime runtime = GraphicRuntime.Create(_options, _loggerFactory);
        LayoutPage page = runtime.Layout.Pages[0];
        FieldContext context = new(
            MetricSnapshot.Empty(DateTimeOffset.Now),
            AutoCycle: false,
            ShutdownUiState.Idle,
            ConfirmYesSelected: false,
            _options.Shutdown.CountdownSeconds,
            _options.Shutdown.CountdownSeconds);

        await device.ClearDisplayAsync(cancellationToken).ConfigureAwait(false);
        await device.SetGraphicOptionsAsync(true, runtime.Layout.GammaCorrection, cancellationToken).ConfigureAwait(false);

        byte[] frame = runtime.Composer.ComposeFullFrame(page, context);
        await device.SendImageAsync(
            0,
            0,
            Cfa835Device.DisplayWidth,
            Cfa835Device.DisplayHeight,
            frame,
            transparency: false,
            invert: false,
            cancellationToken).ConfigureAwait(false);
        await device.FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"Graphic page '{page.Id}' pushed: {frame.Length} bytes, font {runtime.Glyphs.FontFamily}");
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        foreach (LayoutField field in page.FieldsFor(ShutdownUiState.Idle))
        {
            await device.DrawRectangleAsync(
                field.X, field.Y, field.Width, field.Height, 248, 0, cancellationToken).ConfigureAwait(false);
        }

        await device.FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Field rectangles outlined; compare them against the background artwork.");
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders one layout page to a PNG without touching the CFA835. This is how the display layout
    /// gets tuned: edit layout.json, re-run, look at the image.
    /// </summary>
    public async Task<int> LayoutPreviewAsync(CommandLineOptions commandLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GraphicRuntime runtime = GraphicRuntime.Create(_options, _loggerFactory);
        LayoutPage page = commandLine.PreviewPage is null
            ? runtime.Layout.Pages[0]
            : runtime.Page(commandLine.PreviewPage);

        MetricSnapshot snapshot;
        try
        {
            IMetricSource live = new WindowsMetricSource(_options, _loggerFactory);
            // --simulate is accepted here so a layout can be checked against realistic values on a
            // workstation that has no PawnIO driver, where cpu.temperature would otherwise be "N/A"
            // and hide whether a real reading fits its box.
            using IMetricSource metrics = commandLine.Simulation is null
                ? live
                : new SimulationMetricSource(live, commandLine.Simulation);

            // CPU% and the PDH-based counters are deltas, so a single sample would preview as 0.
            _ = metrics.Sample(DateTimeOffset.Now);
            await Task.Delay(Math.Max(1000, _options.Sampling.TemperatureMs), cancellationToken).ConfigureAwait(false);
            snapshot = metrics.Sample(DateTimeOffset.Now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read live metrics; previewing with an empty snapshot");
            snapshot = MetricSnapshot.Empty(DateTimeOffset.Now);
        }

        FieldContext context = new(
            snapshot,
            AutoCycle: false,
            ShutdownUiState.Idle,
            ConfirmYesSelected: false,
            _options.Shutdown.CountdownSeconds,
            _options.Shutdown.CountdownSeconds);

        byte[] frame = runtime.Composer.ComposeFullFrame(page, context);
        string output = commandLine.PreviewPath
            ?? Path.Combine(Environment.CurrentDirectory, $"layout-preview-{page.Id}.png");
        GrayscaleImage.SavePng(frame, output, commandLine.PreviewScale);

        Console.WriteLine($"Font: {runtime.Glyphs.FontFamily}");
        Console.WriteLine($"Page: '{page.Id}' [{page.Kind}], background {page.BackgroundPath ?? "(none)"}");
        foreach (LayoutField field in page.FieldsFor(ShutdownUiState.Idle))
        {
            Console.WriteLine(
                $"  {field.Source,-22} x={field.X,3} y={field.Y,3} {field.Width,3}x{field.Height,-3} " +
                $"{field.SizePx}px {field.Align} shade {field.Shade}");
        }

        Console.WriteLine(
            $"Preview written to {output} ({GrayscaleImage.Width}x{GrayscaleImage.Height} at {commandLine.PreviewScale}x)");
        return 0;
    }

    public async Task<int> DiagnoseAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("CFA835 System Monitor diagnostics");
        Console.WriteLine($"OS: {Environment.OSVersion}");
        Console.WriteLine($"Process: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Elevated: {IsElevated()}");
        Console.WriteLine($"Configuration: {_options.Device.Vid}:{_options.Device.Pid}, serial {_options.Device.Serial}, fallback {_options.Device.FallbackPort}");

        int failures = 0;
        try
        {
            CfaDeviceLocator locator = new(_options.Device);
            string port = locator.ResolvePort();
            await using Cfa835Device device = new(
                new SerialCfaTransport(_loggerFactory.CreateLogger<SerialCfaTransport>()),
                _loggerFactory.CreateLogger<Cfa835Device>());
            string version = await device.OpenAsync(port, enableKeyReports: false, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Display: {version} on {port}");
            string[] rows = await device.ReadRowsAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine("Display rows:");
            for (int row = 0; row < rows.Length; row++)
            {
                Console.WriteLine($"  {row + 1}: |{rows[row]}|");
            }
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"Display: ERROR - {exception.Message}");
        }

        Console.WriteLine($"Display mode: {_options.Display.ResolvedMode.ToString().ToLowerInvariant()}");
        string layoutPath = _options.ResolveLayoutPath();
        if (_options.Display.ResolvedMode == DisplayMode.Graphic || File.Exists(layoutPath))
        {
            try
            {
                LayoutDocument layout = LayoutDocument.Load(layoutPath);
                Console.WriteLine(
                    $"Layout: {layoutPath} (refresh {layout.RefreshMs} ms, font chain {string.Join(" > ", layout.FontFamilies)})");
                foreach (LayoutPage page in layout.Pages)
                {
                    string background = page.BackgroundPath ?? "(none)";
                    Console.WriteLine($"  page '{page.Id}' [{page.Kind}] background {background}");
                    foreach (LayoutField field in page.Fields)
                    {
                        Console.WriteLine(
                            $"    {field.Source,-22} x={field.X,3} y={field.Y,3} {field.Width,3}x{field.Height,-3} " +
                            $"{field.SizePx}px {field.Align}");
                    }
                }
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"Layout: ERROR - {exception.Message}");
            }
        }

        try
        {
            using WindowsMetricSource metrics = new(_options, _loggerFactory);
            _ = metrics.Sample(DateTimeOffset.Now);
            await Task.Delay(Math.Max(1000, _options.Sampling.TemperatureMs), cancellationToken).ConfigureAwait(false);
            MetricSnapshot snapshot = metrics.Sample(DateTimeOffset.Now);
            Console.WriteLine($"PawnIO: {(metrics.IsPawnIoInstalled ? "installed" : "NOT INSTALLED")}");
            Console.WriteLine($"CPU utilization: {snapshot.CpuPercent:0.0}%");
            Console.WriteLine($"Physical interfaces: {metrics.GetInterfaces().Count}");
            foreach (InterfaceReading adapter in metrics.GetInterfaces())
            {
                Console.WriteLine($"  {adapter.Alias}: {adapter.Description}");
            }

            Console.WriteLine($"Network: Rx {snapshot.ReceiveMbps:0.00} Mbps, Tx {snapshot.TransmitMbps:0.00} Mbps");
            TemperatureReading? temperature = snapshot.Temperatures.FirstOrDefault();
            string temperatureValue = temperature?.Celsius is double celsius ? $"{celsius:0.0} C" : "N/A";
            string temperatureSource = temperature is null ? string.Empty : $" ({temperature.Hardware})";
            Console.WriteLine($"System temperature: {temperatureValue}{temperatureSource}");

            if (!metrics.IsPawnIoInstalled)
            {
                Console.WriteLine("WARNING: install the signed PawnIO driver before service acceptance testing.");
            }
            else if (!IsElevated())
            {
                // The registry key only proves the driver is installed. Opening
                // \\?\GLOBALROOT\Device\PawnIO additionally requires elevation, so an unelevated
                // diagnostic reports "installed" and still sees no CPU temperature. The service
                // itself runs as LocalSystem and is unaffected.
                Console.WriteLine(
                    "WARNING: PawnIO is installed but this process is not elevated, so CPU temperature is unavailable. Re-run from an elevated shell.");
            }
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"Metrics: ERROR - {exception.Message}");
        }

        Console.WriteLine(failures == 0 ? "Diagnostics completed." : $"Diagnostics completed with {failures} failure(s).");
        return failures == 0 ? 0 : 2;
    }

    public async Task<int> HardwareTestAsync(bool nonInteractive, CancellationToken cancellationToken)
    {
        CfaDeviceLocator locator = new(_options.Device);
        string port = locator.ResolvePort();
        await using Cfa835Device device = new(
            new SerialCfaTransport(_loggerFactory.CreateLogger<SerialCfaTransport>()),
            _loggerFactory.CreateLogger<Cfa835Device>());
        string version = await device.OpenAsync(port, enableKeyReports: false, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Starting hardware test on {Version}", version);

        (byte Press, byte Release) savedKeyMasks;
        try
        {
            savedKeyMasks = await device.ReadKeyMasksAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not snapshot keypad masks; factory press-only reporting will be restored");
            savedKeyMasks = (0x3F, 0x00);
        }

        await device.SetKeyMasksAsync(0x3F, 0x00, cancellationToken).ConfigureAwait(false);

        string[] savedRows;
        (byte Green, byte Red)[] savedLeds;
        try
        {
            savedRows = await device.ReadRowsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not snapshot display text; blank text will be restored");
            savedRows = Enumerable.Repeat(ScreenFormatter.Fit(string.Empty), 4).ToArray();
        }

        try
        {
            savedLeds = await device.ReadLedsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not snapshot LEDs; all-off will be restored");
            savedLeds = new (byte, byte)[4];
        }

        TaskCompletionSource<bool> exitPressed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HashSet<CfaKey> seen = [];
        device.KeyPressed += key =>
        {
            lock (seen)
            {
                seen.Add(key);
            }

            _logger.LogInformation("Key test: {Key}", key);
            if (key == CfaKey.Exit)
            {
                exitPressed.TrySetResult(true);
            }
        };

        try
        {
            ScreenWriter writer = new();
            await writer.RenderAsync(device,
            [
                "CFA835 HARDWARE TEST",
                "LED color chase...",
                "Then press all keys",
                "EXIT finishes"
            ], cancellationToken).ConfigureAwait(false);

            for (int led = 0; led < 4; led++)
            {
                foreach (LedColor color in new[] { LedColor.GreenOnly, LedColor.RedOnly, LedColor.Amber, LedColor.Off })
                {
                    await device.SetLedAsync(led, color.Green, color.Red, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_options.Display.ResolvedMode == DisplayMode.Graphic)
            {
                await GraphicHardwareStepAsync(device, cancellationToken).ConfigureAwait(false);
            }

            await writer.RenderAsync(device,
            [
                "KEYPAD TEST",
                "Press all six keys",
                nonInteractive ? "Timed auto-finish" : "Watching for input",
                "EXIT finishes"
            ], cancellationToken).ConfigureAwait(false);

            if (nonInteractive)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await exitPressed.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }

            lock (seen)
            {
                Console.WriteLine($"Keys observed: {(seen.Count == 0 ? "none" : string.Join(", ", seen.Order()))}");
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Keypad test timed out after 30 seconds");
        }
        finally
        {
            using CancellationTokenSource restore = new(TimeSpan.FromSeconds(5));
            try
            {
                if (_options.Display.ResolvedMode == DisplayMode.Graphic)
                {
                    // The graphic buffer cannot be read back, so the honest restore is a clear plus a
                    // return to automatic flushing before the saved text rows go out.
                    await device.ClearDisplayAsync(restore.Token).ConfigureAwait(false);
                    await device.SetGraphicOptionsAsync(false, false, restore.Token).ConfigureAwait(false);
                }

                for (int row = 0; row < 4; row++)
                {
                    await device.WriteRowAsync(row, savedRows[row], restore.Token).ConfigureAwait(false);
                }

                for (int led = 0; led < 4; led++)
                {
                    await device.SetLedAsync(led, savedLeds[led].Green, savedLeds[led].Red, restore.Token).ConfigureAwait(false);
                }

                await device.SetKeyMasksAsync(savedKeyMasks.Press, savedKeyMasks.Release, restore.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not fully restore CFA835 state after hardware test");
            }
        }

        return 0;
    }
}
