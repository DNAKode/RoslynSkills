using System.Buffers;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RoslynSkills.Contracts;

namespace RoslynSkills.Cli;

public sealed record WorkspaceHostClientOptions(
    string Transport,
    string? PipeName = null,
    string? SocketPath = null)
{
    public static WorkspaceHostClientOptions NamedPipe(string pipeName)
        => new("named-pipe", PipeName: pipeName);

    public static WorkspaceHostClientOptions UnixSocket(string socketPath)
        => new("unix-socket", SocketPath: socketPath);
}

public sealed class WorkspaceHostClient : IAsyncDisposable
{
    private const int StreamReadBufferSize = 65536;
    private static readonly JsonSerializerOptions JsonOptions = ContractsJsonContext.CreateDefaultOptions();

    private readonly Stream? _stream;
    private readonly byte[]? _readBuffer;
    private int _readStart;
    private int _readEnd;
    private readonly ArrayBufferWriter<byte>? _lineScratch;
    private readonly ArrayBufferWriter<byte>? _writeBuffer;

    private readonly TextReader? _reader;
    private readonly TextWriter? _writer;
    private readonly IAsyncDisposable? _asyncDisposable;
    private readonly IDisposable? _disposable;

    private WorkspaceHostClient(
        Stream stream,
        IAsyncDisposable? asyncDisposable,
        IDisposable? disposable)
    {
        _stream = stream;
        _readBuffer = new byte[StreamReadBufferSize];
        _lineScratch = new ArrayBufferWriter<byte>(4096);
        _writeBuffer = new ArrayBufferWriter<byte>(4096);
        _asyncDisposable = asyncDisposable;
        _disposable = disposable;
    }

    private WorkspaceHostClient(
        TextReader reader,
        TextWriter writer,
        IAsyncDisposable? asyncDisposable,
        IDisposable? disposable)
    {
        _reader = reader;
        _writer = writer;
        _asyncDisposable = asyncDisposable;
        _disposable = disposable;
    }

    public static Task<WorkspaceHostClient> ConnectAsync(
        WorkspaceHostClientOptions options,
        CancellationToken cancellationToken)
        => ConnectAsync(options, connectTimeout: null, cancellationToken);

    public static async Task<WorkspaceHostClient> ConnectAsync(
        WorkspaceHostClientOptions options,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
        => await ConnectAsync(options, (TimeSpan?)connectTimeout, cancellationToken).ConfigureAwait(false);

    private static async Task<WorkspaceHostClient> ConnectAsync(
        WorkspaceHostClientOptions options,
        TimeSpan? connectTimeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource? connectCts = connectTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (connectCts is not null)
        {
            connectCts.CancelAfter(connectTimeout!.Value);
        }

        CancellationToken effectiveToken = connectCts?.Token ?? cancellationToken;
        try
        {
            return NormalizeTransport(options.Transport) switch
            {
                "named-pipe" => await ConnectNamedPipeAsync(options.PipeName, effectiveToken).ConfigureAwait(false),
                "unix-socket" => await ConnectUnixSocketAsync(options.SocketPath, effectiveToken).ConfigureAwait(false),
                _ => throw new ArgumentException(
                    $"Unsupported workspace host transport '{options.Transport}'. Supported transports: named-pipe, unix-socket.",
                    nameof(options)),
            };
        }
        catch (OperationCanceledException) when (connectCts is not null && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {(int)Math.Ceiling(connectTimeout!.Value.TotalMilliseconds)} ms connecting to workspace host ({options.Transport}).");
        }
    }

    public static WorkspaceHostClient CreateForText(
        TextReader reader,
        TextWriter writer)
    {
        return new WorkspaceHostClient(reader, writer, asyncDisposable: null, disposable: null);
    }

    public async Task<WorkspaceHostResponse> SendAsync(
        WorkspaceHostRequest request,
        CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return await SendOverUtf8StreamAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string requestJson = JsonSerializer.Serialize(request, JsonOptions);
        await _writer!.WriteLineAsync(requestJson).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        string? responseJson = await _reader!
            .ReadLineAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new IOException("Workspace host closed the connection before returning a response.");
        }

        WorkspaceHostResponse? response = JsonSerializer.Deserialize<WorkspaceHostResponse>(responseJson, JsonOptions);
        return response ?? throw new JsonException("Workspace host returned an empty response payload.");
    }

