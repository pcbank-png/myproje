using System.Collections.Concurrent;

namespace NSYazilim.Web.Services
{
    public sealed record PendingLocalizationResource(string SourceKey, string SourceText, string Path, long Hits);

    public sealed class LocalizationCaptureService
    {
        private sealed record Pending(string SourceText, string Path, long Hits);
        private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _knownThisProcess = new(StringComparer.Ordinal);

        public void Enqueue(string sourceText, string? path)
        {
            var normalized = LocalizationTextKey.Normalize(sourceText);
            if (!ShouldCapture(normalized))
                return;

            var key = LocalizationTextKey.Create(normalized);
            if (key.Length == 0 || _knownThisProcess.ContainsKey(key))
                return;

            var safePath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            if (safePath.Length > 500)
                safePath = safePath[..500];

            _pending.AddOrUpdate(
                key,
                _ => new Pending(normalized, safePath, 1),
                (_, current) => current with { Hits = current.Hits + 1 });
        }


        public void MarkKnown(IEnumerable<string> sourceKeys)
        {
            foreach (var key in sourceKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    _knownThisProcess.TryAdd(key, 0);
            }
        }

        public IReadOnlyList<PendingLocalizationResource> Drain(int maxItems = 300)
        {
            var result = new List<PendingLocalizationResource>(Math.Min(maxItems, _pending.Count));
            foreach (var pair in _pending)
            {
                if (result.Count >= maxItems)
                    break;

                if (_pending.TryRemove(pair.Key, out var item))
                    result.Add(new PendingLocalizationResource(pair.Key, item.SourceText, item.Path, item.Hits));
            }
            return result;
        }

        private static bool ShouldCapture(string text)
        {
            if (text.Length < 2 || text.Length > 9000)
                return false;

            if (!text.Any(char.IsLetter))
                return false;

            if (text.StartsWith("@@NSX_SKIP_", StringComparison.Ordinal))
                return false;

            // Capture worker creates durable jobs. Never inventory a value that the machine
            // translation layer is intentionally forbidden to process; otherwise harmless
            // URLs/e-mails/technical literals can keep a language below 100% forever.
            if (LocalizationMachineTranslationService.IsInvariantLocalizationText(text))
                return false;

            return true;
        }
    }
}
