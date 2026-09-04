using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

public enum InstanceRunContext
{
    Foreground,
    Service
}

public sealed record InstanceDescriptor(
    int Pid,
    DateTime StartTimeUtc,
    string ExecutablePath,
    string Version,
    AppMode Mode,
    int SessionId,
    InstanceRunContext RunContext);

public sealed record TakeoverRequest(int ProtocolVersion, InstanceDescriptor Requester);

public sealed record TakeoverResponse(
    int ProtocolVersion,
    bool Accepted,
    string? Reason,
    InstanceDescriptor Owner,
    bool ShutdownWasPending,
    bool ShutdownCancelled);

public sealed record ReplacementPreparationResult(
    bool Accepted,
    string? Reason,
    bool ShutdownWasPending,
    bool ShutdownCancelled)
{
    public static ReplacementPreparationResult Ready(
        bool shutdownWasPending = false,
        bool shutdownCancelled = false) =>
        new(true, null, shutdownWasPending, shutdownCancelled);

    public static ReplacementPreparationResult Rejected(string reason, bool shutdownWasPending = false) =>
        new(false, reason, shutdownWasPending, false);
}

public sealed record InstanceAcquireResult(bool Acquired, string? Error)
{
    public static InstanceAcquireResult Success() => new(true, null);

    public static InstanceAcquireResult Conflict(string error) => new(false, error);
}

/// <summary>
/// Serializes starts and owns the machine-wide CFA835 lease. Mutex acquisition and release are kept
/// on the synchronous Main thread because Win32 mutex ownership is thread-affine.
/// </summary>
public sealed class InstanceCoordinator : IDisposable
{
    private const int ProtocolVersion = 1;
    private const string ServiceName = "Cfa835SystemMonitor";
    private const string ProcessName = "Cfa835SystemMonitor";
    private const string ExecutableName = "Cfa835SystemMonitor.exe";
    private const string StartupMutexName = @"Global\Cfa835SystemMonitor.Startup.v1";
    private const string DeviceMutexName = @"Global\Cfa835SystemMonitor.Device.v1";
    private const string ControlPipeName = "Cfa835SystemMonitor.Control.v1";
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GracefulStopWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForcedStopWait = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger;
    private readonly AppMode _mode;
    private readonly InstanceRunContext _runContext;
    private readonly Mutex _startupMutex;
    private readonly Mutex _deviceMutex;
    private bool _ownsStartupMutex;
    private bool _ownsDeviceMutex;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverTask;

    public InstanceCoordinator(AppMode mode, ILogger<InstanceCoordinator> logger)
    {
        _mode = mode;
        _logger = logger;
        _runContext = Environment.UserInteractive ? InstanceRunContext.Foreground : InstanceRunContext.Service;
        _startupMutex = CreateSecuredMutex(StartupMutexName);
        _deviceMutex = CreateSecuredMutex(DeviceMutexName);
    }

    public static bool UsesCfaDevice(AppMode mode) =>
        mode is AppMode.Monitor or AppMode.Diagnose or AppMode.HardwareTest;

    public static bool MayReplace(InstanceRunContext requester, InstanceRunContext owner) =>
        requester == InstanceRunContext.Foreground && owner == InstanceRunContext.Foreground;

