
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Chat.Protocol;
using Xunit;

namespace Tests;

public class MessageFramingTests
{
    private static ChatMessage CreateMessage(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(
            JsonSerializer.Serialize(new { text }));

        return new ChatMessage
        {
            Type = "chat.send",
            RequestId = Guid.NewGuid().ToString(),
            Payload = doc.RootElement.Clone()
        };
    }

    private static byte[] CreateFrame(byte[] json)
    {
        byte[] frame = new byte[4 + json.Length];

        BinaryPrimitives.WriteInt32BigEndian(
            frame.AsSpan(0, 4), json.Length);

        json.CopyTo(frame, 4);
        return frame;
    }

    // Test 2: Header và JSON có thể bị chia nhỏ khi đọc.
    [Fact]
    public async Task ReadAsync_HandlesFragmentedHeaderAndJson()
    {
        using var output = new MemoryStream();

        await MessageFraming.WriteAsync(
            output, CreateMessage("hello"));

        byte[] data = output.ToArray();

        // Mỗi lần đọc chỉ trả về tối đa 1 byte.
        using var input = new ChunkedReadStream(data, 1);

        ChatMessage? result = await MessageFraming.ReadAsync(input);

        Assert.NotNull(result);
        Assert.Equal("chat.send", result.Type);
        Assert.Equal("hello", result.Payload.GetProperty("text").GetString());
    }

    // Mục 3: Nhiều thông điệp nối tiếp trên cùng một stream.
    [Fact]
    public async Task ReadAsync_HandlesMultipleMessages()
    {
        using var stream = new MemoryStream();

        await MessageFraming.WriteAsync(
            stream, CreateMessage("first"));

        await MessageFraming.WriteAsync(
            stream, CreateMessage("second"));

        stream.Position = 0;

        ChatMessage? first = await MessageFraming.ReadAsync(stream);
        ChatMessage? second = await MessageFraming.ReadAsync(stream);
        ChatMessage? third = await MessageFraming.ReadAsync(stream);

        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Equal("first",
            first.Payload.GetProperty("text").GetString());

        Assert.Equal("second",
            second.Payload.GetProperty("text").GetString());

        // Hết dữ liệu ở ranh giới thông điệp thì trả về null.
        Assert.Null(third);
    }

    // Mục 4: Từ chối độ dài âm hoặc vượt quá giới hạn.
    [Theory]
    [InlineData(-1)]
    [InlineData(1048577)]
    public async Task ReadAsync_RejectsInvalidLength(int length)
    {
        byte[] header = new byte[4];

        BinaryPrimitives.WriteInt32BigEndian(header, length);

        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => MessageFraming.ReadAsync(stream));
    }

    // Mục 5a: Từ chối JSON không hợp lệ.
    [Fact]
    public async Task ReadAsync_RejectsInvalidJson()
    {
        byte[] invalidJson = Encoding.UTF8.GetBytes("{bad json");
        byte[] frame = CreateFrame(invalidJson);

        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<JsonException>(
            () => MessageFraming.ReadAsync(stream));
    }

    // Mục 5b: Báo lỗi nếu kết nối đóng giữa thông điệp.
    [Fact]
    public async Task ReadAsync_RejectsTruncatedMessage()
    {
        byte[] header = new byte[4];

        // Khai báo 10 byte JSON...
        BinaryPrimitives.WriteInt32BigEndian(header, 10);

        // ...nhưng chỉ gửi 2 byte rồi kết thúc stream.
        byte[] frame = header.Concat(
            Encoding.UTF8.GetBytes("{}")).ToArray();

        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => MessageFraming.ReadAsync(stream));
    }

    // Stream kiểm thử mô phỏng dữ liệu đến theo từng khối nhỏ.
    private sealed class ChunkedReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maxChunkSize;

        public ChunkedReadStream(byte[] data, int maxChunkSize)
        {
            _inner = new MemoryStream(data);
            _maxChunkSize = maxChunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int count = Math.Min(buffer.Length, _maxChunkSize);

            return _inner.ReadAsync(
                buffer[..count], cancellationToken);
        }

        public override int Read(
            byte[] buffer, int offset, int count)
        {
            return _inner.Read(
                buffer, offset, Math.Min(count, _maxChunkSize));
        }

        public override void Flush() =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
