#nullable enable

using System.Security.Claims;

namespace ShortenerTools.Admin;

public static class AdministratorAccess
{
    public static bool IsAllowed(ClaimsPrincipal? principal, string tenantId, string objectId) =>
        principal?.Identity?.IsAuthenticated == true
        && Guid.TryParse(tenantId, out var expectedTenant)
        && Guid.TryParse(objectId, out var expectedObject)
        && Guid.TryParse(principal.FindFirst("tid")?.Value, out var actualTenant)
        && Guid.TryParse(principal.FindFirst("oid")?.Value, out var actualObject)
        && actualTenant == expectedTenant
        && actualObject == expectedObject;
}
