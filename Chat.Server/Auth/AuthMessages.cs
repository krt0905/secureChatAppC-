
namespace Chat.Server.Auth;

public sealed record RegisterRequest(
    string Username,
    string Password);

public sealed record LoginRequest(
    string Username,
    string Password);

public sealed record AuthResult(
    bool Success,
    string Code,
    string Message,
    string? Username = null);
