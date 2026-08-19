using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Cfa835SystemMonitor;

public sealed class WindowsMetricSource : IMetricSource
{
    private readonly MonitorOptions _options;
    private readonly ILogger<WindowsMetricSource> _logger;
    private readonly CpuUsageSampler _cpu = new();
    private readonly DiskActivitySampler _disk;
    private readonly NetworkRateSampler _network;
    private readonly TemperatureMonitor _temperatures;
    private DateTimeOffset _nextHardwareSample = DateTimeOffset.MinValue;
    private double _cpuPercent;
    private IReadOnlyList<TemperatureReading> _temperatureReadings = Array.Empty<TemperatureReading>();
    private double? _hottestCpu;

    public WindowsMetricSource(MonitorOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<WindowsMetricSource>();
        _disk = new DiskActivitySampler(loggerFactory.CreateLogger<DiskActivitySampler>());
        _network = new NetworkRateSampler(new NativeNetworkCounterProvider());
        _temperatures = new TemperatureMonitor(loggerFactory.CreateLogger<TemperatureMonitor>());
    }

    public bool IsPawnIoInstalled => TemperatureMonitor.IsPawnIoPresent();

    public MetricSnapshot Sample(DateTimeOffset now)
    {
        double? diskBytesPerSecond = _disk.Sample();
        NetworkRateSample network = _network.Sample(now);

        if (now >= _nextHardwareSample)
        {
            _nextHardwareSample = now.AddMilliseconds(_options.Sampling.TemperatureMs);
            try
            {
                _cpuPercent = _cpu.Sample();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "CPU utilization sampling failed");
            }

            try
            {
                _temperatureReadings = _temperatures.Poll(now);
                _hottestCpu = CpuTemperatureSelector.Hottest(_temperatureReadings);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Temperature sampling failed");
            }
        }

        return new MetricSnapshot(
            now,
            _cpuPercent,
            _temperatureReadings,
            _hottestCpu,
            network.ReceiveMbps,
            network.TransmitMbps,
            diskBytesPerSecond > 0,
            network.ReceiveActive,
            network.TransmitActive);
    }

    public IReadOnlyList<InterfaceReading> GetInterfaces() => _network.LastInterfaces;

    public void Dispose()
    {
        _disk.Dispose();
        _temperatures.Dispose();
    }
}

public static class CpuTemperatureSelector
{
    public static double? Hottest(IEnumerable<TemperatureReading> readings) => readings
        .Where(reading => reading.IsCpu &&
            reading.Celsius.HasValue &&
            !reading.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
        .Select(reading => reading.Celsius)
        .Max();
}

public sealed class CpuUsageSampler
{
    private ulong? _idle;
    private ulong? _kernel;
    private ulong? _user;

    public double Sample()
    {
        if (!GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        ulong idle = idleTime.ToUInt64();
        ulong kernel = kernelTime.ToUInt64();
        ulong user = userTime.ToUInt64();
        if (!_idle.HasValue)
        {
            _idle = idle;
            _kernel = kernel;
            _user = user;
            return 0;
        }

        double value = Calculate(_idle.Value, _kernel!.Value, _user!.Value, idle, kernel, user);
        _idle = idle;
        _kernel = kernel;
        _user = user;
        return value;
    }

    public static double Calculate(
        ulong previousIdle,
        ulong previousKernel,
        ulong previousUser,
        ulong currentIdle,
        ulong currentKernel,
        ulong currentUser)
    {
        if (currentIdle < previousIdle || currentKernel < previousKernel || currentUser < previousUser)
        {
            return 0;
        }

        ulong idleDelta = currentIdle - previousIdle;
        ulong totalDelta = (currentKernel - previousKernel) + (currentUser - previousUser);
        if (totalDelta == 0 || idleDelta > totalDelta)
        {
            return 0;
        }

        return Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;
        public ulong ToUInt64() => ((ulong)_high << 32) | _low;
    }
}

public sealed class DiskActivitySampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private readonly ILogger<DiskActivitySampler> _logger;
    private IntPtr _query;
    private IntPtr _counter;

    public DiskActivitySampler(ILogger<DiskActivitySampler> logger)
    {
        _logger = logger;
        uint status = PdhOpenQuery(null, UIntPtr.Zero, out _query);
        if (status == 0)
        {
            status = PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Bytes/sec", UIntPtr.Zero, out _counter);
        }

        if (status == 0)
        {
            _ = PdhCollectQueryData(_query);
        }
        else
        {
            _logger.LogWarning("PDH physical-disk counter initialization failed with status 0x{Status:X8}", status);
            Dispose();
        }
    }

    public bool IsAvailable => _query != IntPtr.Zero && _counter != IntPtr.Zero;

    public double? Sample()
    {
        if (!IsAvailable || PdhCollectQueryData(_query) != 0)
        {
            return null;
        }

        uint status = PdhGetFormattedCounterValue(_counter, PdhFmtDouble, IntPtr.Zero, out PdhFormattedCounterValue value);
        if (status != 0 || value.CStatus != 0)
        {
            return null;
        }

        return Math.Max(0, value.DoubleValue);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        IntPtr counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }
}

public interface INetworkCounterProvider
{
    IReadOnlyList<InterfaceReading> ReadPhysicalInterfaces();
}

public sealed record NetworkRateSample(
    double ReceiveMbps,
    double TransmitMbps,
    bool ReceiveActive,
    bool TransmitActive);

public sealed class NetworkRateSampler(INetworkCounterProvider provider)
{
    private readonly Dictionary<ulong, (ulong InOctets, ulong OutOctets)> _previous = [];
    private readonly Queue<TrafficInterval> _window = new();
    private DateTimeOffset? _previousTime;

