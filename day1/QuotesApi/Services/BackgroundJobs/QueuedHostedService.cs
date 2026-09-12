namespace QuotesApi.Services.BackgroundJobs;

// BackgroundService (not a raw IHostedService) because all this needs is a single
// long-running loop with a cancellation token - BackgroundService already wraps
// StartAsync/StopAsync/ExecuteAsync plumbing so this class only has to write the
// loop body. See DAY18_BACKGROUND_JOBS.md for why this - and not Hangfire - is the
// right tool for "drain a queue", and what graceful shutdown actually means here.
public class QueuedHostedService(
    IBackgroundTaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<QueuedHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queued hosted service running.");

        while (!stoppingToken.IsCancellationRequested)
        {
            BackgroundWorkItem workItem;
            try
            {
                workItem = await taskQueue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown: DequeueAsync was blocked waiting for work when
                // stoppingToken fired. Not an error, just the loop's exit condition.
                break;
            }

            try
            {
                // A fresh scope per item, not the queue's own (singleton) scope - a
                // work item queued from a request needs its own AppDbContext etc.,
                // not one shared and mutated by every other item ever dequeued.
                await using var scope = scopeFactory.CreateAsyncScope();
                await workItem(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                // One bad job must not take the whole drain loop down - swallow, log,
                // and move on to the next item. Without this catch, an unhandled
                // exception here would propagate out of ExecuteAsync and stop the
                // hosted service entirely, silently abandoning every job still queued.
                logger.LogError(ex, "Error occurred executing background work item.");
            }
        }

        logger.LogInformation("Queued hosted service stopping.");
    }

    // Graceful shutdown: the host calls StopAsync with a shutdown-timeout token
    // (Host:ShutdownTimeout, default 30s) when the app is asked to stop. Overriding
    // StopAsync to log first, then delegating to the base implementation, is what
    // makes shutdown graceful rather than abrupt - base.StopAsync signals
    // stoppingToken (unblocking DequeueAsync above) and then awaits ExecuteAsync's
    // Task, so any work item already in flight gets to finish (or the item throws
    // OperationCanceledException itself, if it's cooperative) before the process
    // exits - the loop never gets killed mid-item the way it would if the host
    // just tore down the process on the timeout expiring.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Queued hosted service is stopping gracefully.");
        await base.StopAsync(cancellationToken);
    }
}
