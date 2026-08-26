using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Cfa835SystemMonitor;

/// <summary>A port worth trying, with the reason it was suggested (for logs and diagnostics).</summary>
public sealed record PortCandidate(string Port, string Reason);

/// <summary>One USB enumeration entry for the configured VID/PID, as recorded in the registry.</summary>
public sealed record UsbPortEntry(string Serial, string? PortName, bool Present);

public sealed class CfaDeviceLocator(DeviceOptions options)
{
    private string UsbKeyName => $"VID_{options.Vid.ToUpperInvariant()}&PID_{options.Pid.ToUpperInvariant()}";

    public static IReadOnlyList<string> PresentPorts() =>
        System.IO.Ports.SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();

    /// <summary>Every CFA835 the registry knows about, whichever serial it carries. Used by --diagnose.</summary>
    public IReadOnlyList<UsbPortEntry> DescribeUsbEntries()
    {
        using RegistryKey? usb = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{UsbKeyName}");
        if (usb is null)
        {
            return [];
        }

        List<UsbPortEntry> entries = [];
        foreach (string serial in usb.GetSubKeyNames())
        {
            using RegistryKey? parameters = usb.OpenSubKey($@"{serial}\Device Parameters");
            string? port = parameters?.GetValue("PortName") as string;
            entries.Add(new UsbPortEntry(serial, port, port is not null && IsPresent(port)));
        }

        return entries;
    }

    /// <summary>
    /// Ports to try, best first. Crucially this includes CFA835s whose serial does <em>not</em> match
    /// <c>device.serial</c>: pinning the configuration to one unit's serial otherwise makes every other
    /// module fall through to <c>device.fallbackPort</c>, which may well be an unrelated device that
    /// opens fine and then never answers.
    /// </summary>
    public IReadOnlyList<PortCandidate> CandidatePorts() =>
        BuildCandidates(options, DescribeUsbEntries(), PresentPorts());

    /// <summary>
    /// Pure ordering rule behind <see cref="CandidatePorts"/>, separated from registry and serial-port
    /// enumeration so it can be tested against machine states that are awkward to reproduce.
    /// </summary>
    public static IReadOnlyList<PortCandidate> BuildCandidates(
        DeviceOptions options,
        IReadOnlyList<UsbPortEntry> entries,
        IReadOnlyList<string> presentPorts)
    {
        string usbKeyName = $"VID_{options.Vid.ToUpperInvariant()}&PID_{options.Pid.ToUpperInvariant()}";
        List<PortCandidate> candidates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(string? port, string reason)
        {
            if (!string.IsNullOrWhiteSpace(port) &&
                presentPorts.Contains(port, StringComparer.OrdinalIgnoreCase) &&
                seen.Add(port))
            {
                candidates.Add(new PortCandidate(port, reason));
            }
        }

        foreach (UsbPortEntry entry in entries.Where(entry =>
                     !string.IsNullOrWhiteSpace(options.Serial) &&
                     entry.Serial.Equals(options.Serial, StringComparison.OrdinalIgnoreCase)))
        {
            Add(entry.PortName, $"USB {usbKeyName}, configured serial {entry.Serial}");
        }

        // Every other module of the same VID/PID comes before the fallback. A configuration carrying
        // one site's serial must not strand an identical unit that simply has a different one.
        foreach (UsbPortEntry entry in entries)
        {
            Add(entry.PortName, $"USB {usbKeyName}, serial {entry.Serial}");
        }

        Add(options.FallbackPort, "configured device.fallbackPort");

        if (options.ProbeAllPorts)
        {
            foreach (string port in presentPorts)
            {
                Add(port, "probe of remaining serial ports");
            }
        }

        return candidates;
    }

    /// <summary>First candidate without contacting the device. Kept for callers that only need a name.</summary>
    public string ResolvePort() =>
        CandidatePorts().FirstOrDefault()?.Port ?? throw new FileNotFoundException(NotFoundMessage());

