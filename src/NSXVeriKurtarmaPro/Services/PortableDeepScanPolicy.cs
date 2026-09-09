using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Keeps Deep Scan responsive on mounted USB flash, SD/microSD and camera media without
/// weakening recovery coverage. The metadata prelude is bounded; the following RAW pass
/// still visits the complete source byte-for-byte in read-only mode.
/// </summary>
internal static class PortableDeepScanPolicy
{
    private const int MiB = 1024 * 1024;

    // Ilk RAW blok kucuk tutulur: bozuk/RAW USB ve SD medyada kullanici ilk sonucu
    // beklerken 8 MB'lik tek bir I/O veya CPU analizine kilitlenmez. Adaptive controller
    // temiz ve hizli bloklarda 4/8/16 MB'a otomatik buyur.
    internal const int PortableInitialScanBlockSize = 2 * MiB;
    internal const int FatPreludeDirectoryClusterLimit = 2048;
    internal const long NtfsPreludeMftBytes = 64L * MiB;
    internal const long ExFatPreludeDirectoryBytesPerItem = 8L * MiB;
    internal const long ExFatPreludeTotalDirectoryBytes = 32L * MiB;
    internal const int ExFatPreludeDirectoryLimit = 2048;

    internal const int RawInBlockProgressBytes = 256 * 1024;
    internal static readonly TimeSpan RawCandidateValidationBudget = TimeSpan.FromMilliseconds(120);
    internal static readonly TimeSpan RawLiveImageValidationBudget = TimeSpan.FromMilliseconds(2200);
    internal static readonly TimeSpan RawLiveDocumentValidationBudget = TimeSpan.FromMilliseconds(650);
    internal static readonly TimeSpan UnifiedCandidateValidationBudget = TimeSpan.FromMilliseconds(24);
    internal static readonly TimeSpan UnifiedImageFallbackValidationBudget = TimeSpan.FromMilliseconds(40);
    internal static readonly TimeSpan UnifiedDocumentFallbackValidationBudget = TimeSpan.FromMilliseconds(35);
    internal static readonly TimeSpan UnifiedDeferredDrainBudget = TimeSpan.FromSeconds(6);
    internal const int UnifiedDeferredCandidateLimit = 256;

    internal const double MetadataPhaseEndPercent = 2.5d;
    internal const double RawPhaseEndPercent = 98d;
    internal static readonly TimeSpan MetadataPrefaceBudget = TimeSpan.FromSeconds(12);

    internal static TimeSpan GetRawCandidateValidationBudget(SignatureKind kind) => kind switch
    {
        // Strong image signatures are cheap to prefilter and are the first confidence signal
        // a user expects during a USB/SD Deep Scan. Give a real image enough time to reach
        // its verified end marker instead of postponing every photo until the 98% drain.
        SignatureKind.Jpeg or
        SignatureKind.Png or
        SignatureKind.Gif or
        SignatureKind.Bmp or
        SignatureKind.TiffLittleEndian or
        SignatureKind.TiffBigEndian or
        SignatureKind.FujiRaf or
        SignatureKind.SigmaX3f or
        SignatureKind.WebP or
        SignatureKind.Ico or
        SignatureKind.Jpeg2000 or
        SignatureKind.Jpeg2000Codestream or
        SignatureKind.Psd or
        SignatureKind.Dds or
        SignatureKind.Exr => RawLiveImageValidationBudget,

        // Documents/archives also need to surface during the sequential pass, but their
        // footers can legitimately be far away. Keep a tighter budget and defer only the
        // unusually large/truncated candidate.
        SignatureKind.Pdf or
        SignatureKind.Zip or
        SignatureKind.Rar or
        SignatureKind.OleCompound or
        SignatureKind.SqliteDatabase or
        SignatureKind.SqliteWal or
        SignatureKind.SqliteJournal => RawLiveDocumentValidationBudget,

        _ => RawCandidateValidationBudget
    };

    internal static bool IsLiveResultPriority(SignatureKind kind) =>
        GetRawCandidateValidationBudget(kind) > RawCandidateValidationBudget;

