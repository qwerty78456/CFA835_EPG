using System.Text;
using Microsoft.Extensions.Logging;

namespace Cfa835SystemMonitor.Tests;

/// <summary>
/// In-memory CFA835 transport. Records every command and every streamed image, and keeps one
/// combined <see cref="Log"/> in send order so tests can assert on the exact wire sequence of a
/// display transition (e.g. that a clear precedes the first graphic frame).
/// </summary>
internal sealed class FakeTransport : ICfaTransport
{
    public List<(byte Command, byte[] Data)> Commands { get; } = [];
    public List<(byte Command, byte[] Header, byte[] Payload)> Streams { get; } = [];

    /// <summary>Every command and stream in the order it was sent: "cmd:0x06", "stream:0x28".</summary>
    public List<string> Log { get; } = [];

    public bool IsOpen { get; private set; }
    public event Action<CfaPacket>? ReportReceived;
    public event Action<Exception?>? ConnectionLost;

    public Task OpenAsync(string portName, CancellationToken cancellationToken)
    {
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task<CfaPacket> SendCommandAsync(byte command, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        byte[] payload = data.ToArray();
        Commands.Add((command, payload));
        Log.Add(payload.Length > 0 ? $"cmd:0x{command:X2}/{payload[0]}" : $"cmd:0x{command:X2}");
        byte[] response = command switch
        {
            0x01 => "CFA835:h2.0,f1.7"u8.ToArray(),
            // Command 40 subcommand 5 read form echoes the subcommand plus the sampled shade.
            0x28 when payload.Length > 0 && payload[0] == 5 => [5, 0x40],
            _ => []
        };

        return Task.FromResult(new CfaPacket((byte)(0x40 | command), response));
    }

    public Task<CfaPacket> SendStreamingCommandAsync(
        byte command,
        ReadOnlyMemory<byte> header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        Streams.Add((command, header.ToArray(), payload.ToArray()));
        Log.Add($"stream:0x{command:X2}");
        byte[] response = header.Length > 0 ? [header.Span[0]] : [];
        return Task.FromResult(new CfaPacket((byte)(0x40 | command), response));
    }

    public Task CloseAsync()
    {
        IsOpen = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    /// <summary>Raises a key report as the serial reader thread would.</summary>
    public void RaiseReport(CfaPacket packet) => ReportReceived?.Invoke(packet);

    /// <summary>Simulates the receive loop dying.</summary>
    public void RaiseConnectionLost(Exception? failure) => ConnectionLost?.Invoke(failure);
}

/// <summary>
/// Deterministic stand-in for <see cref="GdiGlyphSource"/>: every non-space character is a solid
/// block, which keeps composition assertions exact and keeps GDI+ out of the test run.
/// </summary>
internal sealed class BlockGlyphSource : IGlyphSource
{
    public int CellHeight(int sizePx) => sizePx;

    public int InkTop(int sizePx) => 1;

    public int InkHeight(int sizePx) => sizePx - 2;

    public Glyph Get(int sizePx, char character)
    {
        int width = Math.Max(1, sizePx / 2);
        byte[] coverage = new byte[width * sizePx];
        if (character != ' ')
        {
            for (int row = InkTop(sizePx); row < InkTop(sizePx) + InkHeight(sizePx); row++)
            {
                for (int column = 0; column < width; column++)
                {
                    coverage[(row * width) + column] = 255;
                }
            }
        }

        return new Glyph(width, sizePx, coverage);
    }
}

internal sealed class FakeShutdownExecutor : IShutdownExecutor
{
    public List<int> Requested { get; } = [];
    public int AbortCount { get; private set; }
    public bool AbortSucceeds { get; init; } = true;
    public void RequestShutdown(int seconds) => Requested.Add(seconds);
    public bool TryAbort()
    {
        AbortCount++;
        return AbortSucceeds;
    }
}

/// <summary>Captures log entries so tests can assert that an operator-facing message appears exactly once.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly object _sync = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    public IEnumerable<string> At(LogLevel level) => Entries.Where(entry => entry.Level == level).Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        if (exception is not null)
        {
            message += " | " + exception.Message;
        }

        lock (_sync)
        {
            _entries.Add((logLevel, message));
        }
    }
}

/// <summary>
/// One control connection: reads a scripted request and records the response, or fails every write
/// the way a named pipe does once its client has gone.
/// </summary>
internal sealed class ScriptedConnection(string request) : Stream
{
    private readonly MemoryStream _input = new(Encoding.UTF8.GetBytes(request));
    private readonly MemoryStream _output = new();

    public bool FailWrites { get; init; }
    public bool Disposed { get; private set; }
    public string Response => Encoding.UTF8.GetString(_output.ToArray());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_input.Read(buffer.Span));

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (FailWrites)
        {
            throw new IOException("Pipe is broken.");
        }

        _output.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>Process checks for the handover endpoint, with the requester's exit under test control.</summary>
internal sealed class FakeHandoverProcesses(InstanceDescriptor owner) : IHandoverProcesses
{
    public string? VerificationError { get; init; }
    public TaskCompletionSource<bool> RequesterExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<int> WaitedFor { get; } = [];

    public InstanceDescriptor DescribeOwner() => owner;

    public bool TryVerifyRequester(InstanceDescriptor requester, out string? error)
    {
        error = VerificationError;
        return error is null;
    }

    public Task<bool> WaitForExitAsync(InstanceDescriptor process, CancellationToken cancellationToken)
    {
        WaitedFor.Add(process.Pid);
        return RequesterExit.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>Hands out queued connections, then either fails or waits for cancellation.</summary>
internal sealed class FakeControlListener(params Stream[] connections) : IControlListener
{
    private readonly Queue<Stream> _connections = new(connections);

    public Exception? FailWhenEmpty { get; init; }
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }

    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        if (_connections.TryDequeue(out Stream? connection))
        {
            return connection;
        }

        if (FailWhenEmpty is not null)
        {
            throw FailWhenEmpty;
        }

        Drained.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    public void Dispose() => Disposed = true;
}
