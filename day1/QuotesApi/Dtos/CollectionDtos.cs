namespace QuotesApi.Dtos;

public record CreateCollectionRequest(string Name, int OwnerId);
public record AddCollectionItemRequest(int QuoteId);
public record CollectionItemDetail(int QuoteId, string Author, string Text, DateTimeOffset AddedAt);
public record CollectionDetailResponse(int Id, string Name, int OwnerId, IReadOnlyList<CollectionItemDetail> Items);