    public IReadOnlyList<InterfaceReading> LastInterfaces { get; private set; } = Array.Empty<InterfaceReading>();

    public NetworkRateSample Sample(DateTimeOffset now)
    {
        IReadOnlyList<InterfaceReading> interfaces = provider.ReadPhysicalInterfaces();
        LastInterfaces = interfaces;
        ulong received = 0;
        ulong transmitted = 0;

        foreach (InterfaceReading current in interfaces)
        {
            if (_previous.TryGetValue(current.Luid, out (ulong InOctets, ulong OutOctets) old))
            {
                if (current.InOctets >= old.InOctets)
                {
                    received += current.InOctets - old.InOctets;
                }

                if (current.OutOctets >= old.OutOctets)
                {
                    transmitted += current.OutOctets - old.OutOctets;
                }
            }

            _previous[current.Luid] = (current.InOctets, current.OutOctets);
        }

        HashSet<ulong> activeLuids = interfaces.Select(item => item.Luid).ToHashSet();
        foreach (ulong removed in _previous.Keys.Where(key => !activeLuids.Contains(key)).ToArray())
        {
            _previous.Remove(removed);
        }

        double duration = _previousTime.HasValue ? Math.Max(0, (now - _previousTime.Value).TotalSeconds) : 0;
        _previousTime = now;
        if (duration > 0 && duration < 10)
        {
            _window.Enqueue(new TrafficInterval(now, duration, received, transmitted));
        }

        DateTimeOffset cutoff = now.AddSeconds(-1);
        while (_window.Count > 1 && _window.Peek().End <= cutoff)
        {
            _window.Dequeue();
        }

        double windowSeconds = _window.Sum(item => item.DurationSeconds);
        ulong windowReceived = SumSaturating(_window.Select(item => item.Received));
        ulong windowTransmitted = SumSaturating(_window.Select(item => item.Transmitted));
        double receiveMbps = windowSeconds > 0 ? windowReceived * 8.0 / windowSeconds / 1_000_000.0 : 0;
        double transmitMbps = windowSeconds > 0 ? windowTransmitted * 8.0 / windowSeconds / 1_000_000.0 : 0;

        return new NetworkRateSample(receiveMbps, transmitMbps, received > 0, transmitted > 0);
    }

    private static ulong SumSaturating(IEnumerable<ulong> values)
    {
        ulong total = 0;
        foreach (ulong value in values)
        {
            total = ulong.MaxValue - total < value ? ulong.MaxValue : total + value;
        }

        return total;
    }

    private sealed record TrafficInterval(DateTimeOffset End, double DurationSeconds, ulong Received, ulong Transmitted);
}

public sealed class NativeNetworkCounterProvider : INetworkCounterProvider
{
    public IReadOnlyList<InterfaceReading> ReadPhysicalInterfaces()
    {
        uint status = GetIfTable2(out IntPtr table);
        if (status != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)status);
        }

        try
        {
            uint count = (uint)Marshal.ReadInt32(table);
            int rowSize = Marshal.SizeOf<MibIfRow2>();
            int firstRowOffset = Align(sizeof(uint), IntPtr.Size);
            List<InterfaceReading> rows = new((int)count);
            for (int index = 0; index < count; index++)
            {
                IntPtr rowPointer = IntPtr.Add(table, firstRowOffset + (index * rowSize));
                MibIfRow2 row = Marshal.PtrToStructure<MibIfRow2>(rowPointer);
                bool hardware = (row.InterfaceAndOperStatusFlags & 0x01) != 0;
                bool endpoint = (row.InterfaceAndOperStatusFlags & 0x80) != 0;
                if (hardware && !endpoint && row.OperStatus == 1 && row.Type is not 24 and not 131)
                {
                    rows.Add(new InterfaceReading(
                        row.InterfaceLuid,
                        row.Alias ?? string.Empty,
                        row.Description ?? string.Empty,
                        row.InOctets,
                        row.OutOctets));
                }
            }

            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string? Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string? Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }
}

public sealed class TemperatureMonitor(ILogger<TemperatureMonitor> logger) : IDisposable
{
    private Computer? _computer;
    private DateTimeOffset _openedAt;

    public IReadOnlyList<TemperatureReading> Poll(DateTimeOffset now)
    {
        if (_computer is null || now - _openedAt >= TimeSpan.FromSeconds(60))
        {
            Reopen(now);
        }

        List<TemperatureReading> readings = [];
        foreach (IHardware hardware in _computer!.Hardware)
        {
            UpdateAndCollect(hardware, readings);
        }

        return readings
            .OrderBy(item => item.Hardware, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Reopen(DateTimeOffset now)
    {
        _computer?.Close();
        _computer = new Computer
        {
            IsBatteryEnabled = true,
            IsControllerEnabled = true,
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsNetworkEnabled = true,
            IsPsuEnabled = true,
            IsStorageEnabled = true
        };
        _computer.Open();
        _openedAt = now;
        logger.LogInformation("LibreHardwareMonitor hardware inventory opened; PawnIO installed: {PawnIo}", IsPawnIoPresent());
    }

    private static void UpdateAndCollect(IHardware hardware, ICollection<TemperatureReading> readings)
    {
        hardware.Update();
        foreach (ISensor sensor in hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature))
        {
            readings.Add(new TemperatureReading(
                hardware.Name,
                sensor.Name,
                sensor.Value,
                hardware.HardwareType == HardwareType.Cpu));
        }

        foreach (IHardware child in hardware.SubHardware)
        {
            UpdateAndCollect(child, readings);
        }
    }

    public static bool IsPawnIoPresent()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        if (key is not null)
        {
            return true;
        }

        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using RegistryKey? key32 = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        return key32 is not null;
    }

    public void Dispose()
    {
        _computer?.Close();
        _computer = null;
    }
}
