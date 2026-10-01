namespace NSYazilim.Web.Services
{
    public sealed class LocalizationMachineTranslationOptions
    {
        public bool Enabled { get; set; } = true;
        public string Provider { get; set; } = "FreeCascade";
        public string ApiKey { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public int MaxTextsPerRequest { get; set; } = 24;
        public int MaxCharactersPerRequest { get; set; } = 28000;
        public int MaxParallelRequests { get; set; } = 4;
        public int QueueBatchSize { get; set; } = 40;
        public int WorkerStartupDelaySeconds { get; set; } = 2;
        public int WorkerIdleDelaySeconds { get; set; } = 20;
        public int WorkerContinuousDelayMilliseconds { get; set; } = 250;
        public int WorkerBatchTimeoutSeconds { get; set; } = 90;

        // FreeCascade / GoogleFree / MyMemory do not require a paid API key.
        // GoogleCloud and DeepL remain optional providers for future use only.
        public bool IsConfigured
        {
            get
            {
                if (!Enabled)
                    return false;

                var provider = (Provider ?? string.Empty).Trim();
                if (provider.Equals("FreeCascade", StringComparison.OrdinalIgnoreCase)
                    || provider.Equals("GoogleFree", StringComparison.OrdinalIgnoreCase)
                    || provider.Equals("MyMemory", StringComparison.OrdinalIgnoreCase))
                    return true;

                return !string.IsNullOrWhiteSpace(ApiKey);
            }
        }
    }
}