    public InstanceAcquireResult Acquire()
    {
        if (!TryTakeMutex(_startupMutex, StartupWait, "startup", out string? startupError))
        {
            return InstanceAcquireResult.Conflict(startupError!);
        }

        _ownsStartupMutex = true;
        try
        {
            if (_runContext == InstanceRunContext.Foreground)
            {
                ServiceProbe service = ProbeService();
                if (!service.Succeeded)
                {
                    return FailAndRelease(service.Error!);
                }

                if (service.Status is not null and not ServiceControllerStatus.Stopped)
                {
                    return FailAndRelease(
                        $"Windows service '{ServiceName}' is {service.Status}; the foreground process will not stop or replace it.");
                }
            }

            bool acquiredImmediately = TryTakeMutex(_deviceMutex, TimeSpan.Zero, "device", out _);
            if (acquiredImmediately)
            {
                _ownsDeviceMutex = true;
                return ResolveLegacyProcessesWhileOwningDevice();
            }

            if (_runContext == InstanceRunContext.Service)
            {
                return FailAndRelease(
                    "Another CFA835 System Monitor foreground process owns the device; the service will not replace it.");
            }

            PipeAttempt pipe = RequestGracefulReplacement();
            if (pipe.Kind == PipeAttemptKind.Rejected)
            {
                return FailAndRelease(pipe.Error!);
            }

            if (pipe.Kind == PipeAttemptKind.Invalid)
            {
                return FailAndRelease(pipe.Error!);
            }

            if (pipe.Kind == PipeAttemptKind.Accepted)
            {
                if (TryTakeMutex(_deviceMutex, GracefulStopWait, "device", out _))
                {
                    _ownsDeviceMutex = true;
                    return InstanceAcquireResult.Success();
                }

                string? terminationError = null;
                if (pipe.Owner is null || !TryTerminateVerified(pipe.Owner, out terminationError))
                {
                    return FailAndRelease(
                        terminationError ?? "The previous instance accepted replacement but did not release the CFA835 device.");
                }

                if (!TryTakeMutex(_deviceMutex, ForcedStopWait, "device", out string? deviceError))
                {
                    return FailAndRelease(deviceError!);
                }

                _ownsDeviceMutex = true;
                return InstanceAcquireResult.Success();
            }

            return ReplaceLegacyProcessesAndAcquireDevice();
        }
        catch (Exception exception)
        {
            return FailAndRelease($"Could not coordinate CFA835 ownership: {exception.Message}");
        }
    }

    /// <summary>Starts the control endpoint, then lets the next start enter the arbitration gate.</summary>
    public void StartControlServer(
        Func<ReplacementPreparationResult> prepareForReplacement,
        Action stopAfterAcceptedResponse)
    {
        if (!_ownsDeviceMutex)
        {
            throw new InvalidOperationException("The CFA835 device lease has not been acquired.");
        }

        _serverCancellation = new CancellationTokenSource();
        NamedPipeServerStream firstServer = CreatePipeServer();
        _serverTask = Task.Run(
            () => RunControlServerAsync(
                firstServer,
                prepareForReplacement,
                stopAfterAcceptedResponse,
                _serverCancellation.Token));
        ReleaseStartupMutex();
    }

    private InstanceAcquireResult ResolveLegacyProcessesWhileOwningDevice()
    {
        ProcessScan scan = ScanOtherInstances();
        if (scan.Ambiguous.Count > 0)
        {
            return FailAndRelease(
                $"Refusing to stop same-name process(es) that could not be verified: {string.Join(", ", scan.Ambiguous)}.");
        }

        if (scan.Verified.Count == 0)
        {
            return InstanceAcquireResult.Success();
        }

        if (_runContext == InstanceRunContext.Service)
        {
            return FailAndRelease(
                $"The service found {scan.Verified.Count} foreground CFA835 System Monitor process(es) and will not replace them.");
        }

        return AbortShutdownAndTerminate(scan.Verified)
            ? InstanceAcquireResult.Success()
            : FailAndRelease("A verified legacy foreground instance could not be stopped safely.");
    }

    private InstanceAcquireResult ReplaceLegacyProcessesAndAcquireDevice()
    {
        ProcessScan scan = ScanOtherInstances();
        if (scan.Ambiguous.Count > 0)
        {
            return FailAndRelease(
                $"CFA835 ownership is busy and same-name process(es) could not be verified: {string.Join(", ", scan.Ambiguous)}.");
        }

        if (scan.Verified.Count == 0)
        {
            return FailAndRelease(
                "CFA835 ownership is busy, but no verifiable foreground owner or compatible control pipe was found.");
        }

        if (!AbortShutdownAndTerminate(scan.Verified))
        {
            return FailAndRelease("A verified legacy foreground instance could not be stopped safely.");
        }

        if (!TryTakeMutex(_deviceMutex, ForcedStopWait, "device", out string? deviceError))
        {
            return FailAndRelease(deviceError!);
        }

        _ownsDeviceMutex = true;
        return InstanceAcquireResult.Success();
    }

