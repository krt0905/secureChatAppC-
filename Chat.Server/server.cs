
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Chat.Protocol;
using Chat.Server;

const int port = 5000;

var hub = new ChatHub();
using var shutdown = new CancellationTokenSource();
using var listener = new TcpListener(IPAddress.Any, port);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
    listener.Stop();
};

listener.Start();

Console.WriteLine($"[SERVER] Listening on port {port}");

try
{
    while (!shutdown.IsCancellationRequested)
    {
        TcpClient client;

        try
        {
            client = await listener.AcceptTcpClientAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (SocketException) when (shutdown.IsCancellationRequested)
        {
            break;
        }

        _ = HandleClientAsync(client, hub, shutdown.Token);
    }
}
catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
{
}

Console.WriteLine("[SERVER] Stopped.");

static async Task HandleClientAsync(
    TcpClient client,
    ChatHub hub,
    CancellationToken serverToken)
{
    string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    using var session = new ClientSession(client);
    using var connectionCts =
        CancellationTokenSource.CreateLinkedTokenSource(serverToken);

    bool registered = false;
    Task? sendTask = null;

    Console.WriteLine($"[CONNECT] {endpoint}");

    try
    {
        sendTask = session.SendLoopAsync(connectionCts.Token);

        while (!connectionCts.IsCancellationRequested)
        {
            ChatMessage? message = await MessageFraming.ReadAsync(
                session.Stream, connectionCts.Token);

            if (message is null)
                break;

            if (message.Type == "chat.join")
            {
                if (registered)
                {
                    SendError(session, message.RequestId,
                        "You have already joined.");
                    continue;
                }

                if (!message.Payload.TryGetProperty(
                    "username", out JsonElement usernameElement) ||
                    usernameElement.ValueKind != JsonValueKind.String)
                {
                    SendError(session, message.RequestId,
                        "Username is required.");
                    continue;
                }

                string username = usernameElement.GetString()?.Trim() ?? "";

                if (username.Length is < 1 or > 24)
                {
                    SendError(session, message.RequestId,
                        "Username must contain 1 to 24 characters.");
                    continue;
                }

                if (!hub.TryJoin(session, username))
                {
                    SendError(session, message.RequestId,
                        "That username is already in use.");
                    continue;
                }

                registered = true;

                Console.WriteLine($"[ONLINE] {username} ({endpoint})");

                session.TrySend(CreateMessage(
                    "chat.joined",
                    message.RequestId,
                    new { username }));

                session.TrySend(CreateMessage(
                    "presence.list",
                    "",
                    new { users = hub.GetOnlineUsers() }));

                hub.Broadcast(CreateMessage(
                    "presence.changed",
                    "",
                    new { username, online = true }));

                continue;
            }

            if (!registered)
            {
                SendError(session, message.RequestId,
                    "Join the chat before sending messages.");
                continue;
            }

            if (message.Type == "chat.send")
            {
                if (!message.Payload.TryGetProperty(
                    "text", out JsonElement textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                {
                    SendError(session, message.RequestId,
                        "Text is required.");
                    continue;
                }

                string text = textElement.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
                {
                    SendError(session, message.RequestId,
                        "Message must contain 1 to 4000 characters.");
                    continue;
                }

                hub.Broadcast(CreateMessage(
                    "chat.receive",
                    message.RequestId,
                    new
                    {
                        sender = session.Username,
                        text,
                        sentAtUtc = DateTimeOffset.UtcNow
                    }));

                Console.WriteLine($"[CHAT] {session.Username}: {text}");
            }
            else
            {
                SendError(session, message.RequestId,
                    "Unsupported message type.");
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Server đang dừng hoặc kết nối bị hủy.
    }
    catch (InvalidDataException ex)
    {
        Console.WriteLine($"[PROTOCOL ERROR] {endpoint}: {ex.Message}");
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"[JSON ERROR] {endpoint}: {ex.Message}");
    }
    catch (IOException ex)
    {
        Console.WriteLine($"[IO ERROR] {endpoint}: {ex.Message}");
    }
    catch (SocketException ex)
    {
        Console.WriteLine($"[SOCKET ERROR] {endpoint}: {ex.Message}");
    }
    catch (ObjectDisposedException)
    {
        // Kết nối đã được đóng.
    }
    finally
    {
        if (registered && hub.Leave(session))
        {
            Console.WriteLine($"[OFFLINE] {session.Username}");

            hub.Broadcast(CreateMessage(
                "presence.changed",
                "",
                new
                {
                    username = session.Username,
                    online = false
                }));
        }

        connectionCts.Cancel();
        session.CompleteSending();
        session.Dispose();

        if (sendTask is not null)
        {
            try
            {
                await sendTask;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        Console.WriteLine($"[DISCONNECT] {endpoint}");
    }
}

static void SendError(
    ClientSession session,
    string requestId,
    string text)
{
    session.TrySend(CreateMessage(
        "error", requestId, new { message = text }));
}

static ChatMessage CreateMessage<T>(
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
