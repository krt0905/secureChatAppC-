
using System.Net.Sockets;
using System.Text.Json;
using Chat.Protocol;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
const int port = 5000;

Console.Write("Username: ");
string username = Console.ReadLine()?.Trim() ?? "";

if (username.Length is < 1 or > 24)
{
    Console.WriteLine("Username must contain 1 to 24 characters.");
    return;
}

using var client = new TcpClient();

try
{
    Console.WriteLine($"[CLIENT] Connecting to {host}:{port}...");
    await client.ConnectAsync(host, port);
    Console.WriteLine("[CLIENT] Connected.");

    using NetworkStream stream = client.GetStream();
    using var cts = new CancellationTokenSource();

    Task receiveTask = ReceiveLoopAsync(stream, cts.Token);

    try
    {
        await MessageFraming.WriteAsync(
            stream,
            CreateMessage("chat.join", Guid.NewGuid().ToString(),
                new { username }),
            cts.Token);

        Console.WriteLine("Type a message and press Enter.");
        Console.WriteLine("Type /quit to disconnect.");

        while (!cts.IsCancellationRequested)
        {
            string? line = Console.ReadLine();

            if (line is null ||
                line.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await MessageFraming.WriteAsync(
                stream,
                CreateMessage("chat.send", Guid.NewGuid().ToString(),
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
    }
}
catch (SocketException ex)
{
    Console.WriteLine($"[CONNECTION ERROR] {ex.Message}");
}
catch (IOException ex)
{
    Console.WriteLine($"[IO ERROR] {ex.Message}");
}

static async Task ReceiveLoopAsync(
    NetworkStream stream,
    CancellationToken cancellationToken)
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
            case "chat.joined":
                Console.WriteLine("[CLIENT] Joined the chat.");
                break;

            case "chat.receive":
                string sender = message.Payload
                    .GetProperty("sender").GetString() ?? "Unknown";

                string text = message.Payload
                    .GetProperty("text").GetString() ?? "";

                Console.WriteLine($"[{sender}] {text}");
                break;

            case "chat.system":
                Console.WriteLine($"[SYSTEM] {message.Payload
                    .GetProperty("text").GetString()}");
                break;

            case "error":
                Console.WriteLine($"[ERROR] {message.Payload
                    .GetProperty("message").GetString()}");
                break;

            case "presence.list":
                {
                    string[] users = message.Payload
                        .GetProperty("users")
                        .EnumerateArray()
                        .Select(user => user.GetString() ?? "")
                        .Where(user => user.Length > 0)
                        .ToArray();

                    Console.WriteLine($"[ONLINE USERS] {string.Join(", ", users)}");
                    break;
                }

            case "presence.changed":
                {
                    string username = message.Payload
                        .GetProperty("username")
                        .GetString() ?? "Unknown";

                    bool online = message.Payload
                        .GetProperty("online")
                        .GetBoolean();

                    Console.WriteLine(
                        online
                            ? $"[PRESENCE] {username} is online."
                            : $"[PRESENCE] {username} is offline.");

                    break;
                }

            default:
                Console.WriteLine($"[UNKNOWN] {message.Type}");
                break;
        }
    }
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
