namespace QuotesApi.Models;

public class Collection
{
    public const int MinNameLength = 3;
    public const int MaxNameLength = 80;
    public const int MaxItems = 50;

    private readonly List<CollectionItem> _items = new();

    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int OwnerId { get; private set; }
    public IReadOnlyList<CollectionItem> Items => _items.AsReadOnly();

    // EF Core materialization only. Application code must go through Create().
    private Collection() { }

    public static Collection Create(string name, int ownerId)
    {
        var collection = new Collection { OwnerId = ownerId };
        collection.Rename(name);
        return collection;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < MinNameLength || name.Length > MaxNameLength)
        {
            throw new DomainException(
                $"Collection name must be between {MinNameLength} and {MaxNameLength} characters.");
        }

        Name = name;
    }

    // Takes the current time as a parameter rather than reading IClock itself:
    // domain entities shouldn't reach into DI/ambient services. The caller (which
    // does have IClock) resolves "now" and hands it in - this keeps AddItem trivially
    // unit-testable with a fixed DateTimeOffset and no clock abstraction in sight.
    public void AddItem(int quoteId, DateTimeOffset addedAt)
    {
        if (_items.Any(i => i.QuoteId == quoteId))
        {
            throw new DomainException($"Quote {quoteId} is already in this collection.");
        }

        if (_items.Count >= MaxItems)
        {
            throw new DomainException($"A collection cannot contain more than {MaxItems} items.");
        }

        _items.Add(new CollectionItem(quoteId, addedAt));
    }

    public void RemoveItem(int quoteId)
    {
        var item = _items.FirstOrDefault(i => i.QuoteId == quoteId);
        if (item is null)
        {
            throw new DomainException($"Quote {quoteId} is not in this collection.");
        }

        _items.Remove(item);
    }
}
