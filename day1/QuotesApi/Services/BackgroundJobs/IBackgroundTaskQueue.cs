namespace QuotesApi.Services.BackgroundJobs;

// A work item gets its own DI scope at execution time (see QueuedHostedService),
// never the scope of whatever request enqueued it - that request may already have
// finished (and its scoped services disposed) long before the queue drains.
public delegate ValueTask BackgroundWorkItem(IServiceProvider scopedServices, CancellationToken ct);

public interface IBackgroundTaskQueue
{
    // Never throws when full: it waits (backpressure) rather than dropping work,
    // so a burst of enqueues can't silently lose a job.
    ValueTask QueueBackgroundWorkItemAsync(BackgroundWorkItem workItem, CancellationToken ct = default);

    ValueTask<BackgroundWorkItem> DequeueAsync(CancellationToken ct);
}
