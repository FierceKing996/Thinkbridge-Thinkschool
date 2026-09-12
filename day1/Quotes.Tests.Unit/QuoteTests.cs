using FluentAssertions;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Unit;

public class QuoteTests
{
    [Fact]
    public void Create_ValidAuthorAndText_ReturnsSuccessWithQuote()
    {
        var result = Quote.Create("Ada Lovelace", "The Analytical Engine weaves algebra.", createdByUserId: 1);

        result.Succeeded.Should().BeTrue();
        result.Quote.Should().NotBeNull();
        result.Quote!.Author.Should().Be("Ada Lovelace");
        result.Quote.Text.Should().Be("The Analytical Engine weaves algebra.");
        result.Quote.CreatedByUserId.Should().Be(1);
        result.Quote.IsDeleted.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankAuthor_ReturnsFailure(string author)
    {
        var result = Quote.Create(author, "Valid text", createdByUserId: 1);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Author");
    }

    [Fact]
    public void Create_AuthorOverMaxLength_ReturnsFailure()
    {
        var tooLongAuthor = new string('a', Quote.MaxAuthorLength + 1);

        var result = Quote.Create(tooLongAuthor, "Valid text", createdByUserId: 1);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Author");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankText_ReturnsFailure(string text)
    {
        var result = Quote.Create("Valid Author", text, createdByUserId: 1);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Text");
    }

    [Fact]
    public void Create_TextOverMaxLength_ReturnsFailure()
    {
        var tooLongText = new string('a', Quote.MaxTextLength + 1);

        var result = Quote.Create("Valid Author", tooLongText, createdByUserId: 1);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Text");
    }

    [Fact]
    public void Delete_ValidQuote_SetsIsDeletedTrue()
    {
        var quote = Quote.Create("Author", "Text", createdByUserId: 1).Quote!;

        quote.Delete();

        quote.IsDeleted.Should().BeTrue();
    }
}
