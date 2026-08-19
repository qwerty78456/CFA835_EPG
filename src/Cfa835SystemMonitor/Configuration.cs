using System.Globalization;
using System.Text.Json;

namespace Cfa835SystemMonitor;

public sealed class MonitorOptions
{
    public DeviceOptions Device { get; init; } = new();
    public SamplingOptions Sampling { get; init; } = new();
    public DisplayOptions Display { get; init; } = new();
    public ThermalOptions Thermal { get; init; } = new();

    public static MonitorOptions Load(string? requestedPath)
    {
        string path = requestedPath ?? DefaultConfigPath();
        MonitorOptions options;

        if (File.Exists(path))
        {
            string json = File.ReadAllText(path);
            options = JsonSerializer.Deserialize<MonitorOptions>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? throw new InvalidDataException($"Configuration '{path}' is empty.");
        }
        else
        {
            options = new MonitorOptions();
        }

        options.Validate();
        return options;
    }

    public static string DefaultConfigPath()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string installed = Path.Combine(programData, "Cfa835SystemMonitor", "appsettings.json");
        if (File.Exists(installed))
        {
            return installed;
        }

        return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    private void Validate()
    {
        Device.Validate();
        Sampling.Validate();
        Display.Validate();
        Thermal.Validate();
    }
}

public sealed class DeviceOptions
{
    public string Vid { get; init; } = "223B";
    public string Pid { get; init; } = "0005";
    public string Serial { get; init; } = "1711735TMLD419715";
    public string FallbackPort { get; init; } = "COM3";

    internal void Validate()
    {
        if (!IsHexWord(Vid) || !IsHexWord(Pid))
        {
            throw new InvalidDataException("device.vid and device.pid must each contain four hexadecimal digits.");
        }

        if (string.IsNullOrWhiteSpace(FallbackPort) || !FallbackPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("device.fallbackPort must be a Windows COM port name.");
        }
    }

    private static bool IsHexWord(string value) =>
        value.Length == 4 && value.All(Uri.IsHexDigit);
}

public sealed class SamplingOptions
{
    public int TemperatureMs { get; init; } = 1000;
    public int ActivityMs { get; init; } = 100;
    public int DisplayMs { get; init; } = 1000;

    internal void Validate()
    {
        if (TemperatureMs is < 250 or > 60_000)
        {
            throw new InvalidDataException("sampling.temperatureMs must be between 250 and 60000.");
        }

        if (ActivityMs is < 50 or > 5_000)
        {
            throw new InvalidDataException("sampling.activityMs must be between 50 and 5000.");
        }

        if (DisplayMs is < 250 or > 60_000)
        {
            throw new InvalidDataException("sampling.displayMs must be between 250 and 60000.");
        }
    }
}

public sealed class DisplayOptions
{
    public bool AutoCycleOnStart { get; init; }
    public int AutoCycleSeconds { get; init; } = 5;
    public string DateFormat { get; init; } = "yyyy-MM-dd";
    public string TimeFormat { get; init; } = "HH:mm:ss";

    internal void Validate()
    {
        if (AutoCycleSeconds is < 2 or > 300)
        {
            throw new InvalidDataException("display.autoCycleSeconds must be between 2 and 300.");
        }

        try
        {
            _ = DateTime.Now.ToString(DateFormat, CultureInfo.InvariantCulture);
            _ = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("display date/time format is invalid.", exception);
        }
    }
}

public sealed class ThermalOptions
{
    public double TjMaxC { get; init; } = 100;
    public double WarningMarginC { get; init; } = 10;
    public double ClearHysteresisC { get; init; } = 2;

    internal void Validate()
    {
        if (TjMaxC is < 50 or > 150 || WarningMarginC is <= 0 or > 50 || ClearHysteresisC is < 0 or > 20)
        {
            throw new InvalidDataException("thermal settings are outside their safe validation ranges.");
        }
    }
}

public enum AppMode
{
    Monitor,
    Diagnose,
    HardwareTest
}

public sealed record CommandLineOptions(
    AppMode Mode,
    string? ConfigPath,
    string? Simulation,
    bool NonInteractive)
{
    public static CommandLineOptions Parse(string[] args)
    {
        AppMode mode = AppMode.Monitor;
        string? config = null;
        string? simulation = null;
        bool nonInteractive = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--diagnose":
                    mode = AppMode.Diagnose;
                    break;
                case "--hardware-test":
                    mode = AppMode.HardwareTest;
                    break;
                case "--noninteractive":
                    nonInteractive = true;
                    break;
                case "--config" when index + 1 < args.Length:
                    config = args[++index];
                    break;
                case "--simulate" when index + 1 < args.Length:
                    simulation = args[++index];
                    break;
                case "--help":
                case "-h":
                case "/?":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[index]}");
            }
        }

        if (simulation is not null && mode != AppMode.Monitor)
        {
            throw new ArgumentException("--simulate is supported only in normal interactive monitor mode.");
        }

        return new CommandLineOptions(mode, config, simulation, nonInteractive);
    }

    public static string HelpText => """
        CFA835 System Monitor
          --diagnose                 Read-only device and metric diagnostics
          --hardware-test            Exercise LCD, keypad and status LEDs
          --noninteractive           Use a timed hardware test without waiting for keys
          --config <path>            Use an alternate appsettings.json
          --simulate <scenario>      thermal-89|thermal-90|thermal-92|disk|network-rx|network-tx|network-both
        """;
}

public sealed class HelpRequestedException : Exception;
