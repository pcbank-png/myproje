using System.Security.Claims;

namespace NSYazilim.Web.Services;

public static class RemoteSupportAuthorization
{
    public static string? OperatorId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;

    public static bool OwnsRequest(string owner, ClaimsPrincipal user) =>
        !string.IsNullOrWhiteSpace(owner) && owner == OperatorId(user);

    public static bool IsSameOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin.ToString(),UriKind.Absolute,out var source) &&
        string.Equals(source.Authority,request.Host.Value,StringComparison.OrdinalIgnoreCase) &&
        string.Equals(source.Scheme,request.Scheme,StringComparison.OrdinalIgnoreCase);
}
