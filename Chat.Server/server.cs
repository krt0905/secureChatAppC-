
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chat.Protocol;
using Chat.Server;
using Chat.Server.Auth;
using Npgsql;
using System.Collections.Generic;
using System.Linq;

const int port = 5000;

string dbConnection =
    Environment.GetEnvironmentVariable("CHATAPP_DB")
    ?? throw new InvalidOperationException(
        "Missing CHATAPP_DB environment variable.");

string pfxPath =
    Environment.GetEnvironmentVariable("CHATAPP_PFX_PATH")
    ?? throw new InvalidOperationException(
        "Missing CHATAPP_PFX_PATH environment variable.");

string pfxPassword =
    Environment.GetEnvironmentVariable("CHATAPP_PFX_PASSWORD")
    ?? throw new InvalidOperationException(
        "Missing CHATAPP_PFX_PASSWORD environment variable.");

using var certificate = new X509Certificate2(
    pfxPath,
    pfxPassword,
    X509KeyStorageFlags.UserKeySet);



var accounts = new AccountRepository(dbConnection);
var messageRepository = new MessageRepository(dbConnection);
var auth = new AuthenticationService(accounts, new PasswordHasher());
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
Console.WriteLine($"[SERVER] Listening on port {port} (TLS required)");

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

        _ = HandleClientAsync(
            client, certificate, auth, hub, messageRepository, shutdown.Token);
    }
}
catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
{
}
finally
{
    listener.Stop();
    await accounts.DisposeAsync();
}

Console.WriteLine("[SERVER] Stopped.");

