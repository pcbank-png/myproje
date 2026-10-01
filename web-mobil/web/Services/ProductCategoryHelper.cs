using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public static class ProductCategoryHelper
    {
        public sealed record Option(string Key, string Label, bool IsActive = true);

        private static readonly Option[] BuiltInOptions =
        {
            new("organizasyon", "Düğün & Organizasyon"),
            new("cari", "Cari & Finans"),
            new("servis", "Servis Yönetimi"),
            new("otomotiv", "Otomotiv"),
            new("klinik", "Klinik & Randevu"),
            new("performans", "PC Performans")
        };

        private static IReadOnlyList<Option> _allOptions = BuiltInOptions;
        private static IReadOnlyList<Option> _options = BuiltInOptions;

        public static IReadOnlyList<Option> DefaultOptions => BuiltInOptions;
        public static IReadOnlyList<Option> Options => _options;

        public static void Configure(IEnumerable<Option> options)
        {
            var configured = options
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Label))
                .Select(x => new Option(CreateKey(x.Key), x.Label.Trim(), x.IsActive))
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            // Çalışan uygulamada DB kataloğu tek otoritedir. Admin tüm kategorileri silerse
            // sabit listeyi geri yüklemeyiz; aksi halde silinen kategoriler yeniden görünür.
            _allOptions = configured;
            _options = _allOptions.Where(x => x.IsActive).ToList();
        }

        public static string GetKey(Product product)
        {
            return Normalize(product.Category, product.Name, product.Slug, product.ProductCode);
        }

        public static string GetLabel(Product product)
        {
            return GetLabel(GetKey(product));
        }

        public static string GetLabel(string? key)
        {
            var normalized = NormalizeKnownKey(key);
            if (normalized != null)
            {
                var option = _allOptions.FirstOrDefault(x => string.Equals(x.Key, normalized, StringComparison.OrdinalIgnoreCase));
                if (option != null)
                    return option.Label;
            }

            return "Kategorisiz";
        }

        public static string Normalize(string? category, params string?[] inferenceValues)
        {
            var known = NormalizeKnownKey(category);
            if (known != null)
                return known;

            var value = string.Join(' ', inferenceValues.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
            if ((value.Contains("düğün") || value.Contains("dugun") || value.Contains("organizasyon")) && IsConfigured("organizasyon")) return "organizasyon";
            if ((value.Contains("klinik") || value.Contains("randevu") || value.Contains("hasta")) && IsConfigured("klinik")) return "klinik";
            if ((value.Contains("teknik servis") || value.Contains("teknik-servis") || value.Contains("teknikservis")) && IsConfigured("servis")) return "servis";
            if ((value.Contains("veresiye") || value.Contains("cari") || value.Contains("finans")) && IsConfigured("cari")) return "cari";
            if ((value.Contains("oto") || value.Contains("servispro live") || value.Contains("servispro-live")) && IsConfigured("otomotiv")) return "otomotiv";
            if ((value.Contains("turbo") || value.Contains("performans") || value.Contains("fps")) && IsConfigured("performans")) return "performans";
            return string.Empty;
        }

        public static string CreateKey(string? value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant()
                .Replace('ı', 'i')
                .Replace('ğ', 'g')
                .Replace('ü', 'u')
                .Replace('ş', 's')
                .Replace('ö', 'o')
                .Replace('ç', 'c');

            var normalized = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    builder.Append(character);
            }

            var key = Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), "[^a-z0-9]+", "-").Trim('-');
            return key.Length <= 40 ? key : key[..40].TrimEnd('-');
        }

        private static bool IsConfigured(string key)
            => _allOptions.Any(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));

        private static string? NormalizeKnownKey(string? category)
        {
            var value = CreateKey(category);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (IsConfigured(value))
                return value;

            var alias = value switch
            {
                "dugun" => "organizasyon",
                "finans" or "cari-finans" => "cari",
                "servis-yonetimi" => "servis",
                "oto" => "otomotiv",
                "saglik" or "randevu" => "klinik",
                "pc" or "pc-performans" => "performans",
                _ => null
            };

            return alias != null && IsConfigured(alias) ? alias : null;
        }
    }
}
