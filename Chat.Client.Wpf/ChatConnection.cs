
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Chat.Protocol;

namespace Chat.Client.Wpf;

public sealed record AuthResponse(
    bool Success,
    string Code,
    string Message,
    string? Username);

public sealed class ChatConnection : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ConcurrentDictionary<
        string,
        TaskCompletionSource<AuthResponse>> _pendingAuth = new();

    private SslStream? _tlsStream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    public event Action<ChatMessage>? MessageReceived;
    public event Action? Disconnected;

    public bool IsConnected =>
        _tlsStream is not null && _client.Connected;

    public async Task ConnectAsync(
        string host,
        int port = 5000,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _client.ConnectAsync(host, port, cancellationToken);

        var tls = new SslStream(
            _client.GetStream(),
            leaveInnerStreamOpen: false);

        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols =
                        SslProtocols.Tls12 | SslProtocols.Tls13
                },
                cancellationToken);

            _tlsStream = tls;
            _receiveCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            // Chỉ task này đọc dữ liệu từ server.
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
        }
        catch
        {
            tls.Dispose();
            _client.Dispose();
            throw;
        }
    }

    public Task<AuthResponse> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        return SendAuthRequestAsync(
            "auth.login", username, password, cancellationToken);
    }

    public Task<AuthResponse> RegisterAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        return SendAuthRequestAsync(
            "auth.register", username, password, cancellationToken);
    }

    private async Task<AuthResponse> SendAuthRequestAsync(
        string type,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");

        var waiter = new TaskCompletionSource<AuthResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingAuth.TryAdd(requestId, waiter))
            throw new InvalidOperationException(
                "Could not track authentication request.");

        try
        {
            await SendAsync(
                CreateMessage(
                    type,
                    requestId,
                    new { username, password }),
                cancellationToken);

            return await waiter.Task.WaitAsync(
                TimeSpan.FromSeconds(15),
                cancellationToken);
        }
        finally
        {
            _pendingAuth.TryRemove(requestId, out _);
        }
    }

    public async Task SendAsync(
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SslStream stream = _tlsStream
            ?? throw new InvalidOperationException(
                "Not connected to the server.");

        // Tránh hai tác vụ ghi đồng thời vào cùng một stream.
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            await MessageFraming.WriteAsync(
                stream, message, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            SslStream stream = _tlsStream!;

            while (!cancellationToken.IsCancellationRequested)
            {
                ChatMessage? message =
                    await MessageFraming.ReadAsync(
                        stream, cancellationToken);

                if (message is null)
                    break;

                if (message.Type == "auth.result")
                {
                    var response = new AuthResponse(
                        ReadBool(message.Payload, "success"),
                        ReadString(message.Payload, "code") ?? "unknown",
                        ReadString(message.Payload, "message")
                            ?? "No message from server.",
                        ReadString(message.Payload, "username"));

                    if (_pendingAuth.TryGetValue(
                            message.RequestId, out var waiter))
                    {
                        waiter.TrySetResult(response);
                    }

                    continue;
                }

                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Đóng kết nối theo yêu cầu.
        }
        catch (IOException)
        {
            // Kết nối bị đóng hoặc gặp lỗi đọc.
        }
        catch (SocketException)
        {
            // Lỗi socket.
        }
        catch (ObjectDisposedException)
        {
            // Stream đã bị đóng.
        }
        finally
        {
            foreach (var entry in _pendingAuth)
            {
                entry.Value.TrySetException(
                    new IOException(
                        "Server disconnected before authentication completed."));
            }

            Disconnected?.Invoke();
        }
    }

    private static ChatMessage CreateMessage<T>(
        string type,
        string requestId,
        T payload)
    {
        using JsonDocument document =
            JsonDocument.Parse(JsonSerializer.Serialize(payload));

        return new ChatMessage
        {
            Type = type,
            RequestId = requestId,
            Payload = document.RootElement.Clone()
        };
    }

    private static string? ReadString(
        JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static bool ReadBool(
        JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.True;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _receiveCts?.Cancel();
        _client.Dispose();
        _tlsStream?.Dispose();

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        _receiveCts?.Dispose();
        _writeLock.Dispose();
    }
}
