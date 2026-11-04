
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Chat.Protocol;

namespace Chat.Client.Wpf;

public sealed record ChatLine(long? Id, string Text);

public partial class ChatWindow : Window
{
    private readonly ChatConnection _connection;
    private readonly ObservableCollection<ChatLine> _messages = new();
    private readonly ObservableCollection<string> _onlineUsers = new();
    private readonly HashSet<long> _knownMessageIds = new();

    private long? _oldestHistoryId;
    private bool _hasMoreHistory = true;
    private bool _historyPending;
    private bool _initialHistoryLoaded;
    private bool _closing;

    public ChatWindow(ChatConnection connection, string username)
    {
        InitializeComponent();

        _connection = connection;

        UsernameLabel.Text = $"Đăng nhập: {username}";
        MessagesList.ItemsSource = _messages;
        OnlineUsersList.ItemsSource = _onlineUsers;

        _connection.MessageReceived += OnMessageReceived;
        _connection.Disconnected += OnDisconnected;

        Loaded += async (_, _) => await LoadHistoryAsync(older: false);
    }

    private void OnMessageReceived(ChatMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing)
                return;

            switch (message.Type)
            {
                case "chat.receive":
                    {
                        long? id = ReadLong(message.Payload, "messageId");
                        string sender =
                            ReadString(message.Payload, "sender") ?? "?";
                        string text =
                            ReadString(message.Payload, "text") ?? "";

                        AddLiveMessage(id, $"{sender}: {text}");
                        break;
                    }

                case "history.result":
                    DisplayHistory(message.Payload);
                    break;

                case "presence.list":
                    _onlineUsers.Clear();

                    if (message.Payload.TryGetProperty(
                            "users", out JsonElement users) &&
                        users.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement user in users.EnumerateArray())
                        {
                            if (user.ValueKind == JsonValueKind.String)
                            {
                                string? name = user.GetString();

                                if (!string.IsNullOrWhiteSpace(name))
                                    _onlineUsers.Add(name);
                            }
                        }
                    }

                    break;

                case "presence.changed":
                    {
                        string? name =
                            ReadString(message.Payload, "username");

                        bool online =
                            ReadBool(message.Payload, "online");

                        if (string.IsNullOrWhiteSpace(name))
                            break;

                        if (online)
                        {
                            if (!_onlineUsers.Contains(name))
                                _onlineUsers.Add(name);
                        }
                        else
                        {
                            _onlineUsers.Remove(name);
                        }

                        break;
                    }

                case "chat.system":
                    {
                        string text =
                            ReadString(message.Payload, "text") ?? "";

                        AddLiveMessage(null, $"[HỆ THỐNG] {text}");
                        break;
                    }

