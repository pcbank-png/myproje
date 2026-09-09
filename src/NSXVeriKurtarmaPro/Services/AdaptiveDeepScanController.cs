using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Controls Deep Scan read geometry without changing recovery coverage. Every byte is still
/// visited by the same recovery pipeline; only the next sequential read window is adjusted
/// from media capability, data density, read errors and measured I/O latency/throughput.
/// </summary>
public sealed class AdaptiveDeepScanController
{
    private const int MiB = 1024 * 1024;

    private readonly int _sectorSize;
    private readonly int _safeBlockSize;
    private readonly string _profileKey;
    private readonly double _growthLatencyLimitMs;
    private readonly double _slowReadLatencyMs;
    private readonly double _minimumGrowthBytesPerSecond;
    private readonly bool _healthSafeScanForced;
    private int _coldStreak;
    private int _stableStreak;

    public AdaptiveDeepScanController(RecoveryMediaProfile profile, int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _profileKey = (profile.Key ?? string.Empty).Trim().ToLowerInvariant();
        _sectorSize = Math.Max(512, sectorSize);
        BaseBlockSize = Align(Math.Max(MiB, profile.ScanBlockSize));
        MinBlockSize = Align(Math.Max(MiB, BaseBlockSize / 2));
        _safeBlockSize = Align(MiB);
        MaxBlockSize = Align(Math.Max(BaseBlockSize, GetMaximumBlockSize(_profileKey, BaseBlockSize)));
        _healthSafeScanForced = profile.SafeScanPreferred;
        SafeScanActive = _healthSafeScanForced;
        CurrentBlockSize = _healthSafeScanForced ? _safeBlockSize : BaseBlockSize;

        (_growthLatencyLimitMs, _slowReadLatencyMs, _minimumGrowthBytesPerSecond) = _profileKey switch
        {
            "nvme" => (180d, 650d, 180d * MiB),
            "ssd" => (240d, 750d, 90d * MiB),
            "hdd" => (320d, 1100d, 28d * MiB),
            "sd" => (360d, 1200d, 18d * MiB),
            "camera" => (320d, 1000d, 16d * MiB),
            "usb" => (360d, 1200d, 18d * MiB),
            _ => (320d, 1000d, 20d * MiB)
        };
    }

    public int BaseBlockSize { get; }
    public int MinBlockSize { get; }
    public int MaxBlockSize { get; }
    public int CurrentBlockSize { get; private set; }

    public long BlocksObserved { get; private set; }
    public long CleanBlocks { get; private set; }
    public long DenseBlocks { get; private set; }
    public long ErrorBlocks { get; private set; }
    public long SignatureCandidates { get; private set; }
    public long AcceptedResults { get; private set; }
    public int BlockSizeChanges { get; private set; }
    public bool SafeScanActive { get; private set; }
    public long CumulativeUnreadableBytes { get; private set; }

    public long ReadSamples { get; private set; }
    public long ReadBytes { get; private set; }
    public double ActiveReadMilliseconds { get; private set; }
    public double PeakReadBytesPerSecond { get; private set; }
    public long SlowReadSamples { get; private set; }
    public long FastReadSamples { get; private set; }

    public double AverageReadBytesPerSecond => ActiveReadMilliseconds > 0d
        ? ReadBytes / (ActiveReadMilliseconds / 1000d)
        : 0d;

