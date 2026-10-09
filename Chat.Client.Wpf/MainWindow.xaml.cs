/*
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chat.Client.Core;
using Chat.Protocol;

namespace Chat.Client.Wpf;

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
}*/


using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;

namespace Chat.Client.Wpf;

public partial class MainWindow : Window
{
    private ChatConnection? _connection;
    private bool _connectionTransferred;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void LoginButton_Click(
        object sender, RoutedEventArgs e)
    {
        string username = UsernameTextBox.Text.Trim();
        string password = PasswordInput.Password;

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password))
        {
            ShowStatus("Vui lòng nhập tên tài khoản và mật khẩu.");
            return;
        }

        SetBusy(true);

        try
        {
            ChatConnection connection = await GetConnectionAsync();

            AuthResponse result = await connection.LoginAsync(
                username, password);

            if (!result.Success)
            {
                ShowStatus($"Đăng nhập thất bại: {result.Message}");
                return;
            }

            string authenticatedUsername = result.Username ?? username;

            // Chuyển quyền sở hữu kết nối sang cửa sổ chat.
            _connectionTransferred = true;
            _connection = null;

            var chatWindow = new ChatWindow(
                connection, authenticatedUsername);

            chatWindow.Closed += (_, _) => Close();

            chatWindow.Show();
            Hide();
        }
        catch (AuthenticationException)
        {
            ShowStatus(
                "Không xác thực được chứng chỉ TLS của server. " +
                "Hãy kiểm tra chứng chỉ và tên máy chủ.");
        }
        catch (TimeoutException)
        {
            ShowStatus("Server không phản hồi kịp thời.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Kết nối đã bị hủy.");
        }
        catch (SocketException)
        {
            ShowStatus(
                "Không kết nối được server. Hãy kiểm tra server và cổng 5000.");
            await ResetConnectionAsync();
        }
        catch (IOException)
        {
            ShowStatus("Kết nối bị gián đoạn. Hãy thử lại.");
            await ResetConnectionAsync();
        }
        catch (Exception ex)
        {
            ShowStatus($"Có lỗi: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RegisterButton_Click(
        object sender, RoutedEventArgs e)
    {
        string username = UsernameTextBox.Text.Trim();
        string password = PasswordInput.Password;

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password))
        {
            ShowStatus("Nhập tên tài khoản và mật khẩu để đăng ký.");
            return;
        }

        SetBusy(true);

        try
        {
            ChatConnection connection = await GetConnectionAsync();

            AuthResponse result = await connection.RegisterAsync(
                username, password);

            if (!result.Success)
            {
                ShowStatus($"Đăng ký thất bại: {result.Message}");
                return;
            }

            ShowStatus(
                "Đăng ký thành công! Đang đăng nhập...",
                isError: false);

            // Server cho phép đăng nhập tiếp trên cùng kết nối.
            AuthResponse login = await connection.LoginAsync(
                username, password);

            if (!login.Success)
            {
                ShowStatus(
                    $"Đăng ký thành công nhưng đăng nhập thất bại: {login.Message}");
                return;
            }

            string authenticatedUsername = login.Username ?? username;

            _connectionTransferred = true;
            _connection = null;

            var chatWindow = new ChatWindow(
                connection, authenticatedUsername);

            chatWindow.Closed += (_, _) => Close();

            chatWindow.Show();
            Hide();
        }
        catch (AuthenticationException)
        {
            ShowStatus("Không xác thực được chứng chỉ TLS của server.");
        }
        catch (TimeoutException)
        {
            ShowStatus("Server không phản hồi kịp thời.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Không thể đăng ký: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<ChatConnection> GetConnectionAsync()
    {
        if (_connection is { IsConnected: true })
            return _connection;

        await ResetConnectionAsync();

        var connection = new ChatConnection();

        try
        {
            // Server đang chạy trên cùng máy.
            // Nếu server ở máy khác, đổi thành hostname/IP phù hợp.
            await connection.ConnectAsync("127.0.0.1", 5000);

            _connection = connection;
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task ResetConnectionAsync()
    {
        if (_connection is null)
            return;

        ChatConnection oldConnection = _connection;
        _connection = null;

        await oldConnection.DisposeAsync();
    }

    private void ShowStatus(string text, bool isError = true)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = isError
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.ForestGreen;
    }

    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        RegisterButton.IsEnabled = !busy;
        UsernameTextBox.IsEnabled = !busy;
        PasswordInput.IsEnabled = !busy;

        LoginButton.Content = busy
            ? "Đang xử lý..."
            : "Đăng nhập";
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (!_connectionTransferred)
            await ResetConnectionAsync();
    }
}
