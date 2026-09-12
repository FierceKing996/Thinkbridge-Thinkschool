using RabbitMQ.Client;

namespace QuotesApi.Messaging;

// Owns exactly one IConnection for the app's whole lifetime - RabbitMQ.Client
// connections are heavyweight (a TCP socket + a background reader loop) but
// IChannel is cheap, so the pattern is one shared connection, one channel per
// publisher/consumer. Lazy + double-checked locking because the first caller
// (could be the topology initializer, a consumer, or the outbox relay,
// depending on hosted-service start order) is the one that pays the connect cost.
public class RabbitMqConnection(RabbitMqOptions options) : IAsyncDisposable
{
    private IConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IChannel> CreateChannelAsync(CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        return await connection.CreateChannelAsync(cancellationToken: ct);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;

            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                VirtualHost = options.VirtualHost,
            };

            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }
    }
}
