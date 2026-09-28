using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Controllers;

[ApiController]
[Asp.Versioning.ApiVersion("1.0")]
[Route("api/collections")]
public class CollectionsController : ApiControllerBase
{
    private readonly ICollectionRepository _collections;
    private readonly ICollectionQueries _collectionQueries;
    private readonly IAddCollectionItemCommandHandler _addItemHandler;
    private readonly ITextNormalizer _normalizer;

    public CollectionsController(
        ICollectionRepository collections,
        ICollectionQueries collectionQueries,
        IAddCollectionItemCommandHandler addItemHandler,
        ITextNormalizer normalizer)
    {
        _collections = collections;
        _collectionQueries = collectionQueries;
        _addItemHandler = addItemHandler;
        _normalizer = normalizer;
    }

    // GET /api/collections/{id} - read model, see ICollectionQueries
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCollection(int id, CancellationToken ct)
    {
        var detail = await _collectionQueries.GetDetailAsync(id, ct);
        return detail is not null ? Ok(detail) : NotFound();
    }

    // POST /api/collections
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateCollection([FromBody] CreateCollectionRequest req, CancellationToken ct)
    {
        Collection collection;
        try
        {
            // All invariant checking (name length, etc.) lives on the aggregate itself.
            collection = Collection.Create(_normalizer.Trim(req.Name), req.OwnerId);
        }
        catch (DomainException ex)
        {
            return ValidationProblemResponse(nameof(req.Name), ex.Message);
        }

        var created = await _collections.AddAsync(collection, ct);
        return Created($"/api/collections/{created.Id}", created);
    }

    // POST /api/collections/{id}/items - write model, see AddCollectionItemCommandHandler
    [HttpPost("{id:int}/items")]
    [Authorize]
    public async Task<IActionResult> AddItem(int id, [FromBody] AddCollectionItemRequest req, CancellationToken ct)
    {
        var result = await _addItemHandler.HandleAsync(new AddCollectionItemCommand(id, req.QuoteId), ct);
        if (result.NotFound) return NotFound();
        if (!result.Succeeded)
        {
            return ValidationProblemResponse(nameof(req.QuoteId), result.Error!);
        }

        return Ok(result.Collection);
    }

    // DELETE /api/collections/{id}/items/{quoteId}
    [HttpDelete("{id:int}/items/{quoteId:int}")]
    [Authorize]
    public async Task<IActionResult> RemoveItem(int id, int quoteId, CancellationToken ct)
    {
        var collection = await _collections.GetByIdAsync(id, ct);
        if (collection is null) return NotFound();

        try
        {
            collection.RemoveItem(quoteId);
        }
        catch (DomainException ex)
        {
            return ValidationProblemResponse(nameof(quoteId), ex.Message);
        }

        await _collections.UpdateAsync(collection, ct);
        return Ok(collection);
    }
}
