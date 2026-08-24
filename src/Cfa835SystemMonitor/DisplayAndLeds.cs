using System.Globalization;

namespace Cfa835SystemMonitor;

public enum PageCategory
{
    DateTime,
    Cpu,
    Temperatures,
    Network,
    Shutdown
}

public enum ShutdownUiState
{
    Idle,
    Confirm,
    CountingDown
}

public static class ScreenFormatter
{
    public const int Width = 20;

    public static string Fit(string? value)
    {
        string ascii = new((value ?? string.Empty)
            .Select(character => character is >= ' ' and <= '~' ? character : '?')
            .ToArray());
        return ascii.Length > Width ? ascii[..Width] : ascii.PadRight(Width);
    }

    public static string TemperatureValue(double? value) =>
        value.HasValue ? $"{value.Value,5:0.0}C" : "   N/A";

    public static string Rate(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            return "N/A";
        }

        return value switch
        {
            >= 100_000 => ">99999",
            >= 1_000 => value.ToString("0.0", CultureInfo.InvariantCulture),
            _ => value.ToString("0.00", CultureInfo.InvariantCulture)
        };
    }
}

public sealed class PageController(
    DisplayOptions options,
    ShutdownOptions shutdownOptions,
    IShutdownExecutor shutdownExecutor)
{
    private enum PendingShutdownAction
    {
        None,
        Start,
        Abort
    }

    private readonly object _sync = new();
    private PageCategory _category = PageCategory.DateTime;
    private int _temperaturePage;
    private int _temperatureCount;
    private DateTimeOffset _nextAuto = DateTimeOffset.MinValue;
    private DateTimeOffset _lastKeyAt = DateTimeOffset.MinValue;
    private ShutdownUiState _shutdownState = ShutdownUiState.Idle;
    private bool _confirmYes;
    private int _pendingSeconds = shutdownOptions.CountdownSeconds;
    private DateTimeOffset _shutdownDeadline;

    public bool AutoCycle { get; private set; } = options.AutoCycleOnStart;
    public PageCategory Category => _category;
    public int TemperaturePage => _temperaturePage;
    public ShutdownUiState ShutdownState => _shutdownState;
    public bool ConfirmYesSelected => _confirmYes;
    public int PendingSeconds => _pendingSeconds;

    public bool HandleKey(CfaKey key, DateTimeOffset now)
    {
        PendingShutdownAction action = PendingShutdownAction.None;
        int seconds = 0;
        bool handled;
        lock (_sync)
        {
            if (now - _lastKeyAt < TimeSpan.FromMilliseconds(100))
            {
                return false;
            }

            _lastKeyAt = now;
            switch (_shutdownState)
            {
                case ShutdownUiState.Confirm:
                    handled = HandleConfirmKey(key, now, out action);
                    seconds = _pendingSeconds;
                    break;
                case ShutdownUiState.CountingDown:
                    handled = HandleCountdownKey(key, out action);
                    break;
                default:
                    handled = HandleIdleKey(key, now);
                    break;
            }
        }

        // Invoked after releasing the lock so the process launch never stalls other
        // key/render callers; the executor is contractually non-throwing. Both actions
        // originate from the single serial key-report stream (plus the 100ms debounce),
        // so Start/Abort cannot reorder in practice.
        if (action == PendingShutdownAction.Start)
        {
            shutdownExecutor.RequestShutdown(seconds);
        }
        else if (action == PendingShutdownAction.Abort)
        {
            shutdownExecutor.Abort();
        }

        return handled;
    }

    private bool HandleIdleKey(CfaKey key, DateTimeOffset now)
    {
        switch (key)
        {
            case CfaKey.Left:
                _category = Previous(_category);
                break;
            case CfaKey.Right:
                _category = Next(_category);
                break;
            case CfaKey.Up when _category == PageCategory.Temperatures:
                _temperaturePage = WrapTemperaturePage(_temperaturePage - 1);
                break;
            case CfaKey.Down when _category == PageCategory.Temperatures:
                _temperaturePage = WrapTemperaturePage(_temperaturePage + 1);
                break;
            case CfaKey.Enter when _category == PageCategory.Shutdown:
                _shutdownState = ShutdownUiState.Confirm;
                _confirmYes = false;
                _pendingSeconds = shutdownOptions.CountdownSeconds;
                break;
            case CfaKey.Enter:
                AutoCycle = !AutoCycle;
                _nextAuto = now.AddSeconds(options.AutoCycleSeconds);
                break;
            case CfaKey.Exit:
                AutoCycle = false;
                _category = PageCategory.DateTime;
                break;
            default:
                return false;
        }

        return true;
    }

    private bool HandleConfirmKey(CfaKey key, DateTimeOffset now, out PendingShutdownAction action)
    {
        action = PendingShutdownAction.None;
        switch (key)
        {
            case CfaKey.Left:
                _confirmYes = true;
                break;
            case CfaKey.Right:
                _confirmYes = false;
                break;
            case CfaKey.Up:
                _pendingSeconds = Math.Min(ShutdownOptions.MaxCountdownSeconds, _pendingSeconds + 5);
                break;
            case CfaKey.Down:
                _pendingSeconds = Math.Max(ShutdownOptions.MinCountdownSeconds, _pendingSeconds - 5);
                break;
            case CfaKey.Enter when _confirmYes:
                _shutdownDeadline = now.AddSeconds(_pendingSeconds);
                _shutdownState = ShutdownUiState.CountingDown;
                AutoCycle = false;
                action = PendingShutdownAction.Start;
                break;
            case CfaKey.Enter:
            case CfaKey.Exit:
                _shutdownState = ShutdownUiState.Idle;
                break;
            default:
                return false;
        }

        return true;
    }

    private bool HandleCountdownKey(CfaKey key, out PendingShutdownAction action)
    {
        action = PendingShutdownAction.None;
        if (key != CfaKey.Exit)
        {
            // Page switching and re-arming are blocked while the countdown runs.
            return false;
        }

        action = PendingShutdownAction.Abort;
        _shutdownState = ShutdownUiState.Idle;
        _category = PageCategory.DateTime;
        AutoCycle = false;
        return true;
    }

    public bool Tick(DateTimeOffset now)
    {
        lock (_sync)
        {
            // Confirm and countdown are modal: never page away under the user's feet.
            if (_shutdownState != ShutdownUiState.Idle)
            {
                return false;
            }

            if (!AutoCycle)
            {
                return false;
            }

            if (_nextAuto == DateTimeOffset.MinValue)
            {
                _nextAuto = now.AddSeconds(options.AutoCycleSeconds);
                return false;
            }

            if (now < _nextAuto)
            {
                return false;
            }

            int pages = TemperaturePageCount();
            if (_category == PageCategory.Temperatures && _temperaturePage + 1 < pages)
            {
                _temperaturePage++;
            }
            else
            {
                // Auto-cycle skips the Shutdown page (it may still advance *off* it when
                // the user parks there with auto-cycle on); manual Left/Right reaches it.
                _category = NextAutoCycle(_category);
                if (_category == PageCategory.Temperatures)
                {
                    _temperaturePage = 0;
                }
            }

            _nextAuto = now.AddSeconds(options.AutoCycleSeconds);
            return true;
        }
    }

    public string[] Render(MetricSnapshot snapshot, ThermalOptions thermal)
    {
        lock (_sync)
        {
            _temperatureCount = snapshot.Temperatures.Count;
            _temperaturePage = Math.Min(_temperaturePage, Math.Max(0, TemperaturePageCount() - 1));
            return _shutdownState switch
            {
                ShutdownUiState.Confirm => RenderShutdownConfirm(),
                ShutdownUiState.CountingDown => RenderShutdownCountdown(snapshot.Timestamp),
                _ => _category switch
                {
                    PageCategory.Cpu => RenderCpu(snapshot, thermal),
                    PageCategory.Temperatures => RenderTemperatures(snapshot),
                    PageCategory.Network => RenderNetwork(snapshot),
                    PageCategory.Shutdown => RenderShutdownIdle(),
                    _ => RenderDateTime(snapshot.Timestamp)
                }
            };
        }
    }

    private string[] RenderDateTime(DateTimeOffset timestamp) =>
    [
        ScreenFormatter.Fit("DATE / TIME"),
        ScreenFormatter.Fit(timestamp.LocalDateTime.ToString(options.DateFormat, CultureInfo.InvariantCulture)),
        ScreenFormatter.Fit(timestamp.LocalDateTime.ToString(options.TimeFormat, CultureInfo.InvariantCulture)),
        ScreenFormatter.Fit($"AUTO: {(AutoCycle ? "ON" : "OFF")}")
    ];

    private static string[] RenderCpu(MetricSnapshot snapshot, ThermalOptions thermal)
    {
        double? displayedTemperature = snapshot.HottestCpuC ?? snapshot.Temperatures
            .Where(reading => reading.Celsius.HasValue && double.IsFinite(reading.Celsius.Value))
            .Select(reading => reading.Celsius)
            .Max();
        bool isCpuTemperature = snapshot.HottestCpuC.HasValue;
        string hottest = ScreenFormatter.TemperatureValue(displayedTemperature);
        double? margin = snapshot.HottestCpuC.HasValue ? thermal.TjMaxC - snapshot.HottestCpuC.Value : null;
        return
        [
            ScreenFormatter.Fit("CPU UTILIZATION"),
            ScreenFormatter.Fit($"Total{snapshot.CpuPercent,14:0.0}%"),
            ScreenFormatter.Fit($"{(isCpuTemperature ? "Hottest" : "System")}{hottest,13}"),
            isCpuTemperature
                ? ScreenFormatter.Fit($"TjMax left{ScreenFormatter.TemperatureValue(margin),10}")
                : ScreenFormatter.Fit("CPU TEMP UNAVAILABLE")
        ];
    }

    private string[] RenderTemperatures(MetricSnapshot snapshot)
    {
        int start = _temperaturePage * 3;
        int end = Math.Min(start + 3, snapshot.Temperatures.Count);
        string header = snapshot.Temperatures.Count == 0
            ? "TEMPS 00-00/00"
            : $"TEMPS {start + 1:00}-{end:00}/{snapshot.Temperatures.Count:00}";
        string[] rows =
        [
            ScreenFormatter.Fit(header),
            ScreenFormatter.Fit("No temperature data"),
            ScreenFormatter.Fit(string.Empty),
            ScreenFormatter.Fit(string.Empty)
        ];

        for (int index = start; index < end; index++)
        {
            TemperatureReading sensor = snapshot.Temperatures[index];
            string label = sensor.DisplayName;
            if (label.Length > 14)
            {
                label = label[..14];
            }

            rows[(index - start) + 1] = ScreenFormatter.Fit(label.PadRight(14) + ScreenFormatter.TemperatureValue(sensor.Celsius));
        }

        return rows;
    }

    private string[] RenderShutdownIdle() =>
    [
        ScreenFormatter.Fit("SHUTDOWN"),
        ScreenFormatter.Fit("Press ENTER to shut"),
        ScreenFormatter.Fit("down this device"),
        ScreenFormatter.Fit($"Timeout: {shutdownOptions.CountdownSeconds}s")
    ];

    private string[] RenderShutdownConfirm() =>
    [
        ScreenFormatter.Fit("SHUTDOWN DEVICE?"),
        ScreenFormatter.Fit($"Delay: {_pendingSeconds}s (UP/DN)"),
        ScreenFormatter.Fit(_confirmYes ? " >YES<         NO" : "  YES         >NO<"),
        ScreenFormatter.Fit("ENTER=OK  X=BACK")
    ];

    private string[] RenderShutdownCountdown(DateTimeOffset timestamp)
    {
        int remaining = Math.Max(0, (int)Math.Ceiling((_shutdownDeadline - timestamp).TotalSeconds));
        return
        [
            ScreenFormatter.Fit("SHUTTING DOWN"),
            ScreenFormatter.Fit($"IN {remaining}s"),
            ScreenFormatter.Fit(string.Empty),
            ScreenFormatter.Fit("PRESS X TO CANCEL")
        ];
    }

    private static string[] RenderNetwork(MetricSnapshot snapshot) =>
    [
        ScreenFormatter.Fit("NETWORK Mbps"),
        ScreenFormatter.Fit($"Rx{ScreenFormatter.Rate(snapshot.ReceiveMbps),18}"),
        ScreenFormatter.Fit($"Tx{ScreenFormatter.Rate(snapshot.TransmitMbps),18}"),
        ScreenFormatter.Fit($"Total{ScreenFormatter.Rate(snapshot.ReceiveMbps + snapshot.TransmitMbps),15}")
    ];

    private int WrapTemperaturePage(int page)
    {
        int count = TemperaturePageCount();
        return ((page % count) + count) % count;
    }

    private int TemperaturePageCount() => Math.Max(1, (_temperatureCount + 2) / 3);

    private static readonly int PageCount = Enum.GetValues<PageCategory>().Length;

    private static PageCategory Next(PageCategory category) =>
        (PageCategory)(((int)category + 1) % PageCount);

    private static PageCategory Previous(PageCategory category) =>
        (PageCategory)(((int)category + PageCount - 1) % PageCount);

    // Auto-cycle never lands on the Shutdown page; it is reachable only manually.
    private static PageCategory NextAutoCycle(PageCategory category)
    {
        PageCategory next = Next(category);
        return next == PageCategory.Shutdown ? Next(next) : next;
    }
}

