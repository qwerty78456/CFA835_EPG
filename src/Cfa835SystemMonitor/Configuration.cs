using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cfa835SystemMonitor;

public enum DisplayMode
{
    /// <summary>The 20x4 character API (command 31 / 0x1F).</summary>
    Text,

    /// <summary>Host-composited 244x68 greyscale frames driven by layout.json.</summary>
    Graphic
}

public sealed class MonitorOptions
{
    public DeviceOptions Device { get; init; } = new();
    public SamplingOptions Sampling { get; init; } = new();
    public DisplayOptions Display { get; init; } = new();
    public ThermalOptions Thermal { get; init; } = new();
    public ShutdownOptions Shutdown { get; init; } = new();

    /// <summary>Directory the configuration came from; relative layout and artwork paths hang off it.</summary>
    [JsonIgnore]
    public string ConfigDirectory { get; private set; } = AppContext.BaseDirectory;

    /// <summary>Absolute path of the layout file, whether or not graphic mode is enabled.</summary>
    public string ResolveLayoutPath() => Path.IsPathRooted(Display.LayoutPath)
        ? Display.LayoutPath
        : Path.Combine(ConfigDirectory, Display.LayoutPath);

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

        options.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
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
        Shutdown.Validate();
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

    /// <summary>"text" (default, unchanged behaviour) or "graphic" (layout.json driven).</summary>
    public string Mode { get; init; } = "text";

    /// <summary>Layout file used by graphic mode; relative paths resolve next to appsettings.json.</summary>
    public string LayoutPath { get; init; } = "layout.json";

    [JsonIgnore]
    public DisplayMode ResolvedMode { get; private set; }

    internal void Validate()
    {
        ResolvedMode = Mode.ToLowerInvariant() switch
        {
            "text" => DisplayMode.Text,
            "graphic" => DisplayMode.Graphic,
            _ => throw new InvalidDataException("display.mode must be 'text' or 'graphic'.")
        };

        if (string.IsNullOrWhiteSpace(LayoutPath))
        {
            throw new InvalidDataException("display.layoutPath must not be empty.");
        }

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

public sealed class ShutdownOptions
{
    public const int MinCountdownSeconds = 5;
    public const int MaxCountdownSeconds = 3600;

    public int CountdownSeconds { get; init; } = 30;

    internal void Validate()
    {
        if (CountdownSeconds is < MinCountdownSeconds or > MaxCountdownSeconds)
        {
            throw new InvalidDataException(
                $"shutdown.countdownSeconds must be between {MinCountdownSeconds} and {MaxCountdownSeconds}.");
        }
    }
}

public enum AppMode
{
    Monitor,
    Diagnose,
    HardwareTest,
    LayoutPreview
}

public sealed record CommandLineOptions(
    AppMode Mode,
    string? ConfigPath,
    string? Simulation,
    bool NonInteractive,
    string? PreviewPath = null,
    int PreviewScale = 4,
    string? PreviewPage = null)
{
    public static CommandLineOptions Parse(string[] args)
    {
        AppMode mode = AppMode.Monitor;
        string? config = null;
        string? simulation = null;
        bool nonInteractive = false;
        string? previewPath = null;
        int previewScale = 4;
        string? previewPage = null;

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
                case "--layout-preview":
                    mode = AppMode.LayoutPreview;
                    if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        previewPath = args[++index];
                    }

                    break;
                case "--preview-page" when index + 1 < args.Length:
                    previewPage = args[++index];
                    break;
                case "--preview-scale" when index + 1 < args.Length:
                    if (!int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out previewScale) ||
                        previewScale is < 1 or > 16)
                    {
                        throw new ArgumentException("--preview-scale must be an integer between 1 and 16.");
                    }

                    index++;
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

        if ((previewPage is not null || previewPath is not null) && mode != AppMode.LayoutPreview)
        {
            throw new ArgumentException("--preview-page and --preview-scale require --layout-preview.");
        }

        return new CommandLineOptions(mode, config, simulation, nonInteractive, previewPath, previewScale, previewPage);
    }

    public static string HelpText => """
        CFA835 System Monitor
          --diagnose                 Read-only device and metric diagnostics
          --hardware-test            Exercise LCD, keypad and status LEDs
          --noninteractive           Use a timed hardware test without waiting for keys
          --config <path>            Use an alternate appsettings.json
          --simulate <scenario>      thermal-89|thermal-90|thermal-92|disk|network-rx|network-tx|network-both
          --layout-preview [file]    Render the graphic layout to a PNG; no CFA835 required
          --preview-page <id>        Layout page to preview (default: the first page)
          --preview-scale <1-16>     Pixel magnification of the preview PNG (default: 4)
        """;
}

public sealed class HelpRequestedException : Exception;