    /// <summary>
    /// Picks the port that actually answers a Get Version command. Opening a port proves nothing —
    /// an unrelated COM device opens just as happily and then times out — so each candidate is
    /// verified before the monitor commits to it.
    /// </summary>
    public Task<string> ResolvePortAsync(
        Func<ICfaTransport> transportFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PortCandidate> candidates = CandidatePorts();
        return candidates.Count == 0
            ? throw new FileNotFoundException(NotFoundMessage())
            : ProbeCandidatesAsync(candidates, transportFactory, logger, cancellationToken);
    }

    /// <summary>
    /// Probing loop, separated from registry and serial-port enumeration so it can be tested against
    /// a scripted set of ports.
    /// </summary>
    public static async Task<string> ProbeCandidatesAsync(
        IReadOnlyList<PortCandidate> candidates,
        Func<ICfaTransport> transportFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        PortCandidate? answered = null;
        foreach (PortCandidate candidate in candidates)
        {
            string? version = await IdentifyAsync(candidate.Port, transportFactory, logger, cancellationToken)
                .ConfigureAwait(false);
            if (version is null)
            {
                logger.LogDebug("No response on {Port} ({Reason})", candidate.Port, candidate.Reason);
                continue;
            }

            if (version.Contains("835", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Found {Version} on {Port} via {Reason}", version, candidate.Port, candidate.Reason);
                return candidate.Port;
            }

            // Something is talking the CFA packet protocol but does not call itself an 835. Remember it
            // and keep looking for a better match before settling.
            logger.LogWarning(
                "{Port} answered but reported '{Version}', not a CFA835", candidate.Port, version);
            answered ??= candidate;
        }

        if (answered is not null)
        {
            logger.LogWarning("Falling back to {Port} ({Reason})", answered.Port, answered.Reason);
            return answered.Port;
        }

        throw new FileNotFoundException(
            "No CFA835 answered on any candidate port. Tried: " +
            string.Join(", ", candidates.Select(item => $"{item.Port} ({item.Reason})")) + ".");
    }

    private static async Task<string?> IdentifyAsync(
        string port,
        Func<ICfaTransport> transportFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ICfaTransport transport = transportFactory();
        try
        {
            // SendCommandAsync retries three times at 750 ms; probing a port that will never answer
            // must not cost that much, so cap the whole attempt.
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await transport.OpenAsync(port, attempt.Token).ConfigureAwait(false);
            CfaPacket response = await transport
                .SendCommandAsync(0x01, ReadOnlyMemory<byte>.Empty, attempt.Token)
                .ConfigureAwait(false);
            return Encoding.ASCII.GetString(response.Data).TrimEnd('\0', ' ');
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug(exception, "Probe of {Port} failed", port);
            return null;
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string NotFoundMessage()
    {
        IReadOnlyList<string> present = PresentPorts();
        return $"No candidate port for CFA835 USB {UsbKeyName} (configured serial '{options.Serial}', " +
            $"fallback {options.FallbackPort}). Present ports: " +
            (present.Count == 0 ? "(none)" : string.Join(", ", present)) + ".";
    }

    private static bool IsPresent(string port) =>
        System.IO.Ports.SerialPort.GetPortNames().Contains(port, StringComparer.OrdinalIgnoreCase);
}

public sealed class Cfa835Device(ICfaTransport transport, ILogger<Cfa835Device> logger) : IAsyncDisposable
{
    /// <summary>Pixel width of the CFA835 graphic LCD (datasheet command 40, 0x28).</summary>
    public const int DisplayWidth = 244;

    /// <summary>Pixel height of the CFA835 graphic LCD.</summary>
    public const int DisplayHeight = 68;

    private const byte GraphicCommand = 0x28;

    private static readonly (byte Green, byte Red)[] LedGpio =
    [
        (11, 12),
        (9, 10),
        (7, 8),
        (5, 6)
    ];

    private readonly Dictionary<int, (byte Green, byte Red)> _ledCache = [];

    public bool IsOpen => transport.IsOpen;
    public event Action<CfaKey>? KeyPressed;

    public async Task<string> OpenAsync(string port, bool enableKeyReports, CancellationToken cancellationToken)
    {
        transport.ReportReceived += HandleReport;
        await transport.OpenAsync(port, cancellationToken).ConfigureAwait(false);
        string version = await ReadVersionAsync(cancellationToken).ConfigureAwait(false);
        if (!version.Contains("835", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Device on {Port} did not identify itself explicitly as CFA835: {Version}", port, version);
        }

        if (enableKeyReports)
        {
            await SetKeyMasksAsync(0x3F, 0x00, cancellationToken).ConfigureAwait(false);
        }

        _ledCache.Clear();
        return version;
    }

    public async Task<(byte Press, byte Release)> ReadKeyMasksAsync(CancellationToken cancellationToken)
    {
        CfaPacket response = await transport.SendCommandAsync(0x17, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        if (response.Data.Length != 2)
        {
            throw new InvalidDataException("Unexpected keypad-mask response length.");
        }

        return (response.Data[0], response.Data[1]);
    }

    public async Task SetKeyMasksAsync(byte press, byte release, CancellationToken cancellationToken)
    {
        await transport.SendCommandAsync(0x17, new byte[] { press, release }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadVersionAsync(CancellationToken cancellationToken)
    {
        CfaPacket response = await transport.SendCommandAsync(0x01, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(response.Data).TrimEnd('\0', ' ');
    }

    public async Task WriteRowAsync(int row, string text, CancellationToken cancellationToken)
    {
        if (row is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        string fitted = ScreenFormatter.Fit(text);
        byte[] payload = new byte[22];
        payload[0] = 0;
        payload[1] = (byte)row;
        Encoding.ASCII.GetBytes(fitted, payload.AsSpan(2));
        await transport.SendCommandAsync(0x1F, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string[]> ReadRowsAsync(CancellationToken cancellationToken)
    {
        string[] rows = new string[4];
        for (int row = 0; row < 4; row++)
        {
            CfaPacket response = await transport.SendCommandAsync(0x20, new byte[] { 0, (byte)row, 20 }, cancellationToken).ConfigureAwait(false);
            rows[row] = ScreenFormatter.Fit(Encoding.ASCII.GetString(response.Data));
        }

        return rows;
    }

    public async Task SetLedAsync(int led, byte green, byte red, CancellationToken cancellationToken)
    {
        if (led is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(led));
        }

        green = Math.Min(green, (byte)100);
        red = Math.Min(red, (byte)100);
        if (_ledCache.TryGetValue(led, out (byte Green, byte Red) previous) && previous == (green, red))
        {
            return;
        }

        (byte greenGpio, byte redGpio) = LedGpio[led];
        await transport.SendCommandAsync(0x22, new byte[] { greenGpio, green }, cancellationToken).ConfigureAwait(false);
        await transport.SendCommandAsync(0x22, new byte[] { redGpio, red }, cancellationToken).ConfigureAwait(false);
        _ledCache[led] = (green, red);
    }

    public async Task<(byte Green, byte Red)[]> ReadLedsAsync(CancellationToken cancellationToken)
    {
        (byte Green, byte Red)[] states = new (byte, byte)[4];
        for (int led = 0; led < 4; led++)
        {
            (byte greenGpio, byte redGpio) = LedGpio[led];
            states[led] = (await ReadGpioAsync(greenGpio, cancellationToken).ConfigureAwait(false),
                await ReadGpioAsync(redGpio, cancellationToken).ConfigureAwait(false));
        }

        return states;
    }

    private async Task<byte> ReadGpioAsync(byte gpio, CancellationToken cancellationToken)
    {
        CfaPacket response = await transport.SendCommandAsync(0x22, new byte[] { gpio }, cancellationToken).ConfigureAwait(false);
        if (response.Data.Length < 3 || response.Data[0] != gpio)
        {
            throw new InvalidDataException($"Unexpected GPIO read response for index {gpio}.");
        }

        // data[1] is the sampled pin/edge flags. data[2] is the configured
        // PWM output level on CFA835 h1.0/f0.6 through current firmware.
        return (byte)Math.Min(response.Data[2], (byte)100);
    }

    /// <summary>Command 6 (0x06): clears the text buffer, the graphic buffer and any playing video.</summary>
    public async Task ClearDisplayAsync(CancellationToken cancellationToken)
    {
        await transport.SendCommandAsync(0x06, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Command 40 (0x28) subcommand 0. With <paramref name="manualFlush"/> the module accumulates every
    /// graphic write until <see cref="FlushBufferAsync"/> runs, so a frame appears in one piece.
    /// Note that command 31 (0x1F) text writes bypass the buffer entirely and must not be mixed in.
    /// </summary>
    public async Task SetGraphicOptionsAsync(bool manualFlush, bool gammaCorrection, CancellationToken cancellationToken)
    {
        byte flags = (byte)((manualFlush ? 0x01 : 0x00) | (gammaCorrection ? 0x02 : 0x00));
        await transport.SendCommandAsync(GraphicCommand, new byte[] { 0, flags }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Command 40 (0x28) subcommand 1: push the graphic buffer to the panel.</summary>
    public async Task FlushBufferAsync(CancellationToken cancellationToken)
    {
        await transport.SendCommandAsync(GraphicCommand, new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Command 40 (0x28) subcommand 2: write a rectangle of raw 8-bit greyscale pixels at (x, y).
    /// The pixel stream leaves the packet layer, so it carries no CRC — callers repaint periodically.
    /// </summary>
    public async Task SendImageAsync(
        int x,
        int y,
        int width,
        int height,
        ReadOnlyMemory<byte> pixels,
        bool transparency,
        bool invert,
        CancellationToken cancellationToken)
    {
        ValidateRectangle(x, y, width, height);
        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"Expected {width * height} pixel bytes for a {width}x{height} image but received {pixels.Length}.",
                nameof(pixels));
        }

        byte flags = (byte)((transparency ? 0x01 : 0x00) | (invert ? 0x02 : 0x00));
        byte[] header = [2, flags, (byte)x, (byte)y, (byte)width, (byte)height];
        await transport.SendStreamingCommandAsync(GraphicCommand, header, pixels, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Command 40 (0x28) subcommand 5 (read form). Used by diagnostics to confirm shade polarity and
    /// glyph geometry on real hardware without needing a microSD card for a screenshot.
    /// </summary>
    public async Task<byte> ReadPixelAsync(int x, int y, CancellationToken cancellationToken)
    {
        ValidateRectangle(x, y, 1, 1);
        CfaPacket response = await transport
            .SendCommandAsync(GraphicCommand, new byte[] { 5, (byte)x, (byte)y }, cancellationToken)
            .ConfigureAwait(false);
        if (response.Data.Length < 2 || response.Data[0] != 5)
        {
            throw new InvalidDataException($"Unexpected pixel-read response for ({x}, {y}).");
        }

        return response.Data[1];
    }

    /// <summary>Command 40 (0x28) subcommand 7: outline a rectangle, used to preview layout boxes.</summary>
    public async Task DrawRectangleAsync(
        int x,
        int y,
        int width,
        int height,
        byte lineShade,
        byte fillShade,
        CancellationToken cancellationToken)
    {
        ValidateRectangle(x, y, width, height);
        byte[] payload = [7, (byte)x, (byte)y, (byte)width, (byte)height, lineShade, fillShade];
        await transport.SendCommandAsync(GraphicCommand, payload, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRectangle(int x, int y, int width, int height)
    {
        if (width < 1 || height < 1 || x < 0 || y < 0 ||
            x + width > DisplayWidth || y + height > DisplayHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Rectangle ({x}, {y}, {width}, {height}) does not fit the {DisplayWidth}x{DisplayHeight} display.");
        }
    }

    public async Task BlankAndTurnOffAsync(CancellationToken cancellationToken, bool graphicMode = false)
    {
        if (graphicMode)
        {
            // Blanking text rows would leave the composited artwork behind, so clear both buffers.
            await ClearDisplayAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            for (int row = 0; row < 4; row++)
            {
                await WriteRowAsync(row, string.Empty, cancellationToken).ConfigureAwait(false);
            }
        }

        for (int led = 0; led < 4; led++)
        {
            await SetLedAsync(led, 0, 0, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleReport(CfaPacket packet)
    {
        if (packet.Type == 0x80 && packet.Data.Length == 1 && packet.Data[0] is >= 1 and <= 6)
        {
            KeyPressed?.Invoke((CfaKey)packet.Data[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        transport.ReportReceived -= HandleReport;
        await transport.DisposeAsync().ConfigureAwait(false);
    }
}
