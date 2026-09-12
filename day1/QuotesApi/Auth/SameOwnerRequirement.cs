using Microsoft.AspNetCore.Authorization;

namespace QuotesApi.Auth;

// Marker only - no state. "Can the current user delete THIS quote" isn't
// claim-based (there's no claim that encodes "owns quote 42"), so it can't be
// expressed with RequireClaim; it has to compare the user against the resource.
public class SameOwnerRequirement : IAuthorizationRequirement { }