static async Task HandleClientAsync(
    TcpClient client,
    X509Certificate2 certificate,
    AuthenticationService auth,
    ChatHub hub,
    MessageRepository messageRepository,
    CancellationToken serverToken)
{
    string endpoint =
        client.Client.RemoteEndPoint?.ToString() ?? "unknown";

    using var connectionCts =
        CancellationTokenSource.CreateLinkedTokenSource(serverToken);

    ClientSession? session = null;
    SslStream? tlsStream = null;
    Task? sendTask = null;

    Console.WriteLine($"[CONNECT] {endpoint}");

    try
    {
        tlsStream = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false);

        await tlsStream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols =
                    SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false
            },
            connectionCts.Token);

        // Chỉ tạo session sau khi TLS handshake thành công.
        session = new ClientSession(client, tlsStream);
        tlsStream = null; // session hiện quản lý stream này.

        sendTask = session.SendLoopAsync(connectionCts.Token);

        while (!connectionCts.IsCancellationRequested)
        {
            ChatMessage? message = await MessageFraming.ReadAsync(
                session.Stream, connectionCts.Token);

            if (message is null)
                break;

            if (message.Type is "auth.register" or "auth.login")
            {
                if (session.IsAuthenticated)
                {
                    SendAuthResult(
                        session, message.RequestId,
                        false, "already_authenticated",
                        "This connection is already authenticated.");
                    continue;
                }

                if (!TryReadCredentials(
                    message.Payload,
                    out string username,
                    out string password))
                {
                    SendAuthResult(
                        session, message.RequestId,
                        false, "invalid_request",
                        "Invalid authentication request.");
                    continue;
                }

                if (message.Type == "auth.register")
                {
                    AuthResult result = await auth.RegisterAsync(
                        new RegisterRequest(username, password),
                        connectionCts.Token);

                    SendAuthResult(
                        session, message.RequestId,
                        result.Success, result.Code, result.Message,
                        result.Username);

                    continue;
                }

                var login = await auth.LoginAsync(
                    new LoginRequest(username, password),
                    connectionCts.Token);

                if (!login.Result.Success ||
                    login.CanonicalUsername is null)
                {
                    SendAuthResult(
                        session, message.RequestId,
                        false, "invalid_credentials",
                        "Invalid username or password.");
                    continue;
                }

                string canonicalUsername = login.CanonicalUsername;

                if (!session.TrySetAuthenticated(canonicalUsername))
                {
                    SendAuthResult(
                        session, message.RequestId,
                        false, "authentication_failed",
                        "Unable to authenticate this connection.");
                    continue;
                }

                if (!hub.TryJoin(session, canonicalUsername))
                {
                    session.ClearAuthenticated();

                    SendAuthResult(
                        session, message.RequestId,
                        false, "already_online",
                        "This account is already connected.");
                    continue;
                }

                // Username đến từ bản ghi PostgreSQL đã xác thực.
                session.Username = canonicalUsername;

                SendAuthResult(
                    session, message.RequestId,
                    true, "authenticated", "Login successful.",
                    canonicalUsername);

                Console.WriteLine(
                    $"[ONLINE] {canonicalUsername} ({endpoint})");

                session.TrySend(CreateMessage(
                    "presence.list", "",
                    new { users = hub.GetOnlineUsers() }));

                hub.Broadcast(CreateMessage(
                    "presence.changed", "",
                    new
                    {
                        username = canonicalUsername,
                        online = true
                    }));

                continue;
            }

            // Bỏ hoàn toàn cơ chế cũ cho phép client tự khai username.
            /* if (message.Type == "chat.join")
             {
                 SendError(
                     session, message.RequestId,
                     "chat.join is no longer supported. Please log in.");
                 continue;
             }*/


            else if (message.Type == "history.get")
            {
                if (!session.IsAuthenticated)
                {
                    SendError(
                        session,
                        message.RequestId,
                        "Authentication required.");
                    continue;
                }

                int limit = 50;


                if (message.Payload.TryGetProperty(
                        "limit", out JsonElement limitElement))
                {
                    if (limitElement.ValueKind != JsonValueKind.Number ||
                        !limitElement.TryGetInt32(out limit) ||
                        limit < 1)
                    {
                        SendError(
                            session,
                            message.RequestId,
                            "Limit must be a positive integer.");
                        continue;
                    }
                }

                // Không cho client yêu cầu quá nhiều bản ghi.
                limit = Math.Min(limit, 50);

                long? beforeId = null;

                if (message.Payload.TryGetProperty(
                        "beforeId", out JsonElement beforeElement) &&
                    beforeElement.ValueKind != JsonValueKind.Null)
                {
                    if (beforeElement.ValueKind != JsonValueKind.Number ||
                        !beforeElement.TryGetInt64(out long parsedBeforeId) ||
                        parsedBeforeId <= 0)
                    {
                        SendError(
                            session,
                            message.RequestId,
                            "beforeId must be a positive integer.");
                        continue;
                    }

                    beforeId = parsedBeforeId;
                }

                try
                {
                    List<SavedMessage> history =
                        await messageRepository.GetHistoryAsync(
                            beforeId,
                            limit,
                            connectionCts.Token);

                    bool hasMore = history.Count > limit;

                    // Nếu có bản ghi dư, bỏ bản cũ nhất trong kết quả
                    // để trang chỉ chứa đúng số lượng client yêu cầu.
                    if (hasMore)
                        history.RemoveAt(0);

                    var payload = new
                    {
                        messages = history.Select(item => new
                        {
                            messageId = item.Id,
                            sender = item.Sender,
                            text = item.Text,
                            sentAtUtc = item.CreatedAt
                        }).ToArray(),
                        hasMore
                    };

                    if (!session.TrySend(CreateMessage(
                            "history.result",
                            message.RequestId,
                            payload)))
                    {
                        Console.WriteLine(
                            "[WARN] Could not queue history response.");
                    }
                }

                catch (NpgsqlException ex)
                {
                    Console.WriteLine(
                        $"[DB ERROR] Loading chat history failed: {ex.GetType().Name}");

                    SendError(
                        session,
                        message.RequestId,
                        "Could not load chat history. Please try again.");
                }
            }


            if (!session.IsAuthenticated)
            {
                SendError(
                    session, message.RequestId,
                    "Authentication required.");
                continue;
            }

            if (message.Type == "chat.send")
            {
                if (!message.Payload.TryGetProperty(
                    "text", out JsonElement textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                {
                    SendError(
                        session, message.RequestId,
                        "Text is required.");
                    continue;
                }

                string text = textElement.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(text) ||
                    text.Length > 4000)
                {
                    SendError(
                        session, message.RequestId,
                        "Message must contain 1 to 4000 characters.");
                    continue;
                }

                string sender = session.AuthenticatedUsername!;

                SavedMessage? savedMessage;

                try
                {
                    savedMessage = await messageRepository.SaveAsync(
                        sender,
                        text,
                        connectionCts.Token);
                }
                catch (NpgsqlException ex)
                {
                    // Không ghi nội dung tin nhắn hoặc thông tin bí mật vào log.
                    Console.WriteLine(
                        $"[DB ERROR] Saving chat message failed: {ex.GetType().Name}");

                    SendError(
                        session,
                        message.RequestId,
                        "Could not save message. Please try again.");

                    continue;
                }

                if (savedMessage is null)
                {
                    SendError(
                        session,
                        message.RequestId,
                        "Could not find the sender account.");

                    continue;
                }

                // Chỉ broadcast sau khi PostgreSQL đã lưu thành công.
                hub.Broadcast(CreateMessage(
                    "chat.receive",
                    message.RequestId,
                    new
                    {
                        messageId = savedMessage.Id,
                        sender = savedMessage.Sender,
                        text = savedMessage.Text,
                        sentAtUtc = savedMessage.CreatedAt
                    }));

                Console.WriteLine(
                    $"[CHAT] Saved message {savedMessage.Id} from {sender}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Server dừng hoặc kết nối bị hủy.
    }
    catch (AuthenticationException ex)
    {
        Console.WriteLine(
            $"[TLS ERROR] {client.Client.RemoteEndPoint}");

        Console.WriteLine(ex.ToString());
    }
    catch (InvalidDataException ex)
    {
        Console.WriteLine(
            $"[PROTOCOL ERROR] {endpoint}: {ex.Message}");
    }
    catch (JsonException)
    {
        Console.WriteLine($"[JSON ERROR] Invalid JSON from {endpoint}");
    }
    catch (IOException)
    {
        Console.WriteLine($"[IO] Connection ended: {endpoint}");
    }
    catch (SocketException)
    {
        Console.WriteLine($"[SOCKET] Connection ended: {endpoint}");
    }
    catch (ObjectDisposedException)
    {
        // Kết nối đã được đóng.
    }
    /*  catch (Exception ex)
      {
          // Không log payload hoặc mật khẩu.
          Console.WriteLine(
              $"[CLIENT ERROR] {endpoint}: {ex.GetType().Name}");
      }*/


    catch (Exception ex)
    {
        Console.WriteLine(
            $"[CLIENT ERROR] {endpoint}: {ex}");
    }

    finally
    {
        if (session is not null)
        {
            string? username = session.AuthenticatedUsername;

            if (username is not null && hub.Leave(session))
            {
                Console.WriteLine($"[OFFLINE] {username}");

                hub.Broadcast(CreateMessage(
                    "presence.changed", "",
                    new { username, online = false }));
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
        }
        else
        {
            // TLS handshake thất bại trước khi tạo session.
            tlsStream?.Dispose();
            client.Dispose();
        }

        Console.WriteLine($"[DISCONNECT] {endpoint}");
    }
}

static bool TryReadCredentials(
    JsonElement payload,
    out string username,
    out string password)
{
    username = "";
    password = "";

    if (!payload.TryGetProperty(
            "username", out JsonElement userElement) ||
        userElement.ValueKind != JsonValueKind.String ||
        !payload.TryGetProperty(
            "password", out JsonElement passElement) ||
        passElement.ValueKind != JsonValueKind.String)
    {
        return false;
    }

    username = userElement.GetString() ?? "";
    password = passElement.GetString() ?? "";

    return username.Length > 0 && password.Length > 0;
}

static void SendAuthResult(
    ClientSession session,
    string requestId,
    bool success,
    string code,
    string message,
    string? username = null)
{
    session.TrySend(CreateMessage(
        "auth.result",
        requestId,
        new { success, code, message, username }));
}

static void SendError(
    ClientSession session,
    string requestId,
    string message)
{
    session.TrySend(CreateMessage(
        "error", requestId, new { message }));
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
