using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace Quotes.Tests.Unit;

// Exercises AuthenticationExtensions.ConfigureResilience directly through the exact same
// AddHttpClient(...).AddResilienceHandler(...) call production code makes -
// only the primary handler is swapped for a fake one that forces transient
// failures, so this proves the *real* pipeline retries, not a copy of it.
public class EntraBackchannelResilienceTests
{
    [Fact]
    public async Task Backchannel_TransientFailures_RetriesThenSucceeds()
    {
        var attempts = 0;
        var fakeHandler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            // First two calls simulate a transient Entra blip (503); the
            // third succeeds - well within MaxRetryAttempts = 3.
            return attempts <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var logs = new List<string>();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new CapturingLoggerProvider(logs)));
        services.AddHttpClient("entra-backchannel")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler)
            .AddResilienceHandler("default", AuthenticationExtensions.ConfigureResilience);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("entra-backchannel");

        var response = await client.GetAsync("https://login.microsoftonline.com/fake-tenant/v2.0/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        attempts.Should().Be(3, "the first two 503s should each trigger a retry before the third call succeeds");
        logs.Should().Contain(l => l.Contains("Retry", StringComparison.OrdinalIgnoreCase),
            "AddResilienceHandler's built-in telemetry should log each retry attempt, not swallow it silently");
    }

    [Fact]
    public async Task Backchannel_PersistentFailures_ExhaustsRetriesAndThrows()
    {
        var attempts = 0;
        var fakeHandler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("entra-backchannel")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler)
            .AddResilienceHandler("default", AuthenticationExtensions.ConfigureResilience);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("entra-backchannel");

        // The retry strategy exhausts and hands back the *last* response as-is -
        // ordinary HttpClient semantics, where GetAsync never throws on a non-2xx
        // by itself. "Never silently swallow failures" means the caller still
        // sees the 503 and EnsureSuccessStatusCode() still throws on it, not
        // that the resilience pipeline itself raises an exception mid-flight.
        var response = await client.GetAsync("https://login.microsoftonline.com/fake-tenant/v2.0/.well-known/openid-configuration");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        attempts.Should().Be(4, "1 initial attempt + MaxRetryAttempts = 3 retries, then the pipeline gives up and returns the last result");
        var act = () => response.EnsureSuccessStatusCode();
        act.Should().Throw<HttpRequestException>("the caller must still be able to detect the failure");
    }

    // Day 22: proves the circuit breaker's actual state machine - Closed -> Open ->
    // HalfOpen -> Closed - not just "retries happen". Uses the same three stages
    // (circuit breaker, timeout, and a bulkhead is added separately below) as
    // production's ConfigureResilience, but with a compressed SamplingDuration/
    // BreakDuration so the whole Open -> recover cycle happens in a few seconds
    // instead of production's 30s window. Retry is deliberately left out of
    // this pipeline - with it in, every "failing call" would actually be 4 attempts
    // (1 + 3 retries) before the breaker ever sees an outcome, which would either
    // trip it too early or make the throughput math harder to reason about; the
    // retry stage's own behavior is already covered by the two tests above.
    [Fact]
    public async Task CircuitBreaker_SustainedFailures_OpensThenHalfOpensAndRecovers()
    {
        var isHealthy = false;
        var handlerCalls = 0;
        var fakeHandler = new StubHttpMessageHandler(_ =>
        {
            handlerCalls++;
            return isHealthy
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var transitions = new List<string>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("entra-backchannel")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler)
            .AddResilienceHandler("default", builder =>
            {
                builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    // Polly enforces a 500ms floor on both SamplingDuration and
                    // BreakDuration - these are the shortest values it accepts, still
                    // two orders of magnitude below production's 30s window.
                    SamplingDuration = TimeSpan.FromMilliseconds(500),
                    MinimumThroughput = 4,
                    // Wider than SamplingDuration's floor on purpose: the
                    // "still-open" assertion right below needs the circuit to still
                    // be Open by the time it runs, and this test suite runs classes
                    // in parallel - a 500ms window left barely any margin against
                    // scheduling jitter from other tests (observed flaking under
                    // load). 3s keeps the whole test well under the production
                    // breaker's real duration while giving that assertion room to
                    // actually execute before the window closes.
                    BreakDuration = TimeSpan.FromSeconds(3),
                    OnOpened = _ => { transitions.Add("Opened"); return default; },
                    OnHalfOpened = _ => { transitions.Add("HalfOpened"); return default; },
                    OnClosed = _ => { transitions.Add("Closed"); return default; },
                });
                builder.AddTimeout(TimeSpan.FromSeconds(10));
            });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("entra-backchannel");

        // Sustained failure: MinimumThroughput (4) calls, all failing, inside the
        // 200ms sampling window - enough for FailureRatio=0.5 to trip the breaker.
        for (var i = 0; i < 4; i++)
        {
            await client.GetAsync("https://example.test/");
        }

        transitions.Should().ContainSingle(t => t == "Opened",
            "4 consecutive failures at FailureRatio=0.5 within the sampling window must trip the breaker exactly once");

        // While open, the breaker must short-circuit before ever calling the
        // handler - this is what protects the dependency from being hammered
        // further while it's known to be unhealthy.
        var callsBeforeOpenProbe = handlerCalls;
        var act = () => client.GetAsync("https://example.test/");
        await act.Should().ThrowAsync<BrokenCircuitException>();
        handlerCalls.Should().Be(callsBeforeOpenProbe, "an open circuit must reject the call before it ever reaches the handler");

        // Recovery: the dependency heals, and once BreakDuration elapses the
        // breaker lets exactly one probe through (HalfOpen) - which succeeds and
        // closes the circuit again.
        isHealthy = true;
        await Task.Delay(TimeSpan.FromSeconds(3.2));

        var response = await client.GetAsync("https://example.test/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        transitions.Should().ContainInOrder("Opened", "HalfOpened", "Closed");
    }

    // Day 22's fourth building block: the bulkhead. Uses permitLimit=2/queueLimit=1
    // instead of production's 10/5 purely so the test only needs 4 concurrent
    // calls (not 16) to prove "beyond permit+queue, fail fast" - the mechanism
    // under test (Polly.RateLimiting's concurrency limiter) is identical.
    [Fact]
    public async Task Bulkhead_ExceedsPermitAndQueue_RejectsExcessConcurrentCallsImmediately()
    {
        var entered = 0;
        var releaseGate = new SemaphoreSlim(0);
        var fakeHandler = new StubHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref entered);
            releaseGate.Wait();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("entra-backchannel")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler)
            .AddResilienceHandler("default", builder => builder.AddConcurrencyLimiter(permitLimit: 2, queueLimit: 1));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("entra-backchannel");

        // 2 permits + 1 queue slot = 3 calls that will eventually complete once
        // released. Only the first 2 actually enter the handler concurrently; the
        // 3rd sits queued behind them. Each dispatched via Task.Run rather than
        // called inline: StubHttpMessageHandler.SendAsync blocks synchronously
        // (releaseGate.Wait(), not WaitAsync()) up to its first real await, so
        // calling client.GetAsync directly on the test's own thread would block
        // that thread inside the very first call, before it ever got to start the
        // second or third - a self-inflicted deadlock, not a bulkhead property.
        var admitted = Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => client.GetAsync("https://example.test/")))
            .ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref entered) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        // A 4th concurrent call has no permit and no queue slot left - it must be
        // rejected immediately, not queued indefinitely or left to time out.
        var act = () => client.GetAsync("https://example.test/");
        await act.Should().ThrowAsync<RateLimiterRejectedException>(
            "permitLimit(2) + queueLimit(1) are already fully occupied by the 3 admitted calls");

        releaseGate.Release(3);
        var results = await Task.WhenAll(admitted);
        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);
        public void Dispose() { }

        private sealed class CapturingLogger(string category, List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add($"[{category}] {formatter(state, exception)}");
                }
            }
        }
    }
}
