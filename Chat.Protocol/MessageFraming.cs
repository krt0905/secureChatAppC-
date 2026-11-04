
using System.Buffers.Binary;
using System.Text.Json;

namespace Chat.Protocol;

public static class MessageFraming
{
    private const int HeaderSize = 4;
    public const int MaxMessageBytes = 1024 * 1024; // 1 MiB

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteAsync(
        Stream stream,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            message, JsonOptions);

        if (json.Length == 0 || json.Length > MaxMessageBytes)
            throw new InvalidDataException("Invalid message size.");

        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32BigEndian(header, json.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<ChatMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[HeaderSize];

        bool hasHeader = await ReadExactlyAsync(
            stream, header, allowCleanEnd: true, cancellationToken);

        if (!hasHeader)
            return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length <= 0 || length > MaxMessageBytes)
            throw new InvalidDataException("Invalid message length.");

        byte[] json = new byte[length];

        await ReadExactlyAsync(
            stream, json, allowCleanEnd: false, cancellationToken);

        ChatMessage? message =
            JsonSerializer.Deserialize<ChatMessage>(json, JsonOptions);

        if (message is null || string.IsNullOrWhiteSpace(message.Type))
            throw new InvalidDataException("Invalid message.");

        return message;
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        bool allowCleanEnd,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer[totalRead..], cancellationToken);

            if (read == 0)
            {
                if (totalRead == 0 && allowCleanEnd)
                    return false;

                throw new EndOfStreamException(
                    "Connection closed in the middle of a message.");
            }

            totalRead += read;
        }

        return true;
    }
}
