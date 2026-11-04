
using System.Net.Sockets;
using System.Text.Json;
using Chat.Protocol;

namespace Chat.Client.Core;

public sealed class ChatClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public bool IsConnected =>
        _tcpClient?.Connected == true && _stream is not null;

    public async Task ConnectAsync(
        string host,
        int port,
        string username,
        CancellationToken cancellationToken = default)
    {
        if (_tcpClient is not null)
            throw new InvalidOperationException(
                "Client is already connected or connecting.");

        var tcpClient = new TcpClient();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        _tcpClient = tcpClient;
        _cts = cts;

        try
        {
            StatusChanged?.Invoke($"Connecting to {host}:{port}...");

            await tcpClient.ConnectAsync(host, port, cts.Token);
            _stream = tcpClient.GetStream();

            await SendAsync(
                CreateMessage(
                    "chat.join",
                    Guid.NewGuid().ToString(),
                    new { username }),
                cts.Token);

            _receiveTask = ReceiveLoopAsync(cts.Token);

            StatusChanged?.Invoke("Connected");
        }
        catch
        {
            tcpClient.Dispose();
            cts.Dispose();

            _tcpClient = null;
            _stream = null;
            _cts = null;

            StatusChanged?.Invoke("Connection failed");
            throw;
        }
    }

    public Task SendChatAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            CreateMessage(
                "chat.send",
                Guid.NewGuid().ToString(),
                new { text }),
            cancellationToken);
    }

    private async Task SendAsync(
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            NetworkStream stream = _stream
                ?? throw new InvalidOperationException(
                    "Not connected to the server.");

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
            NetworkStream stream = _stream
                ?? throw new InvalidOperationException(
                    "Connection stream is unavailable.");

            while (!cancellationToken.IsCancellationRequested)
            {
                ChatMessage? message = await MessageFraming.ReadAsync(
                    stream, cancellationToken);

                if (message is null)
                    break;

                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disconnecting.
        }
        catch (IOException ex)
        {
            StatusChanged?.Invoke($"Network error: {ex.Message}");
        }
        catch (SocketException ex)
        {
            StatusChanged?.Invoke($"Socket error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            StatusChanged?.Invoke($"Invalid JSON: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            StatusChanged?.Invoke($"Protocol error: {ex.Message}");
        }
        finally
        {
            StatusChanged?.Invoke("Disconnected");
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

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? cts = _cts;
        TcpClient? tcpClient = _tcpClient;
        Task? receiveTask = _receiveTask;

        if (cts is null && tcpClient is null)
            return;

        cts?.Cancel();
        tcpClient?.Close();

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        _stream?.Dispose();
        tcpClient?.Dispose();
        cts?.Dispose();

        _stream = null;
        _tcpClient = null;
        _cts = null;
        _receiveTask = null;

        StatusChanged?.Invoke("Disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }
}
