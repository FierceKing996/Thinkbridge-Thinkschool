namespace QuotesApi.Messaging;

// The routing key ("quote.created") and payload shape for the one domain event
// this app currently publishes. Both the outbox relay (serializing into
// OutboxMessage.Payload) and the two consumers (deserializing it back out)
// share this type, so there's exactly one place the wire shape is defined.
public static class QuoteEvents
{
    public const string QuoteCreatedType = "quote.created";
}

public record QuoteCreatedPayload(int QuoteId, string Author, string Text, int CreatedByUserId);
