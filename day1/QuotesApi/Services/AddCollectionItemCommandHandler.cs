using QuotesApi.Models;
using QuotesApi.Repositories;

namespace QuotesApi.Services;

// ---- Write model: command + handler for "add a quote to a collection" ----
// Normalized (FK-shaped: CollectionId + QuoteId, no denormalized author/text
// along for the ride) and validated - every mutation goes through the
// Collection aggregate, so duplicate-quote and max-items-per-collection
// invariants can't be bypassed.
public record AddCollectionItemCommand(int CollectionId, int QuoteId);

public class AddCollectionItemResult
{
    public Collection? Collection { get; }
    public bool NotFound { get; }
    public string? Error { get; }
    public bool Succeeded => Collection is not null;

    private AddCollectionItemResult(Collection? collection, bool notFound, string? error)
    {
        Collection = collection;
        NotFound = notFound;
        Error = error;
    }

    public static AddCollectionItemResult Success(Collection collection) => new(collection, false, null);
    public static AddCollectionItemResult CollectionNotFound() => new(null, true, null);
    public static AddCollectionItemResult Fail(string error) => new(null, false, error);
}

public interface IAddCollectionItemCommandHandler
{
    Task<AddCollectionItemResult> HandleAsync(AddCollectionItemCommand command, CancellationToken ct);
}

public class AddCollectionItemCommandHandler(ICollectionRepository repo, IClock clock) : IAddCollectionItemCommandHandler
{
    public async Task<AddCollectionItemResult> HandleAsync(AddCollectionItemCommand command, CancellationToken ct)
    {
        var collection = await repo.GetByIdAsync(command.CollectionId, ct);
        if (collection is null) return AddCollectionItemResult.CollectionNotFound();

        try
        {
            collection.AddItem(command.QuoteId, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return AddCollectionItemResult.Fail(ex.Message);
        }

        await repo.UpdateAsync(collection, ct);
        return AddCollectionItemResult.Success(collection);
    }
}
