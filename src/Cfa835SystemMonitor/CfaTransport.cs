using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor;

public interface ICfaTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    event Action<CfaPacket>? ReportReceived;
    event Action<Exception?>? ConnectionLost;
    Task OpenAsync(string portName, CancellationToken cancellationToken);
    Task<CfaPacket> SendCommandAsync(byte command, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a command packet and then streams <paramref name="payload"/> as raw, un-packetized,
    /// un-CRC-checked bytes. The CFA835 only acknowledges once the whole stream has arrived, so this
    /// never retries: a partial stream leaves the module mid-transfer and a retried command packet
    /// would be consumed as pixel data. Callers recover by repainting.
    /// </summary>
    Task<CfaPacket> SendStreamingCommandAsync(
        byte command,
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);

    Task CloseAsync();
}

public sealed class SerialCfaTransport(ILogger<SerialCfaTransport> logger) : ICfaTransport
{
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly CfaPacketParser _parser = new();
    private SerialPort? _port;
    private Task? _readerTask;
    private CancellationTokenSource? _readerCancellation;
    private PendingCommand? _pending;
    private bool _closing;
    private volatile bool _faulted;

    public bool IsOpen => _port?.IsOpen == true && !_closing && !_faulted;
    public event Action<CfaPacket>? ReportReceived;
    public event Action<Exception?>? ConnectionLost;

    public Task OpenAsync(string portName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOpen)
        {
            throw new InvalidOperationException("CFA transport is already open.");
        }

        _closing = false;
        _faulted = false;
        _parser.Reset();
        SerialPort port = new(portName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        port.Open();
        _port = port;
        _readerCancellation = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadLoopAsync(port, _readerCancellation.Token), CancellationToken.None);
        logger.LogInformation("Opened CFA835 transport on {Port}", portName);
        return Task.CompletedTask;
    }

    public async Task<CfaPacket> SendCommandAsync(byte command, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen || _port is null)
            {
                throw new IOException("CFA835 serial port is not open.");
            }

            Exception? lastError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TaskCompletionSource<CfaPacket> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_pendingLock)
                {
                    _pending = new PendingCommand((byte)(command & 0x3F), completion);
                }

                try
                {
                    byte[] encoded = new CfaPacket((byte)(command & 0x3F), data.ToArray()).Encode();
                    await _port.BaseStream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                    await _port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    CfaPacket response = await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
                    if (response.PacketClass == 0xC0)
                    {
                        throw new CfaCommandException(command, response);
                    }

                    return response;
                }
                catch (TimeoutException exception)
                {
                    lastError = exception;
                    logger.LogWarning("CFA command 0x{Command:X2} timed out on attempt {Attempt}/3", command, attempt);
                }
                finally
                {
                    lock (_pendingLock)
                    {
                        if (_pending?.Completion == completion)
                        {
                            _pending = null;
                        }
                    }
                }
            }

            throw new TimeoutException($"CFA command 0x{command:X2} failed after three attempts.", lastError);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<CfaPacket> SendStreamingCommandAsync(
        byte command,
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen || _port is null)
            {
                throw new IOException("CFA835 serial port is not open.");
            }

            TaskCompletionSource<CfaPacket> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock)
            {
                _pending = new PendingCommand((byte)(command & 0x3F), completion);
            }

            try
            {
                byte[] encoded = new CfaPacket((byte)(command & 0x3F), header.ToArray()).Encode();
                await _port.BaseStream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await _port.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await _port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // The datasheet gives the host 500 ms (USB) to deliver the raw stream and the module
                // only acknowledges afterwards, so the ceiling scales with payload size instead of
                // using the fixed 750 ms budget that packetized commands get.
                TimeSpan timeout = TimeSpan.FromMilliseconds(1000 + (payload.Length / 4));
                CfaPacket response = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (response.PacketClass == 0xC0)
                {
                    throw new CfaCommandException(command, response);
                }

                return response;
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning("CFA streaming command 0x{Command:X2} ({Bytes} bytes) timed out", command, payload.Length);
                throw new TimeoutException(
                    $"CFA streaming command 0x{command:X2} did not acknowledge {payload.Length} bytes.", exception);
            }
            finally
            {
                lock (_pendingLock)
                {
                    if (_pending?.Completion == completion)
                    {
                        _pending = null;
                    }
                }
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[512];
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await port.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("CFA835 serial stream closed.");
                }

                foreach (CfaPacket packet in _parser.Feed(buffer.AsSpan(0, read)))
                {
                    DispatchPacket(packet);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            logger.LogWarning(exception, "CFA835 receive loop stopped");
        }
        finally
        {
            if (!_closing)
            {
                _faulted = true;
                ConnectionLost?.Invoke(failure);
            }
        }
    }

    private void DispatchPacket(CfaPacket packet)
    {
        if (packet.PacketClass == 0x80)
        {
            ReportReceived?.Invoke(packet);
            return;
        }

        PendingCommand? pending;
        lock (_pendingLock)
        {
            pending = _pending;
        }

        if (pending is not null && packet.CommandCode == pending.Command &&
            (packet.PacketClass == 0x40 || packet.PacketClass == 0xC0))
        {
            pending.Completion.TrySetResult(packet);
        }
        else
        {
            logger.LogDebug("Ignoring unmatched CFA packet type 0x{Type:X2}", packet.Type);
        }
    }

    public async Task CloseAsync()
    {
        _closing = true;
        _readerCancellation?.Cancel();
        try
        {
            _port?.Close();
        }
        catch (IOException)
        {
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }
        }

        lock (_pendingLock)
        {
            _pending?.Completion.TrySetException(new IOException("CFA835 transport closed."));
            _pending = null;
        }

        _port?.Dispose();
        _port = null;
        _readerCancellation?.Dispose();
        _readerCancellation = null;
        _readerTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _commandGate.Dispose();
    }

    private sealed record PendingCommand(byte Command, TaskCompletionSource<CfaPacket> Completion);
}