    internal static TimeSpan GetUnifiedCandidateValidationBudget(SignatureKind kind) => kind switch
    {
        SignatureKind.Jpeg or
        SignatureKind.Png or
        SignatureKind.Gif or
        SignatureKind.Bmp or
        SignatureKind.TiffLittleEndian or
        SignatureKind.TiffBigEndian or
        SignatureKind.WebP or
        SignatureKind.Ico or
        SignatureKind.Jpeg2000 or
        SignatureKind.Jpeg2000Codestream or
        SignatureKind.Psd or
        SignatureKind.Dds or
        SignatureKind.Exr => UnifiedImageFallbackValidationBudget,

        SignatureKind.Pdf or
        SignatureKind.Zip or
        SignatureKind.Rar or
        SignatureKind.OleCompound or
        SignatureKind.SqliteDatabase or
        SignatureKind.SqliteWal or
        SignatureKind.SqliteJournal => UnifiedDocumentFallbackValidationBudget,

        _ => UnifiedCandidateValidationBudget
    };

    public static bool IsResponsivePortableSource(StorageDeviceInfo device, RecoveryMediaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);

        if (!PortableQuickScanPolicy.IsPortableRecoverySource(device))
            return false;

        // RAW/format isteyen USB/SD Windows tarafinda drive-letter yerine whole PhysicalDrive
        // veya partition-view olarak gorunebilir. Bu nedenle kaynak sekline degil medya sinifina
        // bakilir. External HDD/SSD ise kendi throughput politikasini korur.
        string key = (profile.Key ?? string.Empty).Trim().ToLowerInvariant();
        return key is "usb" or "sd" or "camera" or "universal";
    }

    public static int GetInitialScanBlockSize(RecoveryMediaProfile profile, bool responsivePortable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!responsivePortable)
            return profile.ScanBlockSize;

        int requested = profile.SafeScanPreferred ? MiB : PortableInitialScanBlockSize;
        return Math.Min(Math.Max(MiB, profile.ScanBlockSize), requested);
    }

    public static long GetProgressIntervalBytes(bool responsivePortable) =>
        responsivePortable ? 1L * MiB : 64L * MiB;

    public static TimeSpan GetProgressHeartbeat(bool responsivePortable) =>
        responsivePortable ? TimeSpan.FromMilliseconds(750) : TimeSpan.FromSeconds(2);

    public static bool AllowExtendedQuickMetadataRescue(
        StorageDeviceInfo device,
        RecoveryMediaProfile profile) =>
        !IsResponsivePortableSource(device, profile);

    public static bool EnableNtfsUsnPriority(
        StorageDeviceInfo device,
        RecoveryMediaProfile profile) =>
        !IsResponsivePortableSource(device, profile);

    public static bool IsFastMetadataPrelude(StorageDeviceInfo device, bool allowPortableRescue) =>
        !allowPortableRescue && PortableQuickScanPolicy.IsPortableRecoverySource(device);

    public static int GetFatDirectoryClusterLimit(bool fastPrelude, int normalLimit) =>
        fastPrelude ? Math.Min(normalLimit, FatPreludeDirectoryClusterLimit) : normalLimit;

    public static long GetNtfsRecordLimit(bool fastPrelude, long totalRecords, int recordSize)
    {
        if (!fastPrelude || totalRecords <= 0 || recordSize <= 0)
            return Math.Max(0, totalRecords);

        long budgetRecords = Math.Max(4096, NtfsPreludeMftBytes / recordSize);
        return Math.Min(totalRecords, budgetRecords);
    }

    public static long GetExFatDirectoryByteLimit(bool fastPrelude, long normalLimit) =>
        fastPrelude ? Math.Min(normalLimit, ExFatPreludeDirectoryBytesPerItem) : normalLimit;

    public static int GetExFatDirectoryLimit(bool fastPrelude) =>
        fastPrelude ? ExFatPreludeDirectoryLimit : int.MaxValue;

    public static double MapMetadataPercent(double sourcePercent)
    {
        double ratio = Math.Clamp(sourcePercent, 0d, 100d) / 100d;
        return MetadataPhaseEndPercent * ratio;
    }

    public static double MapRawPercent(long processed, long total)
    {
        double ratio = total <= 0
            ? 0d
            : Math.Clamp((double)Math.Max(0, processed) / total, 0d, 1d);
        return MetadataPhaseEndPercent + (RawPhaseEndPercent - MetadataPhaseEndPercent) * ratio;
    }
}
