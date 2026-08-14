using System.Diagnostics;

namespace QuotesApi;

// Custom spans (e.g. AuthService's password-verification step) go through
// this ActivitySource. It has to be registered via .AddSource(ServiceName) in
// the OpenTelemetry tracing configuration, or the SDK ignores it entirely and
// activities created here are silently dropped, never exported.
public static class Telemetry
{
    public const string ServiceName = "QuotesApi";
    public static readonly ActivitySource Source = new(ServiceName);
}