    public void Observe(AdaptiveDeepScanObservation observation)
    {
        if (observation.BytesRead <= 0)
            return;

        BlocksObserved++;
        SignatureCandidates += Math.Max(0, observation.SignatureCandidates);
        AcceptedResults += Math.Max(0, observation.AcceptedResults);

        double sampleBytesPerSecond = RegisterReadPerformance(observation.BytesRead, observation.ReadElapsedMilliseconds);
        bool latencySlow = observation.ReadElapsedMilliseconds >= _slowReadLatencyMs;
        bool performanceAllowsGrowth = observation.ReadElapsedMilliseconds <= 0d ||
                                       (observation.ReadElapsedMilliseconds <= _growthLatencyLimitMs &&
                                        sampleBytesPerSecond >= _minimumGrowthBytesPerSecond);

        bool hasReadErrors = observation.UnreadableBytes > 0;
        CumulativeUnreadableBytes += Math.Max(0, observation.UnreadableBytes);
        int mib = Math.Max(1, observation.BytesRead / MiB);
        int denseThreshold = Math.Max(12, mib * 6);
        bool dense = observation.SignatureCandidates >= denseThreshold ||
                     observation.AcceptedResults >= Math.Max(4, mib * 2) ||
                     observation.OrphanFragments >= Math.Max(4, mib * 2);
        bool clean = !hasReadErrors && observation.SignatureCandidates == 0 &&
                     observation.AcceptedResults == 0 && observation.OrphanFragments == 0;

        // SMART caution/critical is a pre-scan health signal. When present, do not wait for
        // the first read failures before becoming conservative and do not grow the request
        // window again merely because a few consecutive regions happen to read cleanly.
        if (_healthSafeScanForced)
        {
            SafeScanActive = true;
            if (hasReadErrors)
                ErrorBlocks++;
            else if (dense)
                DenseBlocks++;
            else if (clean)
                CleanBlocks++;

            _coldStreak = 0;
            _stableStreak = 0;
            SetBlockSize(_safeBlockSize);
            return;
        }

        if (hasReadErrors)
        {
            ErrorBlocks++;
            _coldStreak = 0;
            _stableStreak = 0;
            if (ErrorBlocks >= 2 || CumulativeUnreadableBytes >= _sectorSize * 16L)
                SafeScanActive = true;

            int requested = SafeScanActive
                ? Math.Max(_safeBlockSize, CurrentBlockSize / 4)
                : Math.Max(MinBlockSize, CurrentBlockSize / 2);
            SetBlockSize(requested);
            return;
        }

        // A very slow healthy read is not a recovery failure. Keep full coverage, but reduce
        // request latency so USB2 cameras, weak card readers and throttling flash remain
        // responsive instead of being forced into an oversized sequential window.
        if (latencySlow && CurrentBlockSize > MinBlockSize)
        {
            _coldStreak = 0;
            _stableStreak = 0;
            SetBlockSize(Math.Max(MinBlockSize, CurrentBlockSize / 2));
            return;
        }

        if (dense)
        {
            DenseBlocks++;
            _coldStreak = 0;
            _stableStreak = 0;
            SetBlockSize(Math.Max(MinBlockSize, CurrentBlockSize / 2));
            return;
        }

        if (clean)
        {
            CleanBlocks++;
            _coldStreak++;
            _stableStreak++;

            // Grow only when the real device proves that the current window completes with
            // healthy latency and useful throughput. With no timing sample (unit tests or an
            // older caller), preserve the original adaptive behavior for compatibility.
            if (_coldStreak >= 3 && CurrentBlockSize < MaxBlockSize && performanceAllowsGrowth)
            {
                SetBlockSize(Math.Min(MaxBlockSize, CurrentBlockSize * 2));
                _coldStreak = 0;
            }
            return;
        }

        _coldStreak = 0;
        _stableStreak++;

        if (_stableStreak >= 2 && CurrentBlockSize < BaseBlockSize)
        {
            SetBlockSize(Math.Min(BaseBlockSize, CurrentBlockSize * 2));
            _stableStreak = 0;
        }
    }

    public void RestoreState(RecoveryScanCheckpoint? checkpoint)
    {
        if (checkpoint is null)
            return;

        SafeScanActive = _healthSafeScanForced || checkpoint.AdaptiveSafeScanActive;
        BlocksObserved = Math.Max(0, checkpoint.AdaptiveBlocksObserved);
        CleanBlocks = Math.Max(0, checkpoint.AdaptiveCleanBlocks);
        DenseBlocks = Math.Max(0, checkpoint.AdaptiveDenseBlocks);
        ErrorBlocks = Math.Max(0, checkpoint.AdaptiveErrorBlocks);
        CumulativeUnreadableBytes = Math.Max(0, checkpoint.AdaptiveUnreadableBytes);
        BlockSizeChanges = Math.Max(0, checkpoint.AdaptiveBlockSizeChanges);
        ReadSamples = Math.Max(0, checkpoint.AdaptiveReadSamples);
        ReadBytes = Math.Max(0, checkpoint.AdaptiveReadBytes);
        ActiveReadMilliseconds = Math.Max(0d, checkpoint.AdaptiveReadMilliseconds);
        PeakReadBytesPerSecond = Math.Max(0d, checkpoint.AdaptivePeakBytesPerSecond);
        SlowReadSamples = Math.Max(0, checkpoint.AdaptiveSlowReadSamples);
        FastReadSamples = Math.Max(0, checkpoint.AdaptiveFastReadSamples);
        _coldStreak = 0;
        _stableStreak = 0;

        if (checkpoint.AdaptiveBlockSize > 0)
        {
            if (_healthSafeScanForced)
                CurrentBlockSize = _safeBlockSize;
            else
            {
                int minimum = SafeScanActive ? _safeBlockSize : MinBlockSize;
                CurrentBlockSize = Align(Math.Clamp(checkpoint.AdaptiveBlockSize, minimum, MaxBlockSize));
            }
        }
    }