                case "error":
                    {
                        string text =
                            ReadString(message.Payload, "message")
                            ?? "Lỗi không xác định từ server.";

                        AddLiveMessage(null, $"[LỖI] {text}");
                        break;
                    }
            }
        });
    }

    private void OnDisconnected()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing)
            {
                AddLiveMessage(
                    null,
                    "[HỆ THỐNG] Đã mất kết nối với server.");
                SendButton.IsEnabled = false;
                HistoryButton.IsEnabled = false;
            }
        });
    }

    private async void SendButton_Click(
        object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async void MessageTextBox_KeyDown(
        object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private async Task SendMessageAsync()
    {
        string text = MessageTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return;

        if (text.Length > 4000)
        {
            AddLiveMessage(null, "[LỖI] Tin nhắn tối đa 4000 ký tự.");
            return;
        }

        SendButton.IsEnabled = false;

        try
        {
            var message = new ChatMessage
            {
                Type = "chat.send",
                RequestId = Guid.NewGuid().ToString("N"),
                Payload = JsonSerializer.SerializeToElement(new { text })
            };

            await _connection.SendAsync(message);
            MessageTextBox.Clear();
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is InvalidOperationException ||
            ex is ObjectDisposedException)
        {
            AddLiveMessage(null, "[LỖI] Không gửi được tin nhắn.");
        }
        finally
        {
            SendButton.IsEnabled = _connection.IsConnected;
            MessageTextBox.Focus();
        }
    }

    private async void HistoryButton_Click(
        object sender, RoutedEventArgs e)
    {
        await LoadHistoryAsync(older: true);
    }

    private async Task LoadHistoryAsync(bool older)
    {
        if (_historyPending)
            return;

        if (older && (!_hasMoreHistory || !_oldestHistoryId.HasValue))
            return;

        _historyPending = true;
        HistoryButton.IsEnabled = false;

        try
        {
            var request = new ChatMessage
            {
                Type = "history.get",
                RequestId = Guid.NewGuid().ToString("N"),
                Payload = JsonSerializer.SerializeToElement(new
                {
                    limit = 50,
                    beforeId = older ? _oldestHistoryId : null
                })
            };

            await _connection.SendAsync(request);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is InvalidOperationException ||
            ex is ObjectDisposedException)
        {
            AddLiveMessage(null, "[LỖI] Không tải được lịch sử chat.");
            _historyPending = false;
        }
        finally
        {
            HistoryButton.IsEnabled =
                !_historyPending && _hasMoreHistory &&
                _connection.IsConnected;
        }
    }

    private void DisplayHistory(JsonElement payload)
    {
        if (!payload.TryGetProperty(
                "messages", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            _historyPending = false;
            return;
        }

        var page = items.EnumerateArray().Select(item =>
        {
            long id = item.GetProperty("messageId").GetInt64();
            string sender =
                ReadString(item, "sender") ?? "?";
            string text =
                ReadString(item, "text") ?? "";
            string time =
                ReadString(item, "sentAtUtc") ?? "";

            return new ChatLine(
                id, $"[{time}] {sender}: {text}");
        }).ToList();

        bool olderPage = _initialHistoryLoaded;

        if (!olderPage)
        {
            // Tin nhắn trực tiếp có thể đến trong lúc tải trang đầu.
            // Chỉ thêm những ID chưa có trong danh sách.
            foreach (ChatLine line in page)
            {
                if (line.Id.HasValue &&
                    _knownMessageIds.Add(line.Id.Value))
                {
                    _messages.Add(line);
                }
            }

            _initialHistoryLoaded = true;
        }
        else
        {
            // Server trả từng trang theo thứ tự cũ -> mới.
            // Chèn trang cũ ở đầu danh sách.
            for (int i = page.Count - 1; i >= 0; i--)
            {
                ChatLine line = page[i];

                if (line.Id.HasValue &&
                    _knownMessageIds.Add(line.Id.Value))
                {
                    _messages.Insert(0, line);
                }
            }
        }

        long? pageOldestId = page
            .Where(x => x.Id.HasValue)
            .Select(x => x.Id!.Value)
            .DefaultIfEmpty()
            .Min();

        if (pageOldestId.HasValue &&
            pageOldestId.Value > 0 &&
            (!_oldestHistoryId.HasValue ||
             pageOldestId.Value < _oldestHistoryId.Value))
        {
            _oldestHistoryId = pageOldestId.Value;
        }

        _hasMoreHistory =
            ReadBool(payload, "hasMore");

        _historyPending = false;
        HistoryButton.Content = _hasMoreHistory
            ? "Tải tin nhắn cũ hơn"
            : "Đã tải hết lịch sử";

        HistoryButton.IsEnabled =
            _hasMoreHistory && _connection.IsConnected;

        if (!olderPage && _messages.Count > 0)
            MessagesList.ScrollIntoView(_messages[^1]);
    }

    private void AddLiveMessage(long? id, string text)
    {
        if (id.HasValue && !_knownMessageIds.Add(id.Value))
            return;

        _messages.Add(new ChatLine(id, text));
        MessagesList.ScrollIntoView(_messages[^1]);
    }

    private static string? ReadString(
        JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? ReadLong(
        JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out long result)
            ? result
            : null;
    }

    private static bool ReadBool(
        JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    protected override async void OnClosed(EventArgs e)
    {
        _closing = true;

        _connection.MessageReceived -= OnMessageReceived;
        _connection.Disconnected -= OnDisconnected;

        await _connection.DisposeAsync();

        base.OnClosed(e);
    }
}
