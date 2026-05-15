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
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly IAsyncDisposable? _asyncDisposable;
    private readonly IDisposable? _disposable;

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

    public static async Task<WorkspaceHostClient> ConnectAsync(
        WorkspaceHostClientOptions options,
        CancellationToken cancellationToken)
    {
        return NormalizeTransport(options.Transport) switch
        {
            "named-pipe" => await ConnectNamedPipeAsync(options.PipeName, cancellationToken).ConfigureAwait(false),
            "unix-socket" => await ConnectUnixSocketAsync(options.SocketPath, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException(
                $"Unsupported workspace host transport '{options.Transport}'. Supported transports: named-pipe, unix-socket.",
                nameof(options)),
        };
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
        string requestJson = JsonSerializer.Serialize(request, JsonOptions);
        await _writer.WriteLineAsync(requestJson).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        string? responseJson = await _reader
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
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

        StreamReader reader = new(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        StreamWriter writer = new(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };

        return new WorkspaceHostClient(reader, writer, asyncDisposable: pipe, disposable: null);
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
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);

        NetworkStream stream = new(socket, ownsSocket: true);
        StreamReader reader = new(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        StreamWriter writer = new(stream, Utf8NoBom, bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };

        return new WorkspaceHostClient(reader, writer, asyncDisposable: stream, disposable: socket);
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
