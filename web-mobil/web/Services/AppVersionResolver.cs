using System.Text.RegularExpressions;

namespace NSYazilim.Web.Services;

/// <summary>
/// NSX masaustu uygulamalarindan gelen surum bilgisini tek bir yerde cozer.
/// Oncelik: acik parametre/body -> NSX surum header'lari -> query -> NSX User-Agent.
/// Browser surumleri (Chrome/Edge vb.) kesinlikle program surumu olarak kabul edilmez.
/// </summary>
public static class AppVersionResolver
{
    private static readonly string[] HeaderNames =
    {
        "X-NSX-App-Version",
        "X-NSX-Version",
        "X-App-Version",
        "X-Client-Version",
        "X-Program-Version"
    };

    private static readonly string[] QueryNames =
    {
        "appVersion",
        "clientVersion",
        "programVersion",
        "currentVersion",
        "version"
    };

    private static readonly Regex StrictVersionRegex = new(
        @"^\d+(?:\.\d+){1,3}(?:[-+][A-Za-z0-9._-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NsxUserAgentRegex = new(
        @"(?i)(?:^|[\s(;])NSX[A-Z0-9._ -]{2,}?(?:/|\s+v?)(?<version>\d+(?:\.\d+){1,3}(?:[-+][A-Z0-9._-]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Resolve(HttpContext context, params string?[] explicitCandidates)
    {
        foreach (var candidate in explicitCandidates)
        {
            var normalized = Normalize(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        foreach (var headerName in HeaderNames)
        {
            var normalized = Normalize(context.Request.Headers[headerName].FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        foreach (var queryName in QueryNames)
        {
            var normalized = Normalize(context.Request.Query[queryName].FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
            return string.Empty;

        var match = NsxUserAgentRegex.Match(userAgent);
        return match.Success ? Normalize(match.Groups["version"].Value) : string.Empty;
    }

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text.Length > 1 && char.IsDigit(text[1]) ? text[1..] : text;

        if (text.Length == 0 || text.Length > 50)
            return string.Empty;

        return StrictVersionRegex.IsMatch(text) ? text : string.Empty;
    }

    public static string? ReadLicenseKey(HttpContext context, string? explicitValue = null)
    {
        var value = FirstNonEmpty(
            explicitValue,
            context.Request.Headers["X-NSX-License-Key"].FirstOrDefault(),
            context.Request.Headers["X-License-Key"].FirstOrDefault(),
            context.Request.Query["licenseKey"].FirstOrDefault(),
            context.Request.Query["key"].FirstOrDefault());

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    public static string? ReadMachineId(HttpContext context, string? explicitValue = null)
    {
        var value = FirstNonEmpty(
            explicitValue,
            context.Request.Headers["X-NSX-Machine-Id"].FirstOrDefault(),
            context.Request.Headers["X-Machine-Id"].FirstOrDefault(),
            context.Request.Query["machineId"].FirstOrDefault(),
            context.Request.Query["deviceId"].FirstOrDefault());

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}
