using FluentAssertions;
using QuotesApi;
using Xunit;

namespace Quotes.Tests.Unit;

public class CollectionTests
{
    [Fact]
    public void Create_ValidName_ReturnsCollectionWithGivenNameAndOwner()
    {
        var collection = Collection.Create("My Favorites", ownerId: 7);

        collection.Name.Should().Be("My Favorites");
        collection.OwnerId.Should().Be(7);
        collection.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public void Create_NameTooShort_ThrowsDomainException(string name)
    {
        var act = () => Collection.Create(name, ownerId: 1);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_NameOverMaxLength_ThrowsDomainException()
    {
        var tooLongName = new string('a', Collection.MaxNameLength + 1);

        var act = () => Collection.Create(tooLongName, ownerId: 1);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void AddItem_NewQuoteId_AddsItemWithGivenTimestamp()
    {
        var collection = Collection.Create("Favorites", ownerId: 1);
        var addedAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        collection.AddItem(quoteId: 42, addedAt);

        collection.Items.Should().ContainSingle(i => i.QuoteId == 42 && i.AddedAt == addedAt);
    }

    [Fact]
    public void AddItem_DuplicateQuoteId_ThrowsDomainException()
    {
        var collection = Collection.Create("Favorites", ownerId: 1);
        collection.AddItem(42, DateTimeOffset.UtcNow);

        var act = () => collection.AddItem(42, DateTimeOffset.UtcNow);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void AddItem_AtMaxItems_ThrowsDomainException()
    {
        var collection = Collection.Create("Favorites", ownerId: 1);
        for (var quoteId = 1; quoteId <= Collection.MaxItems; quoteId++)
        {
            collection.AddItem(quoteId, DateTimeOffset.UtcNow);
        }

        var act = () => collection.AddItem(Collection.MaxItems + 1, DateTimeOffset.UtcNow);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void RemoveItem_ExistingQuoteId_RemovesIt()
    {
        var collection = Collection.Create("Favorites", ownerId: 1);
        collection.AddItem(42, DateTimeOffset.UtcNow);

        collection.RemoveItem(42);

        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void RemoveItem_NonExistentQuoteId_ThrowsDomainException()
    {
        var collection = Collection.Create("Favorites", ownerId: 1);

        var act = () => collection.RemoveItem(999);

        act.Should().Throw<DomainException>();
    }
}