public sealed class ScreenWriter
{
    private readonly string?[] _lastRows = new string?[4];

    public async Task RenderAsync(Cfa835Device device, IReadOnlyList<string> rows, CancellationToken cancellationToken)
    {
        if (rows.Count != 4)
        {
            throw new ArgumentException("A CFA835 text screen must have exactly four rows.", nameof(rows));
        }

        for (int row = 0; row < 4; row++)
        {
            string fitted = ScreenFormatter.Fit(rows[row]);
            if (!string.Equals(fitted, _lastRows[row], StringComparison.Ordinal))
            {
                await device.WriteRowAsync(row, fitted, cancellationToken).ConfigureAwait(false);
                _lastRows[row] = fitted;
            }
        }
    }

    public void Reset() => Array.Clear(_lastRows);
}

public readonly record struct LedColor(byte Green, byte Red)
{
    public static LedColor Off => new(0, 0);
    public static LedColor GreenOnly => new(100, 0);
    public static LedColor RedOnly => new(0, 100);
    public static LedColor Amber => new(100, 100);
}

public sealed class LedStateMachine(ThermalOptions options)
{
    private bool _thermalWarning;

    public IReadOnlyList<LedColor> Evaluate(MetricSnapshot snapshot)
    {
        double entry = options.TjMaxC - options.WarningMarginC;
        double clear = entry - options.ClearHysteresisC;
        if (!snapshot.HottestCpuC.HasValue)
        {
            _thermalWarning = false;
        }
        else if (_thermalWarning)
        {
            _thermalWarning = snapshot.HottestCpuC.Value >= clear;
        }
        else
        {
            _thermalWarning = snapshot.HottestCpuC.Value >= entry;
        }

        LedColor network = (snapshot.NetworkReceiveActive, snapshot.NetworkTransmitActive) switch
        {
            (true, true) => LedColor.Amber,
            (true, false) => LedColor.GreenOnly,
            (false, true) => LedColor.RedOnly,
            _ => LedColor.Off
        };
        bool thermalOn = _thermalWarning && ((snapshot.Timestamp.ToUnixTimeMilliseconds() / 250) % 2 == 0);

        return
        [
            LedColor.GreenOnly,
            snapshot.DiskActive ? LedColor.Amber : LedColor.Off,
            network,
            thermalOn ? LedColor.RedOnly : LedColor.Off
        ];
    }

