using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NSYazilim.Web.Services
{
    public static class ProductContentSanitizer
    {
        private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "br", "strong", "b", "em", "i", "u", "s",
            "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "a",
            "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td"
        };

        private static readonly HashSet<string> ContentBlockingTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "iframe", "object", "embed", "form", "svg", "math"
        };

        private static readonly Regex TagNamePattern = new("^<\\s*(/?)\\s*([a-zA-Z0-9]+)", RegexOptions.Compiled);
        private static readonly Regex HrefPattern = new("\\bhref\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Sanitize(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            var source = html.Trim();
            var result = new StringBuilder(source.Length);
            var index = 0;

            while (index < source.Length)
            {
                var tagStart = source.IndexOf('<', index);
                if (tagStart < 0)
                {
                    result.Append(source, index, source.Length - index);
                    break;
                }

                result.Append(source, index, tagStart - index);
                var tagEnd = FindTagEnd(source, tagStart + 1);
                if (tagEnd < 0)
                {
                    result.Append("&lt;");
                    index = tagStart + 1;
                    continue;
                }

                var token = source[tagStart..(tagEnd + 1)];
                if (token.StartsWith("<!--", StringComparison.Ordinal))
                {
                    var commentEnd = source.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                    index = commentEnd >= 0 ? commentEnd + 3 : tagEnd + 1;
                    continue;
                }

                var match = TagNamePattern.Match(token);
                if (!match.Success)
                {
                    index = tagEnd + 1;
                    continue;
                }

                var closing = match.Groups[1].Value.Length > 0;
                var tagName = match.Groups[2].Value.ToLowerInvariant();

                if (!closing && ContentBlockingTags.Contains(tagName))
                {
                    var closeStart = source.IndexOf($"</{tagName}", tagEnd + 1, StringComparison.OrdinalIgnoreCase);
                    if (closeStart >= 0)
                    {
                        var closeEnd = FindTagEnd(source, closeStart + 2);
                        index = closeEnd >= 0 ? closeEnd + 1 : source.Length;
                        continue;
                    }
                }

                if (AllowedTags.Contains(tagName))
                {
                    if (closing)
                    {
                        if (tagName != "br")
                            result.Append("</").Append(tagName).Append('>');
                    }
                    else if (tagName == "a")
                    {
                        var href = ExtractSafeHref(token);
                        result.Append(href == null
                            ? "<a>"
                            : $"<a href=\"{WebUtility.HtmlEncode(href)}\" rel=\"noopener noreferrer\">");
                    }
                    else
                    {
                        result.Append('<').Append(tagName).Append('>');
                    }
                }

                index = tagEnd + 1;
            }

            return result.ToString().Trim();
        }

        public static string ToPlainText(string? html, int maxLength = 0)
        {
            var sanitized = NormalizeForEditor(html);
            var text = Regex.Replace(sanitized, "<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, "\\s+", " ").Trim();

            if (maxLength > 3 && text.Length > maxLength)
                return text[..(maxLength - 3)].TrimEnd() + "...";

            return text;
        }

        public static string NormalizeForEditor(string? content)
        {
            var sanitized = Sanitize(content);
            if (string.IsNullOrWhiteSpace(sanitized))
                return string.Empty;

            if (Regex.IsMatch(sanitized, "</?(?:p|h2|h3|h4|ul|ol|li|blockquote|strong|b|em|i|u|s|a|br|table|caption|thead|tbody|tfoot|tr|th|td)\\b", RegexOptions.IgnoreCase))
                return sanitized;

            var plain = WebUtility.HtmlDecode(sanitized);
            var encoded = WebUtility.HtmlEncode(plain);
            encoded = Regex.Replace(encoded, "\\*\\*(.+?)\\*\\*", "<strong>$1</strong>", RegexOptions.Singleline);

            var paragraphs = Regex.Split(encoded.Trim(), "(?:\\r?\\n){2,}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => $"<p>{Regex.Replace(x.Trim(), "\\r?\\n", "<br>")}</p>");

            return string.Join(Environment.NewLine, paragraphs);
        }

        private static int FindTagEnd(string source, int start)
        {
            char quote = '\0';
            for (var i = start; i < source.Length; i++)
            {
                var ch = source[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                        quote = '\0';
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '>')
                    return i;
            }

            return -1;
        }

        private static string? ExtractSafeHref(string token)
        {
            var match = HrefPattern.Match(token);
            if (!match.Success)
                return null;

            var href = WebUtility.HtmlDecode(match.Groups.Cast<Group>().Skip(1).FirstOrDefault(x => x.Success)?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(href))
                return null;

            if (href.StartsWith('/') || href.StartsWith('#') || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return href;

            return null;
        }
    }
}
