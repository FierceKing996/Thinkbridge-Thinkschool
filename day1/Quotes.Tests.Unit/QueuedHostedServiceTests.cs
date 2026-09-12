using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QuotesApi.Services.BackgroundJobs;
using Xunit;

namespace Quotes.Tests.Unit;

// Day 18: the drain loop itself, exercised without ASP.NET Core hosting at all -
// StartAsync/ExecuteAsync/StopAsync are ordinary members on BackgroundService, so
// there's no need for a WebApplicationFactory just to prove the queue drains and
// shuts down cleanly.
public class QueuedHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DrainsQueuedItemsInOrder()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);
        var services = new ServiceCollection().BuildServiceProvider();
        var sut = new QueuedHostedService(queue, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueuedHostedService>.Instance);

        var executed = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var value = i;
            await queue.QueueBackgroundWorkItemAsync((_, _) =>
            {
                executed.Add(value);
                return ValueTask.CompletedTask;
            });
        }

        using var cts = new CancellationTokenSource();
        var run = sut.StartAsync(cts.Token);

        // Poll instead of a fixed delay - the drain loop runs on its own task, and
        // a fixed sleep would either be flaky under load or slower than necessary.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (executed.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await sut.StopAsync(CancellationToken.None);
        await run;

        executed.Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task ExecuteAsync_OneItemThrows_LoopKeepsDrainingLaterItems()
    {
        var queue = new BackgroundTaskQueue(capacity: 10);
        var services = new ServiceCollection().BuildServiceProvider();
        var sut = new QueuedHostedService(queue, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueuedHostedService>.Instance);

        var secondItemRan = false;
        await queue.QueueBackgroundWorkItemAsync((_, _) => throw new InvalidOperationException("boom"));
        await queue.QueueBackgroundWorkItemAsync((_, _) =>
        {
            secondItemRan = true;
            return ValueTask.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        var run = sut.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!secondItemRan && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await sut.StopAsync(CancellationToken.None);
        await run;

        secondItemRan.Should().BeTrue("a failing work item must not stop the drain loop from reaching the next one");
    }
}
