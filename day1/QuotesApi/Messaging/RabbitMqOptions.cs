namespace QuotesApi.Messaging;

// Bound from the "RabbitMq" config section. HostName is intentionally the
// on/off switch: unset (the default, see appsettings.json), the publisher and
// both consumers are never registered at all - same "opt in only when
// configured" pattern Program.cs already uses for Key Vault and App Insights,
// so `dotnet run` and the test suite work with zero broker running.
public class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    // Topic exchange every publish goes to; "quote.created" etc. are routing keys
    // on this one exchange, not separate exchanges - that's what makes it a topic
    // (fan-out-by-pattern), the free-tier stand-in for a Service Bus topic.
    public string Exchange { get; set; } = "quotes.events";

    // Two independent subscriptions to the same exchange - the Service Bus
    // "topic with two subscriptions" from the Day 19 brief.
    public string SearchIndexQueue { get; set; } = "quotes.search-index";
    public string AuditLogQueue { get; set; } = "quotes.audit-log";

    // How many competing consumers read the search-index queue concurrently.
    public int SearchIndexConsumerCount { get; set; } = 3;

    public string DeadLetterExchange { get; set; } = "quotes.events.dlx";
    public string DeadLetterQueue { get; set; } = "quotes.events.dlq";
}