    private async Task<WorkspaceHostResponse> SendOverUtf8StreamAsync(
        WorkspaceHostRequest request,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> writeBuffer = _writeBuffer!;
        writeBuffer.ResetWrittenCount();

        using (Utf8JsonWriter jsonWriter = new(writeBuffer))
        {
            JsonSerializer.Serialize(jsonWriter, request, JsonOptions);
        }

        Span<byte> newline = writeBuffer.GetSpan(1);
        newline[0] = (byte)'\n';
        writeBuffer.Advance(1);

        await _stream!.WriteAsync(writeBuffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> responseLine = await ReadUtf8LineAsync(cancellationToken).ConfigureAwait(false);
        if (IsNullOrAsciiWhitespace(responseLine.Span))
        {
            throw new IOException("Workspace host closed the connection before returning a response.");
        }

        WorkspaceHostResponse? response = JsonSerializer.Deserialize<WorkspaceHostResponse>(responseLine.Span, JsonOptions);
        return response ?? throw new JsonException("Workspace host returned an empty response payload.");
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadUtf8LineAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = _readBuffer!;
        ArrayBufferWriter<byte> scratch = _lineScratch!;
        scratch.ResetWrittenCount();
        bool usingScratch = false;

        while (true)
        {
            if (_readStart < _readEnd)
            {
                ReadOnlySpan<byte> available = buffer.AsSpan(_readStart, _readEnd - _readStart);
                int nl = available.IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    int lineLen = nl > 0 && available[nl - 1] == (byte)'\r' ? nl - 1 : nl;
                    int lineStart = _readStart;
                    _readStart += nl + 1;

                    if (!usingScratch)
                    {
                        return buffer.AsMemory(lineStart, lineLen);
                    }

                    if (lineLen > 0)
                    {
                        available[..lineLen].CopyTo(scratch.GetSpan(lineLen));
                        scratch.Advance(lineLen);
                    }

                    return TrimTrailingCarriageReturn(scratch.WrittenMemory);
                }

                // No newline in current chunk; append to scratch and read more.
                usingScratch = true;
                available.CopyTo(scratch.GetSpan(available.Length));
                scratch.Advance(available.Length);
                _readStart = 0;
                _readEnd = 0;
            }

            _readStart = 0;
            _readEnd = await _stream!.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (_readEnd == 0)
            {
                return usingScratch
                    ? TrimTrailingCarriageReturn(scratch.WrittenMemory)
                    : ReadOnlyMemory<byte>.Empty;
            }
        }
    }

    private static ReadOnlyMemory<byte> TrimTrailingCarriageReturn(ReadOnlyMemory<byte> memory)
        => memory.Length > 0 && memory.Span[^1] == (byte)'\r'
            ? memory[..^1]
            : memory;

    private static bool IsNullOrAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_asyncDisposable is not null)
        {
            await _asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }

        _disposable?.Dispose();
    }

    private static async Task<WorkspaceHostClient> ConnectNamedPipeAsync(
        string? pipeName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Named-pipe workspace host connections require a pipe name.", nameof(pipeName));
        }

        NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new WorkspaceHostClient(pipe, asyncDisposable: pipe, disposable: null);
    }

    private static async Task<WorkspaceHostClient> ConnectUnixSocketAsync(
        string? socketPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new ArgumentException("Unix-socket workspace host connections require a socket path.", nameof(socketPath));
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        NetworkStream stream = new(socket, ownsSocket: true);
        return new WorkspaceHostClient(stream, asyncDisposable: stream, disposable: socket);
    }

    private static string NormalizeTransport(string transport)
        => transport.ToLowerInvariant() switch
        {
            "pipe" => "named-pipe",
            "named-pipe" => "named-pipe",
            "unix" => "unix-socket",
            "unix-socket" => "unix-socket",
            _ => transport,
        };
}
