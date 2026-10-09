
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chat.Protocol;
using Chat.Server;
using Chat.Server.Auth;

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
            client, certificate, auth, hub, shutdown.Token);
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
            if (message.Type == "chat.join")
            {
                SendError(
                    session, message.RequestId,
                    "chat.join is no longer supported. Please log in.");
                continue;
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

                // Danh tính lấy từ session, không từ payload client.
                string sender = session.AuthenticatedUsername!;

                hub.Broadcast(CreateMessage(
                    "chat.receive",
                    message.RequestId,
                    new
                    {
                        sender,
                        text,
                        sentAtUtc = DateTimeOffset.UtcNow
                    }));

                // Tránh ghi nội dung chat nhạy cảm vào log.
                Console.WriteLine($"[CHAT] Message from {sender}");
            }
            else
            {
                SendError(
                    session, message.RequestId,
                    "Unsupported message type.");
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
