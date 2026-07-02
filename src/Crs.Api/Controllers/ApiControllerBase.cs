using Crs.Api.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Crs.Api.Controllers;

/// <summary>
/// Base class for API controllers, exposing shared helpers for authenticated endpoints.
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// Resolves the authenticated user's id from the current principal.
    /// </summary>
    /// <param name="userId">The resolved user id when present.</param>
    /// <param name="unauthorized">
    /// An <see cref="UnauthorizedResult"/> to return when the id claim is missing or
    /// malformed; <c>null</c> when a valid id was resolved.
    /// </param>
    /// <returns><c>true</c> when a user id was resolved; otherwise <c>false</c>.</returns>
    protected bool TryGetUserId(out Guid userId, out IActionResult unauthorized)
    {
        var id = User.GetUserId();
        if (id is null)
        {
            userId = Guid.Empty;
            unauthorized = Unauthorized();
            return false;
        }

        userId = id.Value;
        unauthorized = null!;
        return true;
    }
}
