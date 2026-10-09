
using Npgsql;

namespace Chat.Server;

public sealed record SavedMessage(
    long Id,
    long SenderAccountId,
    string Sender,
    string Text,
    DateTimeOffset CreatedAt);

public sealed class MessageRepository : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public MessageRepository(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<SavedMessage?> SaveAsync(
        string username,
        string text,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO public.messages
                (sender_account_id, content)
            SELECT id, @content
            FROM public.accounts
            WHERE normalized_username = @normalized
            RETURNING id, sender_account_id, content, created_at;
            """;

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("content", text);
        command.Parameters.AddWithValue(
            "normalized",
            Chat.Server.Auth.AccountRepository.NormalizeUsername(username));

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            // Không tìm thấy tài khoản tương ứng với người gửi.
            return null;
        }

        return new SavedMessage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            username,
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