    public bool ThermalWarning => _thermalWarning;
}

public sealed class SimulationMetricSource : IMetricSource
{
    private readonly IMetricSource _inner;
    private readonly string _scenario;

    public SimulationMetricSource(IMetricSource inner, string scenario)
    {
        _inner = inner;
        _scenario = scenario.ToLowerInvariant();
        string[] valid = ["thermal-89", "thermal-90", "thermal-92", "disk", "network-rx", "network-tx", "network-both"];
        if (!valid.Contains(_scenario, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown simulation '{scenario}'. Valid values: {string.Join(", ", valid)}");
        }
    }

    public bool IsPawnIoInstalled => _inner.IsPawnIoInstalled;

    public MetricSnapshot Sample(DateTimeOffset now)
    {
        MetricSnapshot source = _inner.Sample(now);
        double? temperature = _scenario switch
        {
            "thermal-89" => 89,
            "thermal-90" => 90,
            "thermal-92" => 92,
            _ => source.HottestCpuC
        };
        IReadOnlyList<TemperatureReading> readings = _scenario.StartsWith("thermal-", StringComparison.Ordinal)
            ? [new TemperatureReading("Intel Core i7-7700", "Simulation", temperature, true)]
            : source.Temperatures;

        return source with
        {
            Timestamp = now,
            Temperatures = readings,
            HottestCpuC = temperature,
            DiskActive = _scenario == "disk" || source.DiskActive,
            NetworkReceiveActive = _scenario is "network-rx" or "network-both" || source.NetworkReceiveActive,
            NetworkTransmitActive = _scenario is "network-tx" or "network-both" || source.NetworkTransmitActive
        };
    }

    public IReadOnlyList<InterfaceReading> GetInterfaces() => _inner.GetInterfaces();
    public void Dispose() => _inner.Dispose();
}
