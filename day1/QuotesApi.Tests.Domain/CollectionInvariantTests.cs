using FluentAssertions;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace QuotesApi.Tests.Domain;

public class CollectionInvariantTests
{
    [Fact]
    public void EmptyName_Throws()
    {
        var act = () => Collection.Create("", ownerId: 1);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void NameOver80Chars_Throws()
    {
        var act = () => Collection.Create(new string('a', 81), ownerId: 1);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Adding51stItem_Throws()
    {
        var collection = Collection.Create("My Collection", ownerId: 1);
        for (var quoteId = 1; quoteId <= 50; quoteId++)
        {
            collection.AddItem(quoteId, DateTimeOffset.UtcNow);
        }

        var act = () => collection.AddItem(51, DateTimeOffset.UtcNow);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void DuplicateQuoteId_Throws()
    {
        var collection = Collection.Create("My Collection", ownerId: 1);
        collection.AddItem(quoteId: 1, DateTimeOffset.UtcNow);

        var act = () => collection.AddItem(quoteId: 1, DateTimeOffset.UtcNow);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void RemovingNonExistentItem_Throws()
    {
        var collection = Collection.Create("My Collection", ownerId: 1);

        var act = () => collection.RemoveItem(quoteId: 999);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void AddingThenRemoving_LeavesZeroItems()
    {
        var collection = Collection.Create("My Collection", ownerId: 1);
        collection.AddItem(quoteId: 1, DateTimeOffset.UtcNow);

        collection.RemoveItem(quoteId: 1);

        collection.Items.Should().BeEmpty();
    }
}
