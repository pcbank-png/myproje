using System.Collections.Concurrent;

namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Wakes the localization worker immediately after an admin resume request and
    /// carries a short-lived language priority hint. The durable queue remains in MySQL.
    /// </summary>
    public sealed class LocalizationTranslationQueueSignal
    {
        private readonly ConcurrentQueue<string> _priorityLanguages = new();
        private readonly ConcurrentDictionary<string, byte> _queuedPriorityLanguages = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _wakeSignal = new(0, 1);

        public void Pulse(string? languageCode = null)
        {
            if (!string.IsNullOrWhiteSpace(languageCode))
            {
                var normalizedCode = languageCode.Trim();
                if (_queuedPriorityLanguages.TryAdd(normalizedCode, 0))
                    _priorityLanguages.Enqueue(normalizedCode);
            }

            try
            {
                if (_wakeSignal.CurrentCount == 0)
                    _wakeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake-up is already pending.
            }
        }

        public string? TakePriorityLanguage()
        {
            while (_priorityLanguages.TryDequeue(out var languageCode))
            {
                if (_queuedPriorityLanguages.TryRemove(languageCode, out _))
                    return languageCode;
            }

            return null;
        }

        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                await _wakeSignal.WaitAsync(timeout, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }
}
