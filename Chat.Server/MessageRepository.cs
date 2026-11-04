
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


    public async Task<List<SavedMessage>> GetHistoryAsync(
        long? beforeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Lấy thêm một bản ghi để biết có trang cũ hơn hay không.
        int fetchLimit = Math.Clamp(limit, 1, 50) + 1;

        const string sql = """
        SELECT id, sender_account_id, username, content, created_at
        FROM (
            SELECT
                m.id,
                m.sender_account_id,
                a.username,
                m.content,
                m.created_at
            FROM public.messages AS m
            JOIN public.accounts AS a
                ON a.id = m.sender_account_id
            WHERE (@before_id IS NULL OR m.id < @before_id)
            ORDER BY m.id DESC
            LIMIT @fetch_limit
        ) AS page
        ORDER BY id ASC;
        """;

        var result = new List<SavedMessage>();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.Add(
            new NpgsqlParameter("before_id", NpgsqlTypes.NpgsqlDbType.Bigint)
            {
                Value = beforeId.HasValue
                    ? beforeId.Value
                    : DBNull.Value
            });

        command.Parameters.AddWithValue("fetch_limit", fetchLimit);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SavedMessage(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return result;
    }

}
