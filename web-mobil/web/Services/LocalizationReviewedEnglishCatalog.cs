namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Compile-time reviewed English lookup used to unblock durable queue jobs when the
    /// free providers keep returning empty/Turkish results, and when seed English already
    /// exists in memory but was never persisted to LocalizationTranslations.
    /// </summary>
    public static class LocalizationReviewedEnglishCatalog
    {
        private static readonly Lazy<IReadOnlyDictionary<string, string>> BySourceKey = new(Build);

        public static bool TryResolve(string? sourceText, out string english)
        {
            english = string.Empty;
            var key = LocalizationTextKey.Create(sourceText);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (!BySourceKey.Value.TryGetValue(key, out var resolved)
                || string.IsNullOrWhiteSpace(resolved))
            {
                return false;
            }

            english = resolved;
            return true;
        }

        public static bool TryResolveByKey(string? sourceKey, out string english)
        {
            english = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceKey))
                return false;

            if (!BySourceKey.Value.TryGetValue(sourceKey.Trim(), out var resolved)
                || string.IsNullOrWhiteSpace(resolved))
            {
                return false;
            }

            english = resolved;
            return true;
        }

        private static IReadOnlyDictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            void AddRange(IEnumerable<LocalizationSeedItem> items)
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.English))
                        continue;

                    // Skip leaked Turkish English rows from older packs.
                    if (LocalizationMachineTranslationService.LooksLikeTurkishText(item.English))
                        continue;

                    var key = LocalizationTextKey.Create(item.Source);
                    if (!string.IsNullOrWhiteSpace(key))
                        map[key] = LocalizationTextKey.Normalize(item.English);
                }
            }

            // Later packs intentionally override earlier ones (Final / Offline / Product).
            AddRange(LocalizationPublicAuthSeedData.Items);
            AddRange(LocalizationPublicAccountSeedData.Items);
            AddRange(LocalizationConversionFlowSeedData.Items);
            AddRange(LocalizationEnglishOfflineUiSeedData.Items);
            AddRange(LocalizationSeedData.Items);
            AddRange(ProductLocalizationSeedData.Items);
            AddRange(LocalizationEnglishExtendedSeedData.Items);
            AddRange(LocalizationEnglishBankTransferSeedData.Items);
            AddRange(LocalizationEnglishSharedSeedData.Items);
            AddRange(LocalizationEnglishContentSeedData.Items);
            AddRange(LocalizationEnglishDealerSeedData.Items);
            AddRange(LocalizationFinalSeedData.Items);
            AddRange(LocalizationEnglishGuideSeedData.Items);

            return map;
        }
    }
}
