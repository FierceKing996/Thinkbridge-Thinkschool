using System.Threading.Channels;

namespace QuotesApi.Services.BackgroundJobs;

// Channel<T> is the whole implementation: a Channel is already a thread-safe,
// async-friendly producer/consumer queue, so there's no lock or semaphore to
// write by hand. Bounded (not Channel.CreateUnbounded) on purpose - an
// unbounded queue behind a slow consumer is just a memory leak with a delay;
// BoundedChannelFullMode.Wait makes QueueBackgroundWorkItemAsync itself apply
// backpressure to callers instead.
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
        ArgumentNullException.ThrowIfNull(workItem);
        await _queue.Writer.WriteAsync(workItem, ct);
    }

    public async ValueTask<BackgroundWorkItem> DequeueAsync(CancellationToken ct) =>
        await _queue.Reader.ReadAsync(ct);
}
