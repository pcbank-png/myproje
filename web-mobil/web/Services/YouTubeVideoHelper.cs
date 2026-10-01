using System.Text.RegularExpressions;

namespace NSYazilim.Web.Services
{
    public static partial class YouTubeVideoHelper
    {
        [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
        private static partial Regex VideoIdRegex();

        public static bool TryExtractVideoId(string? input, out string videoId)
        {
            videoId = string.Empty;
            var raw = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (VideoIdRegex().IsMatch(raw))
            {
                videoId = raw;
                return true;
            }

            if (!raw.Contains("://", StringComparison.Ordinal))
                raw = "https://" + raw.TrimStart('/');

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return false;

            var host = uri.Host.ToLowerInvariant();
            var pathParts = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string? candidate = null;

            if (host is "youtu.be" or "www.youtu.be")
            {
                candidate = pathParts.FirstOrDefault();
            }
            else if (host.EndsWith("youtube.com", StringComparison.Ordinal))
            {
                if (pathParts.Length >= 2 &&
                    (pathParts[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
                     || pathParts[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
                     || pathParts[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
                {
                    candidate = pathParts[1];
                }
                else
                {
                    candidate = ParseQuery(uri.Query)
                        .FirstOrDefault(x => x.Key.Equals("v", StringComparison.OrdinalIgnoreCase))
                        .Value;
                }
            }

            candidate = (candidate ?? string.Empty).Trim();
            if (!VideoIdRegex().IsMatch(candidate))
                return false;

            videoId = candidate;
            return true;
        }

        public static string NormalizeWatchUrl(string videoId)
            => $"https://www.youtube.com/watch?v={videoId}";

        public static string GetEmbedUrl(string videoId)
            => $"https://www.youtube-nocookie.com/embed/{videoId}?rel=0&modestbranding=1";

        public static string GetThumbnailUrl(string videoId)
            => $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

        public static string CreateSlug(string? text, string fallback = "video")
        {
            var value = (text ?? string.Empty).Trim().ToLowerInvariant()
                .Replace("ç", "c")
                .Replace("ğ", "g")
                .Replace("ı", "i")
                .Replace("ö", "o")
                .Replace("ş", "s")
                .Replace("ü", "u");

            var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
            var slug = new string(chars);
            while (slug.Contains("--", StringComparison.Ordinal))
                slug = slug.Replace("--", "-", StringComparison.Ordinal);

            slug = slug.Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
        }

        private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
        {
            var raw = (query ?? string.Empty).TrimStart('?');
            if (string.IsNullOrWhiteSpace(raw))
                yield break;

            foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split('=', 2);
                var key = Uri.UnescapeDataString(pieces[0].Replace('+', ' '));
                var value = pieces.Length > 1
                    ? Uri.UnescapeDataString(pieces[1].Replace('+', ' '))
                    : string.Empty;

                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }
}
