# Day 22 — Resilience with Polly

The outbound dependency: Entra's OIDC metadata/JWKS endpoint, called by the
`entra-backchannel` `HttpClient` behind `JwtBearerOptions.Backchannel` (see
`Auth/AuthenticationExtensions.cs` — this dependency and its resilience pipeline
already existed from the Day-N Entra exercise; Day 22 adds the missing fourth
building block, a bulkhead, and proves the circuit breaker's actual state machine).

## The resilience pipeline

```csharp
internal static void ConfigureResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
{
    // Bulkhead: caps total concurrent in-flight calls (including their retries)
    // into the pipeline below it.
    builder.AddConcurrencyLimiter(permitLimit: 10, queueLimit: 5);

    builder.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true
    });

    builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(30),
        MinimumThroughput = 4
    });

    builder.AddTimeout(TimeSpan.FromSeconds(10));
}
```

Wired via:

```csharp
services.AddHttpClient(EntraBackchannelClientName)
    .AddResilienceHandler("default", ConfigureResilience);
```

**Order, outermost first:** bulkhead → retry → circuit breaker → per-attempt timeout.
The bulkhead sits outside retry on purpose — it's meant to cap *all* concurrent
usage of the dependency (a retry storm included), not just the first attempt of
each logical call.

**Retry is idempotent-only by construction, not by a flag:** this pipeline is
attached exclusively to the Entra backchannel `HttpClient`, which only ever does
`GET`s (OIDC discovery + JWKS) — there's no unsafe write path sharing this pipeline
to retry accidentally.

## Proof: the circuit opens under sustained failure, then recovers

`Quotes.Tests.Unit/EntraBackchannelResilienceTests.cs`,
`CircuitBreaker_SustainedFailures_OpensThenHalfOpensAndRecovers` — same three
stages (breaker, timeout; retry deliberately excluded so failing calls aren't
each turned into 4 attempts, which would muddy the throughput math), with
`SamplingDuration` at Polly's 500ms floor and `BreakDuration` at 3s (wider than
its own 500ms floor deliberately - the "still open" assertion below needs
headroom against scheduling jitter when this suite runs its test classes in
parallel, and even 3s is still an order of magnitude below production's 30s
window) instead of production's 30s/default:

```csharp
builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
{
    FailureRatio = 0.5,
    SamplingDuration = TimeSpan.FromMilliseconds(500),
    MinimumThroughput = 4,
    BreakDuration = TimeSpan.FromSeconds(3),
    OnOpened = _ => { transitions.Add("Opened"); return default; },
    OnHalfOpened = _ => { transitions.Add("HalfOpened"); return default; },
    OnClosed = _ => { transitions.Add("Closed"); return default; },
});
```

```csharp
// Sustained failure: 4 failing calls inside the sampling window trips it.
for (var i = 0; i < 4; i++) await client.GetAsync("https://example.test/");
transitions.Should().ContainSingle(t => t == "Opened");

// While open: rejected before ever reaching the handler.
var callsBeforeOpenProbe = handlerCalls;
await act.Should().ThrowAsync<BrokenCircuitException>();
handlerCalls.Should().Be(callsBeforeOpenProbe);

// Recovery: dependency heals, BreakDuration elapses, one probe (HalfOpen) succeeds.
isHealthy = true;
await Task.Delay(TimeSpan.FromSeconds(3.2));
var response = await client.GetAsync("https://example.test/");
response.StatusCode.Should().Be(HttpStatusCode.OK);

transitions.Should().ContainInOrder("Opened", "HalfOpened", "Closed");
```

Test run:

```
Passed Quotes.Tests.Unit.EntraBackchannelResilienceTests.Backchannel_TransientFailures_RetriesThenSucceeds [3 s]
Passed Quotes.Tests.Unit.EntraBackchannelResilienceTests.CircuitBreaker_SustainedFailures_OpensThenHalfOpensAndRecovers [3 s]
Passed Quotes.Tests.Unit.EntraBackchannelResilienceTests.Backchannel_PersistentFailures_ExhaustsRetriesAndThrows [6 s]
Passed Quotes.Tests.Unit.EntraBackchannelResilienceTests.Bulkhead_ExceedsPermitAndQueue_RejectsExcessConcurrentCallsImmediately [28 ms]

Total tests: 4, Passed: 4 (stable across repeated runs)
```

The `transitions` list captured, in order: **`Opened` → `HalfOpened` → `Closed`** —
the exact state machine the exercise asks to prove, driven entirely by Polly's own
circuit breaker (`OnOpened`/`OnHalfOpened`/`OnClosed` callbacks), not inferred from
timing or logs.

## Proof: the bulkhead

`Bulkhead_ExceedsPermitAndQueue_RejectsExcessConcurrentCallsImmediately` uses
`permitLimit: 2, queueLimit: 1` (smaller than production's 10/5 purely so the test
only needs 4 concurrent calls, not 16): 3 concurrent calls are admitted (2 running,
1 queued); a 4th is rejected immediately with `RateLimiterRejectedException` rather
than queuing indefinitely or timing out — proving the pipeline fails fast once
both the permit pool and the queue are full.
