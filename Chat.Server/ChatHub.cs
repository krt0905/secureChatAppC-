
using System.Collections.Concurrent;
using Chat.Protocol;

namespace Chat.Server;

public sealed class ChatHub
{
    private readonly ConcurrentDictionary<string, ClientSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryJoin(ClientSession session, string username)
    {
        if (!_sessions.TryAdd(username, session))
            return false;

        session.Username = username;
        return true;
    }

    public bool Leave(ClientSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Username))
            return false;

        if (!_sessions.TryGetValue(session.Username, out var current))
            return false;

        if (!ReferenceEquals(current, session))
            return false;

        return _sessions.TryRemove(session.Username, out _);
    }

    public string[] GetOnlineUsers()
    {
        return _sessions.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Broadcast(ChatMessage message)
    {
        foreach (ClientSession session in _sessions.Values)
        {
            if (!session.TrySend(message))
            {
                // Hàng đợi đầy: đóng kết nối để giải phóng tài nguyên.
                session.Client.Dispose();
            }
        }
    }
}
