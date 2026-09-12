using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
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
