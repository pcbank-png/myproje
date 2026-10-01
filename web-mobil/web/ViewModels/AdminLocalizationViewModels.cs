using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public sealed class AdminLanguagesViewModel
    {
        public IReadOnlyList<AdminLanguageRowViewModel> Languages { get; init; } = Array.Empty<AdminLanguageRowViewModel>();
        public int TotalResources { get; init; }
        public bool MachineTranslationAvailable { get; init; }
    }

    public sealed class AdminLanguageRowViewModel
    {
        public SiteLanguage Language { get; init; } = new();
        public int TranslationCount { get; init; }
        public decimal CoveragePercent { get; init; }
        public int QueuePending { get; init; }
        public int QueueProcessing { get; init; }
        public int QueueCompleted { get; init; }
        public int QueueFailed { get; init; }
        public decimal QueueProgressPercent { get; init; }
        public DateTime? QueueLastActivityAt { get; init; }
        public DateTime? QueueLastCompletedAt { get; init; }
        public DateTime? QueueNextAttemptAt { get; init; }
        public string? QueueLastError { get; init; }
    }

    public sealed class AdminTranslationsViewModel
    {
        public SiteLanguage Language { get; init; } = new();
        public IReadOnlyList<AdminTranslationRowViewModel> Items { get; init; } = Array.Empty<AdminTranslationRowViewModel>();
        public string? Query { get; init; }
        public int Page { get; init; }
        public int PageSize { get; init; }
        public int TotalCount { get; init; }
        public int MissingCount { get; init; }
        public bool CanAutoTranslate { get; init; }
        public int QueuePending { get; init; }
        public int QueueProcessing { get; init; }
        public int QueueCompleted { get; init; }
        public int QueueFailed { get; init; }
        public decimal QueueProgressPercent { get; init; }
        public int? MatchedProductId { get; init; }
        public string? MatchedProductName { get; init; }
        public string? MatchedProductDescription { get; init; }
        public bool HasProductDescriptionMatch => MatchedProductId.HasValue && !string.IsNullOrWhiteSpace(MatchedProductName) && Items.Count > 0;
        public int TotalPages => HasProductDescriptionMatch ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize)));
    }

    public sealed class AdminTranslationRowViewModel
    {
        public string SourceKey { get; init; } = string.Empty;
        public string SourceText { get; init; } = string.Empty;
        public string? FirstSeenPath { get; init; }
        public long HitCount { get; init; }
        public string Value { get; init; } = string.Empty;
        public bool IsReviewed { get; init; }
        public bool IsLocked { get; init; }
    }
}
