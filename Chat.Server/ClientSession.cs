
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Threading;
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

    private string? _authenticatedUsername;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    // Danh tính chỉ được đặt từ kết quả xác thực phía server.
    public string Username { get; internal set; } = "";

    public TcpClient Client { get; }

    public Stream Stream { get; }

    public bool IsSecureTransport { get; }

    public string? AuthenticatedUsername =>
        Volatile.Read(ref _authenticatedUsername);

    public bool IsAuthenticated =>
        AuthenticatedUsername is not null;

    public ClientSession(TcpClient client, SslStream secureStream)
    {
        if (!secureStream.IsAuthenticated || !secureStream.IsEncrypted)
        {
            throw new InvalidOperationException(
                "ClientSession requires an authenticated TLS stream.");
        }

        Client = client;
        Stream = secureStream;
        IsSecureTransport = true;
    }

    public bool TrySend(ChatMessage message) =>
        _outgoing.Writer.TryWrite(message);

    public async Task SendLoopAsync(
        CancellationToken cancellationToken)
    {
        await foreach (ChatMessage message in
            _outgoing.Reader.ReadAllAsync(cancellationToken))
        {
            await MessageFraming.WriteAsync(
                Stream, message, cancellationToken);
        }
    }

    public bool TrySetAuthenticated(string username) =>
        Interlocked.CompareExchange(
            ref _authenticatedUsername, username, null) is null;

    public void ClearAuthenticated() =>
        Interlocked.Exchange(ref _authenticatedUsername, null);

    public void CompleteSending() =>
        _outgoing.Writer.TryComplete();

    public void Dispose()
    {
        _outgoing.Writer.TryComplete();
        Stream.Dispose();
        Client.Dispose();
    }
}
