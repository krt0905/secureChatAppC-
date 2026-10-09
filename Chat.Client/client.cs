using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using Chat.Protocol;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
const int port = 5000;

// 1. Chọn chế độ xác thực.
Console.WriteLine("=== ChatApp ===");
Console.WriteLine("1. Register");
Console.WriteLine("2. Login");
Console.Write("Choose (1/2): ");

string mode = Console.ReadLine()?.Trim() ?? "";

if (mode is not ("1" or "2"))
{
    Console.WriteLine("[ERROR] Please choose 1 or 2.");
    return;
}

// 2. Nhập thông tin tài khoản.
Console.Write("Username: ");
string username = Console.ReadLine()?.Trim() ?? "";

Console.Write("Password: ");
string password = ReadPassword();

if (string.IsNullOrWhiteSpace(username) ||
    string.IsNullOrEmpty(password))
{
    Console.WriteLine("[ERROR] Username and password are required.");
    return;
}

using var client = new TcpClient();

try
{
    // 3. Kết nối TCP.
    Console.WriteLine($"[CLIENT] Connecting to {host}:{port}...");
    await client.ConnectAsync(host, port);
    Console.WriteLine("[CLIENT] TCP connected.");

    // 4. Thiết lập TLS.
    using var tlsStream = new SslStream(
        client.GetStream(),
        leaveInnerStreamOpen: false);

    await tlsStream.AuthenticateAsClientAsync(
        new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols =
                SslProtocols.Tls12 | SslProtocols.Tls13
        });

    Console.WriteLine("[CLIENT] TLS established.");

    using Stream stream = tlsStream;
    using var cts = new CancellationTokenSource();

    var pendingAuth =
        new ConcurrentDictionary<
            string,
            TaskCompletionSource<AuthResponse>>();

    // Chỉ ReceiveLoopAsync được phép đọc từ stream.
    Task receiveTask = ReceiveLoopAsync(
        stream, pendingAuth, cts.Token);

    try
    {
        // 5. Đăng ký nếu người dùng chọn Register.
        if (mode == "1")
        {
            AuthResponse registration = await SendAuthRequestAsync(
                stream,
                pendingAuth,
                "auth.register",
                username,
                password,
                cts.Token);

            Console.WriteLine(
                registration.Success
                    ? $"[REGISTER] {registration.Message}"
                    : $"[REGISTER ERROR] {registration.Message}");

            if (!registration.Success)
                return;

            Console.WriteLine("[CLIENT] Logging in with the new account...");
        }

        // 6. Đăng nhập.
        // Nếu vừa đăng ký thành công, server vẫn yêu cầu login riêng.
        AuthResponse login = await SendAuthRequestAsync(
            stream,
            pendingAuth,
            "auth.login",
            username,
            password,
            cts.Token);

        if (!login.Success)
        {
            Console.WriteLine(
                $"[LOGIN ERROR] {login.Message} (code: {login.Code})");
            return;
        }

        string authenticatedUsername = login.Username ?? username;

        Console.WriteLine(
            $"[LOGIN] {login.Message} Welcome, {authenticatedUsername}!");

        Console.WriteLine("Type a message and press Enter.");
        Console.WriteLine("Type /quit to disconnect.");

        // 7. Chỉ cho phép chat sau khi xác thực thành công.
        while (!cts.IsCancellationRequested)
        {
            string? line = Console.ReadLine();

            if (line is null ||
                line.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Equals("/history", StringComparison.OrdinalIgnoreCase))
            {
                var historyRequest = new ChatMessage
                {
                    Type = "history.get",
                    RequestId = Guid.NewGuid().ToString("N"),
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        limit = 50
                    })
                };

                await MessageFraming.WriteAsync(
                    tlsStream,
                historyRequest);

                continue;
            }
            if (line.Length > 4000)
            {
                Console.WriteLine(
                    "[ERROR] Message cannot exceed 4000 characters.");
                continue;
            }

            await MessageFraming.WriteAsync(
                stream,
                CreateMessage(
                    "chat.send",
                    Guid.NewGuid().ToString(),
                    new { text = line }),
                cts.Token);
        }
    }
    finally
    {
        cts.Cancel();
        client.Close();

        try
        {
            await receiveTask;
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (JsonException) { }
    }
}
catch (AuthenticationException ex)
{
    Console.WriteLine(
        $"[TLS ERROR] Could not verify server TLS: {ex.Message}");

    Console.WriteLine(
        "[HINT] Check the server certificate, trust chain, " +
        "and whether its hostname matches the host argument.");
}
catch (SocketException ex)
{
    Console.WriteLine($"[CONNECTION ERROR] {ex.Message}");
}
catch (IOException ex)
{
    Console.WriteLine($"[IO ERROR] {ex.Message}");
}
catch (TimeoutException)
{
    Console.WriteLine("[TIMEOUT] Server did not respond to authentication in time.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("[CLIENT] Operation cancelled.");
}
catch (JsonException ex)
{
    Console.WriteLine($"[PROTOCOL ERROR] Invalid JSON: {ex.Message}");
}


// Sends one authentication request and waits for its matching response.
// This method writes to the stream but never reads from it.
static async Task<AuthResponse> SendAuthRequestAsync(
    Stream stream,
    ConcurrentDictionary<
        string,
        TaskCompletionSource<AuthResponse>> pendingAuth,
    string type,
    string username,
    string password,
    CancellationToken cancellationToken)
{
    string requestId = Guid.NewGuid().ToString();

    var waiter = new TaskCompletionSource<AuthResponse>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    if (!pendingAuth.TryAdd(requestId, waiter))
        throw new InvalidOperationException("Could not track authentication request.");

    try
    {
        await MessageFraming.WriteAsync(
            stream,
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
        pendingAuth.TryRemove(requestId, out _);
    }
}


// The only function that reads incoming messages from the server.
static async Task ReceiveLoopAsync(
    Stream stream,
    ConcurrentDictionary<
        string,
        TaskCompletionSource<AuthResponse>> pendingAuth,
    CancellationToken cancellationToken)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ChatMessage? message = await MessageFraming.ReadAsync(
                stream, cancellationToken);

            if (message is null)
            {
                Console.WriteLine("[CLIENT] Server disconnected.");
                break;
            }

            switch (message.Type)
            {
                case "auth.result":
                    {
                        var result = new AuthResponse(
                            Success: ReadBool(message.Payload, "success"),
                            Code: ReadString(message.Payload, "code") ?? "unknown",
                            Message: ReadString(message.Payload, "message")
                                ?? "No message from server.",
                            Username: ReadString(message.Payload, "username"));

                        if (pendingAuth.TryGetValue(
                            message.RequestId, out var waiter))
                        {
                            waiter.TrySetResult(result);
                        }
                        else
                        {
                            Console.WriteLine(
                                "[AUTH] Received an unmatched authentication response.");
                        }

                        break;
                    }

                case "chat.joined":
                    // Hỗ trợ tương thích nếu server gửi message này.
                    Console.WriteLine("[CLIENT] Joined the chat.");
                    break;

                case "chat.receive":
                    {
                        string sender =
                            ReadString(message.Payload, "sender") ?? "Unknown";

                        string text =
                            ReadString(message.Payload, "text") ?? "";

                        Console.WriteLine($"[{sender}] {text}");
                        break;
                    }

                case "chat.system":
                    {
                        string text =
                            ReadString(message.Payload, "text") ?? "";

                        Console.WriteLine($"[SYSTEM] {text}");
                        break;
                    }

                case "error":
                    {
                        string error =
                            ReadString(message.Payload, "message")
                            ?? "Unknown server error.";

                        Console.WriteLine($"[ERROR] {error}");
                        break;
                    }

                case "presence.list":
                    {
                        string[] users = message.Payload
                            .GetProperty("users")
                            .EnumerateArray()
                            .Where(user =>
                                user.ValueKind == JsonValueKind.String)
                            .Select(user => user.GetString() ?? "")
                            .Where(user => user.Length > 0)
                            .ToArray();

                        Console.WriteLine(
                            $"[ONLINE USERS] {string.Join(", ", users)}");

                        break;
                    }

                case "presence.changed":
                    {
                        string changedUsername =
                            ReadString(message.Payload, "username") ?? "Unknown";

                        bool online =
                            ReadBool(message.Payload, "online");

                        Console.WriteLine(
                            online
                                ? $"[PRESENCE] {changedUsername} is online."
                                : $"[PRESENCE] {changedUsername} is offline.");

                        break;
                    }

                default:
                    Console.WriteLine($"[UNKNOWN] {message.Type}");
                    break;
            }
        }
    }
    finally
    {
        // Đánh thức yêu cầu xác thực đang chờ nếu server ngắt kết nối.
        foreach (var entry in pendingAuth)
        {
            entry.Value.TrySetResult(
                new AuthResponse(
                    false,
                    "connection_closed",
                    "Server disconnected before authentication completed.",
                    null));
        }
    }
}


static string? ReadString(
    JsonElement payload,
    string propertyName)
{
    if (!payload.TryGetProperty(
            propertyName, out JsonElement element) ||
        element.ValueKind != JsonValueKind.String)
    {
        return null;
    }

    return element.GetString();
}


static bool ReadBool(
    JsonElement payload,
    string propertyName)
{
    return payload.TryGetProperty(
               propertyName, out JsonElement element) &&
           element.ValueKind is JsonValueKind.True;
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


static string ReadPassword()
{
    var password = new StringBuilder();

    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return password.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
                password.Length--;

            continue;
        }

        if (!char.IsControl(key.KeyChar))
            password.Append(key.KeyChar);
    }
}


sealed record AuthResponse(
    bool Success,
    string Code,
    string Message,
    string? Username);