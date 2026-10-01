using NSYazilim.Web.TeknikServisCloud.Models;

namespace NSYazilim.Web.TeknikServisCloud.Services;

public sealed class TenantAuthService
{
    private readonly TeknikServisMySqlStore _store;

    public TenantAuthService(TeknikServisMySqlStore store)
    {
        _store = store;
    }

    public TenantContext? TryResolve(HttpContext httpContext)
    {
        var firmaId = ReadHeaderOrQuery(httpContext, "X-NSX-FirmaId", "firmaId");
        var apiToken = ReadHeaderOrQuery(httpContext, "X-NSX-ApiToken", "apiToken");
        return _store.ValidateTenant(firmaId, apiToken);
    }

    public static string ReadHeaderOrQuery(HttpContext httpContext, string headerName, string queryName)
    {
        if (httpContext.Request.Headers.TryGetValue(headerName, out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            return headerValue.ToString().Trim();
        }

        if (httpContext.Request.Query.TryGetValue(queryName, out var queryValue)
            && !string.IsNullOrWhiteSpace(queryValue.ToString()))
        {
            return queryValue.ToString().Trim();
        }

        return string.Empty;
    }
}
