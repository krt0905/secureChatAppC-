
namespace Chat.Server.Auth;

public static class AuthValidation
{
    public static bool IsValidUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        username = username.Trim();

        if (username.Length is < 3 or > 32)
            return false;

        return username.All(c =>
            char.IsAsciiLetterOrDigit(c) ||
            c == '_' ||
            c == '-');
    }

    public static bool IsValidPassword(string? password)
    {
        // Ví dụ chính sách tối thiểu; không cắt ngắn mật khẩu âm thầm.
        return password is { Length: >= 12 and <= 128 };
    }
}