    public string PerformanceText
    {
        get
        {
            if (ReadSamples <= 0 || AverageReadBytesPerSecond <= 0d)
                return "I/O kalibrasyonu bekleniyor";

            string avg = $"ort {RecoveryFileItem.FormatBytes((long)AverageReadBytesPerSecond)}/sn";
            string peak = PeakReadBytesPerSecond > 0d
                ? $" • tepe {RecoveryFileItem.FormatBytes((long)PeakReadBytesPerSecond)}/sn"
                : string.Empty;
            string slow = SlowReadSamples > 0 ? $" • yavas okuma {SlowReadSamples:N0}" : string.Empty;
            return $"I/O {avg}{peak}{slow}";
        }
    }

    public string DiagnosticText =>
        $"Adaptive {FormatMiB(MinBlockSize)}-{FormatMiB(MaxBlockSize)} MB" +
        $" • anlik {FormatMiB(CurrentBlockSize)} MB" +
        $" • blok {BlocksObserved:N0}" +
        $" • yogun {DenseBlocks:N0}" +
        $" • temiz {CleanBlocks:N0}" +
        $" • hata {ErrorBlocks:N0}" +
        (SafeScanActive ? " • SAFE SCAN" : string.Empty) +
        (CumulativeUnreadableBytes > 0 ? $" • okunamayan {RecoveryFileItem.FormatBytes(CumulativeUnreadableBytes)}" : string.Empty) +
        $" • gecis {BlockSizeChanges:N0}" +
        $" • {PerformanceText}";

    private double RegisterReadPerformance(int bytesRead, double elapsedMilliseconds)
    {
        if (bytesRead <= 0 || elapsedMilliseconds <= 0d ||
            double.IsNaN(elapsedMilliseconds) || double.IsInfinity(elapsedMilliseconds))
            return 0d;

        ReadSamples++;
        ReadBytes += bytesRead;
        ActiveReadMilliseconds += elapsedMilliseconds;

        double bytesPerSecond = bytesRead / (elapsedMilliseconds / 1000d);
        if (bytesPerSecond > PeakReadBytesPerSecond)
            PeakReadBytesPerSecond = bytesPerSecond;

        if (elapsedMilliseconds >= _slowReadLatencyMs)
            SlowReadSamples++;
        else if (elapsedMilliseconds <= _growthLatencyLimitMs && bytesPerSecond >= _minimumGrowthBytesPerSecond)
            FastReadSamples++;

        return bytesPerSecond;
    }

    private void SetBlockSize(int requested)
    {
        int minimum = SafeScanActive ? _safeBlockSize : MinBlockSize;
        int next = Align(Math.Clamp(requested, minimum, MaxBlockSize));
        if (next == CurrentBlockSize)
            return;

        CurrentBlockSize = next;
        BlockSizeChanges++;
    }

    private int Align(int value)
    {
        int remainder = value % _sectorSize;
        if (remainder == 0)
            return value;

        long aligned = (long)value + (_sectorSize - remainder);
        return aligned > int.MaxValue ? value - remainder : (int)aligned;
    }

    private static int GetMaximumBlockSize(string profileKey, int baseBlockSize) =>
        profileKey switch
        {
            "nvme" => 64 * MiB,
            "ssd" => 32 * MiB,
            "hdd" => 16 * MiB,
            "sd" or "camera" or "usb" => 16 * MiB,
            _ => Math.Min(16 * MiB, checked(baseBlockSize * 2))
        };

    private static int FormatMiB(int bytes) => Math.Max(1, bytes / MiB);
}

public readonly record struct AdaptiveDeepScanObservation(
    int BytesRead,
    long UnreadableBytes,
    int SignatureCandidates,
    int AcceptedResults,
    int OrphanFragments,
    double ReadElapsedMilliseconds = 0d);
