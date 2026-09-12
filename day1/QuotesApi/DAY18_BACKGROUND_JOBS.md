# Day 18 — Background jobs

## The BackgroundService + how it shuts down cleanly

`Services/BackgroundJobs/BackgroundTaskQueue.cs` — the queue (a bounded `Channel<T>`):

```csharp
public class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<BackgroundWorkItem> _queue;

    public BackgroundTaskQueue(int capacity = 100)
    {
        _queue = Channel.CreateBounded<BackgroundWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public async ValueTask QueueBackgroundWorkItemAsync(BackgroundWorkItem workItem, CancellationToken ct = default)
    {
        await _queue.Writer.WriteAsync(workItem, ct);
    }

    public async ValueTask<BackgroundWorkItem> DequeueAsync(CancellationToken ct) =>
        await _queue.Reader.ReadAsync(ct);
}
```

`Services/BackgroundJobs/QueuedHostedService.cs` — the `BackgroundService` that drains it:

```csharp
public class QueuedHostedService(
    IBackgroundTaskQueue taskQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<QueuedHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            BackgroundWorkItem workItem;
            try
            {
                workItem = await taskQueue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // shutdown while waiting for the next item - not an error
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await workItem(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred executing background work item.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Queued hosted service is stopping gracefully.");
        await base.StopAsync(cancellationToken);
    }
}
```

**Graceful shutdown, concretely:** the host calls `StopAsync` with a shutdown-timeout
token (`Host:ShutdownTimeout`, 30s by default) when the process is asked to stop.
`base.StopAsync` signals `stoppingToken` — which unblocks the `DequeueAsync` call
above via `OperationCanceledException`, caught and turned into a clean `break` — and
then *awaits* the `Task` that `ExecuteAsync` returned. That await is what makes this
graceful rather than abrupt: if a work item is already running when shutdown starts,
the loop finishes that `await workItem(...)` call before the loop condition is even
checked again, so the process doesn't exit mid-item. Only a work item that ignores
its own `ct` and runs past the shutdown timeout gets torn down mid-flight — which is
why `QuoteCsvExportJob` checks `ct.ThrowIfCancellationRequested()` between pages
instead of only at the very start.

A single throwing work item is caught and logged, not rethrown — an unhandled
exception out of `ExecuteAsync` would stop the *entire hosted service* (BackgroundService
has no per-iteration exception boundary of its own), silently abandoning every job
still sitting in the queue behind it.

The concrete job wired up on top: `POST /api/exports/quotes` enqueues a CSV export
of all quotes (`Services/BackgroundJobs/QuoteCsvExportJob.cs`), returns `202
Accepted` immediately with a job id, and `GET /api/exports/quotes/{id}` polls an
in-memory `ExportJobStatusStore` for `Queued → Running → Completed/Failed`.

## When Hangfire over a hosted service?

When the trigger isn't "a request just happened" but **time** (recurring/scheduled
jobs), or the job needs to **survive a process restart** — Hangfire persists its job
queue to a real store (SQL Server, Redis, etc.), so a job enqueued five minutes
before a deploy still runs after it; `BackgroundTaskQueue` here is an in-memory
`Channel`, so a restart while items are queued loses them, which is fine for "drain
what showed up during this process's lifetime" but wrong for "this must run, even if
the app restarts."
