using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;

namespace QuotesApi;

// Marker only - no state. "Can the current user delete THIS quote" isn't
// claim-based (there's no claim that encodes "owns quote 42"), so it can't be
// expressed with RequireClaim; it has to compare the user against the resource.
public class SameOwnerRequirement : IAuthorizationRequirement { }

// Resource-based: the two-type-parameter AuthorizationHandler<TRequirement,
// TResource> form, not the single-parameter one - this needs to see the specific
// Quote being acted on, not just claims on the user. Injects ILogger via DI
// (constructor injection - handlers are resolved from the container like any
// other service) to record denied attempts as an audit trail.
public class SameOwnerAuthorizationHandler(ILogger<SameOwnerAuthorizationHandler> logger)
    : AuthorizationHandler<SameOwnerRequirement, Quote>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SameOwnerRequirement requirement, Quote resource)
    {
        var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (userIdClaim is not null
            && int.TryParse(userIdClaim, out var userId)
            && userId == resource.CreatedByUserId)
        {
            context.Succeed(requirement);
        }
        else
        {
            logger.LogWarning(
                "User {UserId} was denied SameOwner access to Quote {QuoteId} (owned by user {OwnerId}).",
                userIdClaim, resource.Id, resource.CreatedByUserId);
        }

        return Task.CompletedTask;
    }
}
