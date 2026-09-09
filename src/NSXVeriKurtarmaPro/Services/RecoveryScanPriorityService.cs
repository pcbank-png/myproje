using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Tarama sonucunu tarih nedeniyle elemez. Yalnızca en güçlü mevcut zaman kanıtını
/// kullanarak şimdiki zamandan geriye doğru daha yeni adayları önce sunar.
/// </summary>
public static class RecoveryScanPriorityService
{
    private static readonly DateTimeOffset MinimumPlausibleTime =
        new(new DateTime(1970, 1, 1), TimeSpan.Zero);

    public static DateTimeOffset? GetPriorityTime(RecoveryFileItem item)
    {
        if (IsPlausible(item.DeletedAt)) return item.DeletedAt;
        if (IsPlausible(item.FileSystemModifiedAt)) return item.FileSystemModifiedAt;
        if (IsPlausible(item.FileSystemCreatedAt)) return item.FileSystemCreatedAt;
        return null;
    }

    public static int GetEvidenceRank(RecoveryFileItem item)
    {
        if (IsPlausible(item.DeletedAt)) return 4;
        if (IsPlausible(item.FileSystemModifiedAt)) return 3;
        if (IsPlausible(item.FileSystemCreatedAt)) return 2;
        return 0;
    }

    public static IEnumerable<RecoveryFileItem> OrderNewestFirst(IEnumerable<RecoveryFileItem> items)
    {
        return items
            .OrderByDescending(item => GetPriorityTime(item) ?? DateTimeOffset.MinValue)
            .ThenByDescending(GetEvidenceRank)
            .ThenByDescending(item => item.SourceOffset);
    }

    public static RecoveryFileItem[] OrderNewestFirst(IReadOnlyList<RecoveryFileItem> items)
    {
        return OrderNewestFirst(items.AsEnumerable()).ToArray();
    }

    private static bool IsPlausible(DateTimeOffset? value)
    {
        if (!value.HasValue)
            return false;

        DateTimeOffset utc = value.Value.ToUniversalTime();
        // Tarama önceliği "şimdi → geçmiş" yönündedir. Bozuk metadata gelecekte bir
        // tarih taşıyorsa sonuç elenmez, yalnızca öncelik kanıtı olarak kullanılmaz.
        return utc >= MinimumPlausibleTime && utc <= DateTimeOffset.UtcNow.AddDays(2);
    }
}
