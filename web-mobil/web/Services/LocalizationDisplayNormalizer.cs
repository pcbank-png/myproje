using System.Net;
using System.Text.RegularExpressions;

namespace NSYazilim.Web.Services
{
    public static partial class LocalizationDisplayNormalizer
    {
        public static string Normalize(string? value, string? languageCode)
        {
            var output = value ?? string.Empty;
            if (output.Length == 0)
                return output;

            // Some free translation providers can occasionally return XLIFF-like group
            // placeholders such as <g id="1">Compte</g>. Localization values are plain UI
            // text, so those provider-only wrappers must never reach the rendered page.
            // Decode only when a group wrapper is actually present, then unwrap it. The
            // normal HTML middleware will safely encode the resulting plain text again.
            var inspected = output.IndexOf("<g", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("</g>", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("&lt;g", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("&lt;/g", StringComparison.OrdinalIgnoreCase) >= 0
                ? WebUtility.HtmlDecode(output)
                : output;

            if (inspected.IndexOf("<g", StringComparison.OrdinalIgnoreCase) >= 0
                || inspected.IndexOf("</g>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                inspected = ProviderGroupTagRegex().Replace(inspected, string.Empty);
            }

            return inspected;
        }

        [GeneratedRegex(@"</?g(?:\s+[^>]*?)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ProviderGroupTagRegex();
    }
}
