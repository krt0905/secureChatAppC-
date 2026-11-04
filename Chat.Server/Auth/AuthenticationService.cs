
using System.Text.Json;
using Chat.Protocol;

namespace Chat.Server.Auth;

public sealed class AuthenticationService
{
    private readonly AccountRepository _accounts;
    private readonly PasswordHasher _hasher;

    public AuthenticationService(
        AccountRepository accounts,
        PasswordHasher hasher)
    {
        _accounts = accounts;
        _hasher = hasher;
    }

    public async Task<AuthResult> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!AuthValidation.IsValidUsername(request.Username))
        {
            return new AuthResult(
                false, "invalid_username",
                "Username must be 3-32 ASCII letters, digits, '_' or '-'.");
        }

        if (!AuthValidation.IsValidPassword(request.Password))
        {
            return new AuthResult(
                false, "invalid_password",
                "Password must contain 12-128 characters.");
        }

        bool created = await _accounts.RegisterAsync(
            request.Username.Trim(),
            request.Password,
            _hasher,
            cancellationToken);

        return created
            ? new AuthResult(
                true, "registered",
                "Account created. Please log in.")
            : new AuthResult(
                false, "username_taken",
                "Unable to create this account.");
    }

    public async Task<(AuthResult Result, string? CanonicalUsername)>
        LoginAsync(
            LoginRequest request,
            CancellationToken cancellationToken = default)
    {
        if (!AuthValidation.IsValidUsername(request.Username) ||
            string.IsNullOrEmpty(request.Password))
        {
            return (
                new AuthResult(
                    false, "invalid_credentials",
                    "Invalid username or password."),
                null);
        }

        var account = await _accounts.FindByUsernameAsync(
            request.Username,
            cancellationToken);

        if (account is null ||
            !_hasher.VerifyPassword(
                request.Password,
                account.PasswordSalt,
                account.PasswordHash,
                account.PasswordIterations))
        {
            return (
                new AuthResult(
                    false, "invalid_credentials",
                    "Invalid username or password."),
                null);
        }

        return (
            new AuthResult(
                true, "authenticated",
                "Login successful.",
                account.Username),
            account.Username);
    }
}
