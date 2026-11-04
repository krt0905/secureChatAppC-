
using System.Text.Json;
using Chat.Protocol;

namespace Chat.Server.Auth;

public sealed class AuthMessageHandler
{
    private readonly AuthenticationService _auth;

    public AuthMessageHandler(AuthenticationService auth)
    {
        _auth = auth;
    }

    public async Task HandleAsync(
        ClientSession session,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        // Phải thay điều kiện này bằng kiểm tra TLS thực sự
        // của ClientSession trước khi cho phép nhận mật khẩu.
        if (!session.IsSecureTransport)
        {
            await SendResultAsync(
                session,
                new AuthResult(
                    false, "tls_required",
                    "A secure TLS connection is required."),
                message.RequestId);

            return;
        }

        if (session.IsAuthenticated)
        {
            await SendResultAsync(
                session,
                new AuthResult(
                    false, "already_authenticated",
                    "This connection is already authenticated."),
                message.RequestId);

            return;
        }

        switch (message.Type)
        {
            case "auth.register":
                {
                    var request =
                        message.Payload.Deserialize<RegisterRequest>();

                    if (request is null)
                    {
                        await SendResultAsync(
                            session,
                            new AuthResult(
                                false, "invalid_request",
                                "Invalid registration request."),
                            message.RequestId);
                        return;
                    }

                    var result = await _auth.RegisterAsync(
                        request, cancellationToken);

                    await SendResultAsync(
                        session, result, message.RequestId);
                    return;
                }

            case "auth.login":
                {
                    var request =
                        message.Payload.Deserialize<LoginRequest>();

                    if (request is null)
                    {
                        await SendResultAsync(
                            session,
                            new AuthResult(
                                false, "invalid_request",
                                "Invalid login request."),
                            message.RequestId);
                        return;
                    }

                    var (result, username) = await _auth.LoginAsync(
                        request, cancellationToken);

                    if (!result.Success || username is null)
                    {
                        await SendResultAsync(
                            session, result, message.RequestId);
                        return;
                    }

                    // Bước kế tiếp sẽ gắn session vào ChatHub.
                    // Chưa được xem phiên là online chỉ vì mật khẩu đúng.
                    if (!session.TrySetAuthenticated(username))
                    {
                        await SendResultAsync(
                            session,
                            new AuthResult(
                                false, "authentication_failed",
                                "Unable to authenticate this connection."),
                            message.RequestId);
                        return;
                    }

                    await SendResultAsync(
                        session, result, message.RequestId);
                    return;
                }

            default:
                await SendResultAsync(
                    session,
                    new AuthResult(
                        false, "unsupported_auth_message",
                        "Unsupported authentication message."),
                    message.RequestId);
                return;
        }
    }

    private static Task SendResultAsync(
        ClientSession session,
        AuthResult result,
        string? requestId)
    {
        // TODO: tạo ChatMessage với Type = "auth.result",
        // RequestId = requestId và Payload = JSON của result.
        // Dùng đúng cách tạo ChatMessage đang có trong dự án.
        throw new NotImplementedException(
            "Connect this method to the existing ChatMessage factory.");
    }
}
