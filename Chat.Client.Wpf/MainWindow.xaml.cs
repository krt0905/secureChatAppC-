
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chat.Client.Core;
using Chat.Protocol;

namespace Chat.Client.Wpf;
/*
public partial class MainWindow : Window
{
    private ChatClient? _chatClient;

    private readonly ObservableCollection<string> _onlineUsers = new();
    private readonly ObservableCollection<string> _messages = new();

    public MainWindow()
    {
        InitializeComponent();

        OnlineList.ItemsSource = _onlineUsers;
        MessagesList.ItemsSource = _messages;
    }

    private async void ConnectButton_Click(
        object sender, RoutedEventArgs e)
    {
        if (_chatClient is not null)
        {
            ConnectButton.IsEnabled = false;

            try
            {
                await _chatClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                AddMessage($"[Disconnect error] {ex.Message}");
            }

            _chatClient = null;
            ConnectButton.Content = "Connect";
            StatusText.Text = "Disconnected";
            _onlineUsers.Clear();
            ConnectButton.IsEnabled = true;
            return;
        }

        string host = HostBox.Text.Trim();
        string username = UsernameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            StatusText.Text = "Enter a server host.";
            return;
        }

        if (username.Length is < 1 or > 24)
        {
            StatusText.Text = "Username must be 1–24 characters.";
            return;
        }

        ConnectButton.IsEnabled = false;

        var client = new ChatClient();

        client.StatusChanged += status =>
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
            });
        };

        client.MessageReceived += message =>
        {
            Dispatcher.Invoke(() => HandleMessage(message));
        };

        try
        {
            await client.ConnectAsync(host, 5000, username);

            _chatClient = client;
            ConnectButton.Content = "Disconnect";
            AddMessage($"Connected as {username}.");
        }
        catch (Exception ex)
        {
            AddMessage($"[Connection error] {ex.Message}");

            await client.DisposeAsync();

            StatusText.Text = "Connection failed";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void HandleMessage(ChatMessage message)
    {
        switch (message.Type)
        {
            case "chat.joined":
                AddMessage("You joined the chat.");
                break;

            case "presence.list":
                _onlineUsers.Clear();

                foreach (var item in message.Payload
                    .GetProperty("users").EnumerateArray())
                {
                    string username = item.GetString() ?? "";

                    if (username.Length > 0 &&
                        !_onlineUsers.Contains(username))
                    {
                        _onlineUsers.Add(username);
                    }
                }
                break;

            case "presence.changed":
                {
                    string username = message.Payload
                        .GetProperty("username").GetString() ?? "";

                    bool online = message.Payload
                        .GetProperty("online").GetBoolean();

                    if (online)
                    {
                        if (!_onlineUsers.Contains(username))
                            _onlineUsers.Add(username);

                        AddMessage($"{username} came online.");
                    }
                    else
                    {
                        _onlineUsers.Remove(username);
                        AddMessage($"{username} went offline.");
                    }

                    break;
                }

            case "chat.receive":
                {
                    string sender = message.Payload
                        .GetProperty("sender").GetString() ?? "Unknown";

                    string text = message.Payload
                        .GetProperty("text").GetString() ?? "";

                    AddMessage($"[{sender}] {text}");
                    break;
                }

            case "chat.system":
                AddMessage(message.Payload
                    .GetProperty("text").GetString() ?? "");
                break;

            case "error":
                AddMessage("[Server error] " +
                    (message.Payload.GetProperty("message")
                        .GetString() ?? "Unknown error"));
                break;

            default:
                AddMessage($"[Unhandled message] {message.Type}");
                break;
        }
    }

    private async void SendButton_Click(
        object sender, RoutedEventArgs e)
    {
        await SendCurrentMessageAsync();
    }

    private async void MessageInput_KeyDown(
        object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendCurrentMessageAsync();
        }
    }

    private async Task SendCurrentMessageAsync()
    {
        string text = MessageInput.Text.Trim();

        if (text.Length == 0)
            return;

        if (_chatClient is null)
        {
            StatusText.Text = "Connect to the server first.";
            return;
        }

        SendButton.IsEnabled = false;

        try
        {
            await _chatClient.SendChatAsync(text);
            MessageInput.Clear();
        }
        catch (Exception ex)
        {
            AddMessage($"[Send error] {ex.Message}");
        }
        finally
        {
            SendButton.IsEnabled = true;
            MessageInput.Focus();
        }
    }

    private void AddMessage(string text)
    {
        _messages.Add($"[{DateTime.Now:HH:mm:ss}] {text}");

        // Giữ số dòng trong giao diện ở mức hợp lý.
        while (_messages.Count > 500)
            _messages.RemoveAt(0);

        if (MessagesList.Items.Count > 0)
            MessagesList.ScrollIntoView(
                MessagesList.Items[MessagesList.Items.Count - 1]);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_chatClient is not null)
        {
            await _chatClient.DisposeAsync();
            _chatClient = null;
        }

        base.OnClosed(e);
    }

    private void MessageInput_TextChanged(object sender, TextChangedEventArgs e)
    {

    }

    private void UsernameBox_TextChanged(object sender, TextChangedEventArgs e)
    {

    }
}*/

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameTextBox.Text.Trim();
        string password = PasswordInput.Password;

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            StatusTextBlock.Text =
                "Vui lòng nhập tên tài khoản và mật khẩu.";
            return;
        }

        StatusTextBlock.Foreground =
            System.Windows.Media.Brushes.DarkOrange;

        StatusTextBlock.Text =
            "Giao diện đã nhận thông tin. Chưa kết nối server.";
    }

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Foreground =
            System.Windows.Media.Brushes.DarkOrange;

        StatusTextBlock.Text =
            "Chức năng đăng ký sẽ được kết nối ở bước tiếp theo.";
    }
}