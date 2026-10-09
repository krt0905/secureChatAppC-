
using System.Net.Sockets;
using System.Threading.Channels;
using Chat.Protocol;

namespace Chat.Server;

public sealed class ClientSession : IDisposable
{
    private readonly Channel<ChatMessage> _outgoing =
        Channel.CreateBounded<ChatMessage>(
            new BoundedChannelOptions(100)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Username { get; set; } = "";

    public TcpClient Client { get; }

    public NetworkStream Stream { get; }

    public ClientSession(TcpClient client)
    {
        Client = client;
        Stream = client.GetStream();
    }

    public bool TrySend(ChatMessage message)
    {
        // Không chờ nếu hàng đợi đã đầy.
        // Server sẽ ngắt client chậm ở tầng quản lý kết nối.
        return _outgoing.Writer.TryWrite(message);
    }

    public async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (ChatMessage message in
            _outgoing.Reader.ReadAllAsync(cancellationToken))
        {
            await MessageFraming.WriteAsync(
                Stream, message, cancellationToken);
        }
    }

    public void CompleteSending()
    {
        _outgoing.Writer.TryComplete();
    }

    public void Dispose()
    {
        _outgoing.Writer.TryComplete();
        Stream.Dispose();
        Client.Dispose();
    }

    private string? _authenticatedUsername;

    public string? AuthenticatedUsername =>
        Volatile.Read(ref _authenticatedUsername);

    public bool IsAuthenticated =>
        AuthenticatedUsername is not null;

    public bool TrySetAuthenticated(string username)
    {
        return Interlocked.CompareExchange(
            ref _authenticatedUsername,
            username,
            null) is null;
    }

    public void ClearAuthenticated()
    {
        Interlocked.Exchange(ref _authenticatedUsername, null);
    }
}
