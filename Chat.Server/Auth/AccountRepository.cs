
using Npgsql;

namespace Chat.Server.Auth;

public sealed record AccountRecord(
    long Id,
    string Username,
    string NormalizedUsername,
    byte[] PasswordHash,
    byte[] PasswordSalt,
    int PasswordIterations);

public sealed class AccountRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AccountRepository(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<bool> RegisterAsync(
        string username,
        string password,
        PasswordHasher hasher,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeUsername(username);
        var passwordData = hasher.HashPassword(password);

        const string sql = """
            INSERT INTO accounts
                (username, normalized_username,
                 password_hash, password_salt, password_iterations)
            VALUES
                (@username, @normalized,
                 @hash, @salt, @iterations);
            """;

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("normalized", normalized);
        command.Parameters.AddWithValue("hash", passwordData.Hash);
        command.Parameters.AddWithValue("salt", passwordData.Salt);
        command.Parameters.AddWithValue("iterations", passwordData.Iterations);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Username đã tồn tại.
            return false;
        }
    }

    public async Task<AccountRecord?> FindByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, username, normalized_username,
                   password_hash, password_salt, password_iterations
            FROM accounts
            WHERE normalized_username = @normalized
            LIMIT 1;
            """;

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(
            "normalized", NormalizeUsername(username));

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AccountRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            (byte[])reader["password_hash"],
            (byte[])reader["password_salt"],
            reader.GetInt32(5));
    }

    public static string NormalizeUsername(string username) =>
        username.Trim().ToUpperInvariant();

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
