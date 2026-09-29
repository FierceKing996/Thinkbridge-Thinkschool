using Microsoft.AspNetCore.Mvc;

namespace QuotesApi.Controllers;

public abstract class ApiControllerBase : ControllerBase
{
    // Mirrors minimal-API Results.ValidationProblem(...) byte-for-byte: HTTP 400,
    // application/problem+json, the RFC 9110 "type" URL and the standard title,
    // and a single-entry "errors" dictionary keyed by the offending field.
    //hello
    protected ObjectResult ValidationProblemResponse(string key, string message)
    {
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]> { [key] = new[] { message } })
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        };

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }
}
