using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Cfa835SystemMonitor;

public sealed class CfaDeviceLocator(DeviceOptions options)
{
    public string ResolvePort()
    {
        string usbKeyName = $"VID_{options.Vid.ToUpperInvariant()}&PID_{options.Pid.ToUpperInvariant()}";
        using RegistryKey? usb = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{usbKeyName}");
        if (usb is not null)
        {
            IEnumerable<string> serials = usb.GetSubKeyNames();
            if (!string.IsNullOrWhiteSpace(options.Serial))
            {
                serials = serials.Where(serial => serial.Equals(options.Serial, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string serial in serials)
            {
                using RegistryKey? parameters = usb.OpenSubKey($@"{serial}\Device Parameters");
                if (parameters?.GetValue("PortName") is string port && IsPresent(port))
                {
                    return port;
                }
            }
        }

        if (IsPresent(options.FallbackPort))
        {
            return options.FallbackPort;
        }

        throw new FileNotFoundException(
            $"CFA835 USB {usbKeyName}, serial '{options.Serial}', was not found. Fallback {options.FallbackPort} is not present.");
    }

    private static bool IsPresent(string port) =>
        System.IO.Ports.SerialPort.GetPortNames().Contains(port, StringComparer.OrdinalIgnoreCase);
}

public sealed class Cfa835Device(ICfaTransport transport, ILogger<Cfa835Device> logger) : IAsyncDisposable
{
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

    public async Task BlankAndTurnOffAsync(CancellationToken cancellationToken)
    {
        for (int row = 0; row < 4; row++)
        {
            await WriteRowAsync(row, string.Empty, cancellationToken).ConfigureAwait(false);
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
