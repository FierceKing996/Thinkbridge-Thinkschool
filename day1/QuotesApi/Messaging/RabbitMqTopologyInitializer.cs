using RabbitMQ.Client;

namespace QuotesApi.Messaging;

// Runs once at startup, declares the topology, and exits - IHostedService (not
// BackgroundService) is the right base here specifically because there's no
// ongoing loop: StartAsync does the one-time work and returns, StopAsync has
// nothing to do. Registered before the consumers in DI, but declares are
// idempotent anyway so start order between this and the consumers doesn't
// actually matter - see RabbitMqTopology.
public class RabbitMqTopologyInitializer(
    RabbitMqConnection connection, RabbitMqOptions options, ILogger<RabbitMqTopologyInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await using var channel = await connection.CreateChannelAsync(ct);
            await RabbitMqTopology.DeclareAsync(channel, options, ct);
        }
        catch (Exception ex)
        {
            // RabbitMq:HostName being configured doesn't guarantee the broker is
            // actually up yet (e.g. `docker compose up` still starting) - an
            // unhandled exception from an IHostedService.StartAsync fails the
            // *entire host*, not just this feature, so this has to swallow it. Both
            // consumers and the publisher declare the topology again themselves
            // before they use it, so a failed declare here just means "not
            // pre-warmed", not "broken forever".
            logger.LogWarning(ex, "Could not declare RabbitMQ topology at startup; will retry lazily on first use.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