    private bool AbortShutdownAndTerminate(IReadOnlyList<InstanceDescriptor> targets)
    {
        WindowsShutdownExecutor shutdown = new(_logger);
        if (!shutdown.TryAbort())
        {
            _logger.LogError("A system shutdown could not be cancelled; legacy process replacement was aborted");
            return false;
        }

        foreach (InstanceDescriptor target in targets)
        {
            if (!TryTerminateVerified(target, out string? error))
            {
                _logger.LogError("Could not stop legacy process {Pid}: {Error}", target.Pid, error);
                return false;
            }
        }

        return true;
    }

    private PipeAttempt RequestGracefulReplacement()
    {
        try
        {
            using NamedPipeClientStream client = new(
                ".", ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            client.Connect(2000);

            InstanceDescriptor requester = DescribeCurrentProcess();
            TakeoverRequest request = new(ProtocolVersion, requester);
            using StreamWriter writer = new(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using StreamReader reader = new(
                client, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));

            string? line = reader.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            if (line is null)
            {
                return PipeAttempt.Invalid("The running instance closed its control pipe without a response.");
            }

            TakeoverResponse? response = JsonSerializer.Deserialize<TakeoverResponse>(line, JsonOptions);
            if (response is null || response.ProtocolVersion != ProtocolVersion)
            {
                return PipeAttempt.Invalid("The running instance returned an incompatible control response.");
            }

            if (!response.Accepted)
            {
                return PipeAttempt.Rejected(response.Reason ?? "The running instance rejected replacement.");
            }

            _logger.LogInformation(
                "Running {Mode} process {Pid} accepted CFA835 handover",
                response.Owner.Mode,
                response.Owner.Pid);
            return PipeAttempt.Accepted(response.Owner);
        }
        catch (Exception exception) when (exception is System.TimeoutException or IOException)
        {
            _logger.LogInformation("No compatible instance control pipe answered: {Message}", exception.Message);
            return PipeAttempt.Unavailable();
        }
        catch (UnauthorizedAccessException exception)
        {
            return PipeAttempt.Invalid($"Access to the running instance control pipe was denied: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return PipeAttempt.Invalid($"The running instance returned invalid control data: {exception.Message}");
        }
    }

    private async Task RunControlServerAsync(
        NamedPipeServerStream firstServer,
        Func<ReplacementPreparationResult> prepareForReplacement,
        Action stopAfterAcceptedResponse,
        CancellationToken cancellationToken)
    {
        NamedPipeServerStream? server = firstServer;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream activeServer = server!;
                using (activeServer)
                {
                    await activeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await HandleControlRequestAsync(
                        activeServer,
                        prepareForReplacement,
                        stopAfterAcceptedResponse,
                        cancellationToken).ConfigureAwait(false);
                }

                server = cancellationToken.IsCancellationRequested ? null : CreatePipeServer();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The instance control pipe stopped unexpectedly");
        }
        finally
        {
            server?.Dispose();
        }
    }

    private async Task HandleControlRequestAsync(
        NamedPipeServerStream server,
        Func<ReplacementPreparationResult> prepareForReplacement,
        Action stopAfterAcceptedResponse,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(
            server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
        using StreamWriter writer = new(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        InstanceDescriptor owner = DescribeCurrentProcess();
        InstanceDescriptor? requester = null;
        TakeoverResponse response;

        try
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            TakeoverRequest? request = line is null
                ? null
                : JsonSerializer.Deserialize<TakeoverRequest>(line, JsonOptions);
            requester = request?.Requester;

            if (request is null || request.ProtocolVersion != ProtocolVersion)
            {
                response = new(ProtocolVersion, false, "Unsupported takeover request.", owner, false, false);
            }
            else if (!MayReplace(request.Requester.RunContext, _runContext))
            {
                response = new(
                    ProtocolVersion,
                    false,
                    "Foreground and service instances do not replace each other.",
                    owner,
                    false,
                    false);
            }
            else
            {
                string? validationError = request.Requester.Pid == owner.Pid
                    ? "requester PID matches the owner PID"
                    : null;
                if (validationError is not null ||
                    !TryValidateDescriptor(request.Requester, out _, out validationError))
                {
                    response = new(
                        ProtocolVersion,
                        false,
                        $"The replacement requester could not be verified: {validationError}",
                        owner,
                        false,
                        false);
                }
                else
                {
                    ReplacementPreparationResult preparation = prepareForReplacement();
                    response = new(
                        ProtocolVersion,
                        preparation.Accepted,
                        preparation.Reason,
                        owner,
                        preparation.ShutdownWasPending,
                        preparation.ShutdownCancelled);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            response = new(ProtocolVersion, false, $"Invalid takeover request: {exception.Message}", owner, false, false);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        if (response.Accepted)
        {
            // Let the replacement receive its acknowledgement before cancellation can tear down
            // this coordinator and its pipe.
            _logger.LogInformation(
                "Accepted CFA835 handover request from {Mode} process {Pid}",
                requester!.Mode,
                requester.Pid);
            stopAfterAcceptedResponse();
        }
    }

    private ProcessScan ScanOtherInstances()
    {
        List<InstanceDescriptor> verified = [];
        List<string> ambiguous = [];
        int currentPid = Environment.ProcessId;

        foreach (Process process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                if (process.Id == currentPid)
                {
                    continue;
                }

                if (TryDescribeVerifiedForegroundProcess(process, out InstanceDescriptor? descriptor, out string? error))
                {
                    verified.Add(descriptor!);
                }
                else
                {
                    ambiguous.Add($"PID {process.Id} ({error})");
                }
            }
        }

        return new(verified, ambiguous);
    }

    private bool TryTerminateVerified(InstanceDescriptor expected, out string? error)
    {
        error = null;
        Process process;
        try
        {
            process = Process.GetProcessById(expected.Pid);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            if (!TryValidateProcess(process, expected, out error))
            {
                return false;
            }

            try
            {
                _logger.LogWarning(
                    "Forcing verified CFA835 System Monitor process {Pid} at {Path} to stop",
                    expected.Pid,
                    expected.ExecutablePath);
                process.Kill(entireProcessTree: false);
                if (!process.WaitForExit((int)ForcedStopWait.TotalMilliseconds))
                {
                    error = $"PID {expected.Pid} did not exit within {ForcedStopWait.TotalSeconds:0} seconds.";
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                error = exception.Message;
                return false;
            }
        }
    }

    private static bool TryValidateDescriptor(
        InstanceDescriptor expected,
        out Process? process,
        out string? error)
    {
        try
        {
            process = Process.GetProcessById(expected.Pid);
        }
        catch (ArgumentException)
        {
            process = null;
            error = $"PID {expected.Pid} no longer exists.";
            return false;
        }

        if (TryValidateProcess(process, expected, out error))
        {
            process.Dispose();
            process = null;
            return true;
        }

        process.Dispose();
        process = null;
        return false;
    }

    private static bool TryValidateProcess(Process process, InstanceDescriptor expected, out string? error)
    {
        if (!TryDescribeVerifiedForegroundProcess(process, out InstanceDescriptor? actual, out error))
        {
            return false;
        }

        if (actual!.StartTimeUtc != expected.StartTimeUtc ||
            !Path.GetFullPath(actual.ExecutablePath).Equals(
                Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase))
        {
            error = "PID, start time, or executable path changed before termination.";
            return false;
        }

        return true;
    }

    private static bool TryDescribeVerifiedForegroundProcess(
        Process process,
        out InstanceDescriptor? descriptor,
        out string? error)
    {
        descriptor = null;
        error = null;
        try
        {
            if (process.SessionId == 0)
            {
                error = "session 0/service process";
                return false;
            }

            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path) ||
                !Path.GetFileName(path).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                error = "unexpected or inaccessible executable path";
                return false;
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            string metadata = string.IsNullOrWhiteSpace(version.ProductName)
                ? version.FileDescription ?? string.Empty
                : version.ProductName;
            if (!NormalizeProductName(metadata).Equals("cfa835systemmonitor", StringComparison.Ordinal))
            {
                error = $"unexpected product metadata '{metadata}'";
                return false;
            }

            descriptor = new(
                process.Id,
                process.StartTime.ToUniversalTime(),
                Path.GetFullPath(path),
                version.ProductVersion ?? "unknown",
                AppMode.Monitor,
                process.SessionId,
                InstanceRunContext.Foreground);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            error = exception.Message;
            return false;
        }
    }

    private ServiceProbe ProbeService()
    {
        try
        {
            ServiceController[] services = ServiceController.GetServices();
            try
            {
                ServiceController? service = services.FirstOrDefault(
                    candidate => candidate.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));
                return service is null
                    ? ServiceProbe.NotInstalled()
                    : ServiceProbe.Found(service.Status);
            }
            finally
            {
                foreach (ServiceController service in services)
                {
                    service.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return ServiceProbe.Failed($"Could not determine whether Windows service '{ServiceName}' is active: {exception.Message}");
        }
    }

    private InstanceDescriptor DescribeCurrentProcess()
    {
        using Process process = Process.GetCurrentProcess();
        string path = Environment.ProcessPath ?? process.MainModule?.FileName ?? ExecutableName;
        string version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        return new(
            process.Id,
            process.StartTime.ToUniversalTime(),
            Path.GetFullPath(path),
            version,
            _mode,
            process.SessionId,
            _runContext);
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ControlPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }

    private static Mutex CreateSecuredMutex(string name)
    {
        MutexSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        return MutexAcl.Create(initiallyOwned: false, name, out _, security);
    }

    private bool TryTakeMutex(Mutex mutex, TimeSpan timeout, string purpose, out string? error)
    {
        try
        {
            if (mutex.WaitOne(timeout))
            {
                error = null;
                return true;
            }

            error = $"Timed out waiting {timeout.TotalSeconds:0} seconds for the {purpose} ownership mutex.";
            return false;
        }
        catch (AbandonedMutexException)
        {
            _logger.LogWarning("Recovered an abandoned {Purpose} ownership mutex", purpose);
            error = null;
            return true;
        }
    }

    private InstanceAcquireResult FailAndRelease(string error)
    {
        if (_ownsDeviceMutex)
        {
            _deviceMutex.ReleaseMutex();
            _ownsDeviceMutex = false;
        }

        ReleaseStartupMutex();
        return InstanceAcquireResult.Conflict(error);
    }

    private void ReleaseStartupMutex()
    {
        if (_ownsStartupMutex)
        {
            _startupMutex.ReleaseMutex();
            _ownsStartupMutex = false;
        }
    }

    private static string NormalizeProductName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public void Dispose()
    {
        _serverCancellation?.Cancel();
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _serverCancellation?.Dispose();
        if (_ownsDeviceMutex)
        {
            _deviceMutex.ReleaseMutex();
            _ownsDeviceMutex = false;
        }

        ReleaseStartupMutex();
        _deviceMutex.Dispose();
        _startupMutex.Dispose();
    }

    private enum PipeAttemptKind
    {
        Unavailable,
        Accepted,
        Rejected,
        Invalid
    }

    private sealed record PipeAttempt(PipeAttemptKind Kind, InstanceDescriptor? Owner, string? Error)
    {
        public static PipeAttempt Unavailable() => new(PipeAttemptKind.Unavailable, null, null);
        public static PipeAttempt Accepted(InstanceDescriptor owner) => new(PipeAttemptKind.Accepted, owner, null);
        public static PipeAttempt Rejected(string error) => new(PipeAttemptKind.Rejected, null, error);
        public static PipeAttempt Invalid(string error) => new(PipeAttemptKind.Invalid, null, error);
    }

    private sealed record ProcessScan(IReadOnlyList<InstanceDescriptor> Verified, IReadOnlyList<string> Ambiguous);

    private sealed record ServiceProbe(bool Succeeded, ServiceControllerStatus? Status, string? Error)
    {
        public static ServiceProbe NotInstalled() => new(true, null, null);
        public static ServiceProbe Found(ServiceControllerStatus status) => new(true, status, null);
        public static ServiceProbe Failed(string error) => new(false, null, error);
    }
}
