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
        PageController pages = new(
            _options.Display,
            _options.Shutdown,
            new WindowsShutdownExecutor(_loggerFactory.CreateLogger<WindowsShutdownExecutor>()));
        int retrySeconds = 1;

        while (!cancellationToken.IsCancellationRequested)
        {
            ICfaTransport transport = new SerialCfaTransport(_loggerFactory.CreateLogger<SerialCfaTransport>());
            await using Cfa835Device device = new(transport, _loggerFactory.CreateLogger<Cfa835Device>());
            ScreenWriter screen = new();
            LedStateMachine leds = new(_options.Thermal);
            int forceRender = 1;

            try
            {
                string port = locator.ResolvePort();
                string version = await device.OpenAsync(port, enableKeyReports: true, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Connected to {Version} on {Port}", version, port);
                retrySeconds = 1;

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
                        await screen.RenderAsync(device, pages.Render(snapshot, _options.Thermal), cancellationToken).ConfigureAwait(false);
                        nextDisplay = now.AddMilliseconds(_options.Sampling.DisplayMs);
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
                        await device.BlankAndTurnOffAsync(cleanup.Token).ConfigureAwait(false);
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

    public async Task<int> DiagnoseAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("CFA835 System Monitor diagnostics");
        Console.WriteLine($"OS: {Environment.OSVersion}");
        Console.WriteLine($"Process: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
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
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"Display: ERROR - {exception.Message}");
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
