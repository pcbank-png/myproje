using NSXVeriKurtarmaPro.Infrastructure;
using System.Windows.Media;

namespace NSXVeriKurtarmaPro.Models;

public sealed class RecoveryFileItem : ObservableObject
{
    private bool _isChecked;
    private ImageSource? _previewImage;
    private string? _repairedFilePath;
    private string? _repairMessage;
    private int? _videoHealthScore;
    private string? _videoHealthGrade;
    private string? _videoHealthSummary;
    private DateTimeOffset? _capturedAt;
    private string? _captureDateSource;
    private uint _pixelWidth;
    private uint _pixelHeight;
    private int _recoveryConfidenceScore = -1;
    private string? _recoveryConfidenceGrade;
    private string? _recoveryConfidenceSummary;
    private string _sourceText = string.Empty;
    private string? _recoveredOriginalPath;
    private string _recoveredVolumeRoot = string.Empty;
    private string? _ntfsForensicEvidence;
    private int _referenceSimilarityScore = -1;
    private bool _isReferenceSearchMatch;
    private string _referenceMatchLabel = string.Empty;

    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required long SizeBytes { get; init; }
    public required string RecoveryState { get; init; }
    public required string TypeGlyph { get; init; }
    public required string SourceText
    {
        get => _sourceText;
        set
        {
            if (SetProperty(ref _sourceText, value))
            {
                OnPropertyChanged(nameof(LocationText));
                OnPropertyChanged(nameof(DetailPrimaryInfoText));
            }
        }
    }
    public required RecoverySourceKind SourceKind { get; init; }
    public bool IsExistingFile { get; init; }

    public long SourceOffset { get; init; }
    public int ClusterSize { get; init; }
    public byte[]? ResidentData { get; init; }
    public IReadOnlyList<DataRun>? DataRuns { get; init; }
    public byte[]? PrefixData { get; init; }
    public byte[]? SuffixData { get; init; }
    public IReadOnlyList<SourceExtent>? SourceExtents { get; init; }
    public RecoveryTransformKind TransformKind { get; init; } = RecoveryTransformKind.None;
    public long SourceUnreadableBytes { get; set; } = -1;

    public DateTimeOffset? FileSystemCreatedAt { get; init; }
    public DateTimeOffset? FileSystemModifiedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public string? DeletionDateSource { get; init; }

    public long NtfsRecordIndex { get; init; } = -1;
    public ulong NtfsFileReference { get; init; }
    public ulong NtfsParentReference { get; init; }

    public string? RecoveredOriginalPath
    {
        get => _recoveredOriginalPath;
        set => SetRecoveredOriginalPath(value, notify: true);
    }

    internal bool SetRecoveredOriginalPath(string? value, bool notify)
    {
        string? normalized = NormalizeRecoveredPath(value);
        if (string.Equals(_recoveredOriginalPath, normalized, StringComparison.Ordinal))
            return false;

        _recoveredOriginalPath = normalized;
        if (notify)
            RaiseRecoveredPathPropertiesChanged();
        return true;
    }

    /// <summary>
    /// UI-only mounted volume root (for example D:\). It is assigned by the active
    /// recovery workspace and intentionally is not persisted as forensic evidence.
    /// RecoveredOriginalPath remains volume-relative so saved sessions stay portable.
    /// </summary>
    public string RecoveredVolumeRoot
    {
        get => _recoveredVolumeRoot;
        set
        {
            string normalized = NormalizeVolumeRoot(value);
            if (SetProperty(ref _recoveredVolumeRoot, normalized))
                RaiseRecoveredPathPropertiesChanged();
        }
    }

    public bool HasRecoveredOriginalPath => !string.IsNullOrWhiteSpace(RecoveredOriginalPath);

    public string RecoveredFolderPath
    {
        get
        {
            string relative = NormalizeRecoveredPath(RecoveredOriginalPath) ?? string.Empty;
            if (relative.Length == 0)
                return string.Empty;

            int separator = relative.LastIndexOf('\\');
            if (separator <= 0)
                return "\\";

            return relative[..separator];
        }
    }

    public string RecoveredFullPath => CombineRecoveredRoot(RecoveredVolumeRoot, RecoveredOriginalPath);
    public string RecoveredFullFolderPath => CombineRecoveredRoot(RecoveredVolumeRoot, RecoveredFolderPath);
    public string RecoveredPathDisplayText => HasRecoveredOriginalPath
        ? (string.IsNullOrWhiteSpace(RecoveredFullFolderPath) ? RecoveredFolderPath : RecoveredFullFolderPath)
        : "—";

    private void RaiseRecoveredPathPropertiesChanged()
    {
        OnPropertyChanged(nameof(RecoveredOriginalPath));
        OnPropertyChanged(nameof(HasRecoveredOriginalPath));
        OnPropertyChanged(nameof(RecoveredFolderPath));
        OnPropertyChanged(nameof(RecoveredFullPath));
        OnPropertyChanged(nameof(RecoveredFullFolderPath));
        OnPropertyChanged(nameof(RecoveredPathDisplayText));
        OnPropertyChanged(nameof(LocationText));
        OnPropertyChanged(nameof(DetailPrimaryInfoText));
    }

    private static string? NormalizeRecoveredPath(string? path)
    {
        string value = (path ?? string.Empty).Trim().Replace('/', '\\');
        if (value.Length == 0)
            return null;

        // Forensic path stays volume-relative: \Folder\File.ext
        if (value.Length >= 2 && value[1] == ':')
            value = value.Length > 2 ? value[2..] : "\\";

        return "\\" + value.Trim('\\');
    }

    private static string NormalizeVolumeRoot(string? root)
    {
        string value = (root ?? string.Empty).Trim().Replace('/', '\\');
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            return $"{char.ToUpperInvariant(value[0])}:\\";
        return string.Empty;
    }

    private static string CombineRecoveredRoot(string root, string? relativePath)
    {
        string relative = NormalizeRecoveredPath(relativePath) ?? string.Empty;
        if (relative.Length == 0)
            return string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return relative;
        if (relative == "\\")
            return root;
        return root.TrimEnd('\\') + relative;
    }

    public string? NtfsForensicEvidence
    {
        get => _ntfsForensicEvidence;
        set => SetProperty(ref _ntfsForensicEvidence, value);
    }

    public DateTimeOffset? ScanPriorityAt => DeletedAt ?? FileSystemModifiedAt ?? FileSystemCreatedAt;

    public DateTimeOffset? CapturedAt
    {
        get => _capturedAt;
        set
        {
            if (SetProperty(ref _capturedAt, value))
            {
                OnPropertyChanged(nameof(CapturedAtText));
                OnPropertyChanged(nameof(HasCapturedAt));
                OnPropertyChanged(nameof(ResultDate));
                OnPropertyChanged(nameof(ResultDateSortValue));
                OnPropertyChanged(nameof(ResultDateText));
            }
        }
    }

    public string? CaptureDateSource
    {
        get => _captureDateSource;
        set
        {
            if (SetProperty(ref _captureDateSource, value))
                OnPropertyChanged(nameof(CaptureDateSourceText));
        }
    }

    public int RecoveryConfidenceScore
    {
        get => _recoveryConfidenceScore;
        set
        {
            int normalized = value < 0 ? -1 : Math.Clamp(value, 0, 100);
            if (SetProperty(ref _recoveryConfidenceScore, normalized))
            {
                OnPropertyChanged(nameof(RecoveryConfidenceText));
                OnPropertyChanged(nameof(HasRecoveryConfidence));
                OnPropertyChanged(nameof(QualitySortValue));
            }
        }
    }

    public string? RecoveryConfidenceGrade
    {
        get => _recoveryConfidenceGrade;
        set
        {
            if (SetProperty(ref _recoveryConfidenceGrade, value))
                OnPropertyChanged(nameof(RecoveryConfidenceText));
        }
    }

    public string? RecoveryConfidenceSummary
    {
        get => _recoveryConfidenceSummary;
        set => SetProperty(ref _recoveryConfidenceSummary, value);
    }

    public bool HasRecoveryConfidence => RecoveryConfidenceScore >= 0;
    public string RecoveryConfidenceText => RecoveryConfidenceScore >= 0
        ? $"%{RecoveryConfidenceScore} • {RecoveryConfidenceGrade ?? "Analiz edildi"}"
        : "—";

    public DateTimeOffset? ResultDate => CapturedAt ?? FileSystemModifiedAt ?? FileSystemCreatedAt ?? DeletedAt;
    public DateTime ResultDateSortValue => ResultDate?.UtcDateTime ?? DateTime.MinValue;
    public string ResultDateText => ResultDate.HasValue
        ? ResultDate.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)
        : "—";
    public string TypeSortKey => FileTypeHelper.Normalize(Extension).ToUpperInvariant();
    public int QualitySortValue => RecoveryConfidenceScore;

    public bool HasCapturedAt => CapturedAt.HasValue;
    public string CapturedAtText => CapturedAt.HasValue
        ? CapturedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)
        : "—";
    public string CaptureDateSourceText => string.IsNullOrWhiteSpace(CaptureDateSource) ? "Bilinmiyor" : CaptureDateSource;

    public uint PixelWidth
    {
        get => _pixelWidth;
        set
        {
            if (SetProperty(ref _pixelWidth, value))
            {
                OnPropertyChanged(nameof(HasResolution));
                OnPropertyChanged(nameof(ResolutionText));
                OnPropertyChanged(nameof(ResolutionStatusText));
                OnPropertyChanged(nameof(DetailPrimaryInfoLabel));
                OnPropertyChanged(nameof(DetailPrimaryInfoText));
            }
        }
    }

    public uint PixelHeight
    {
        get => _pixelHeight;
        set
        {
            if (SetProperty(ref _pixelHeight, value))
            {
                OnPropertyChanged(nameof(HasResolution));
                OnPropertyChanged(nameof(ResolutionText));
                OnPropertyChanged(nameof(ResolutionStatusText));
                OnPropertyChanged(nameof(DetailPrimaryInfoLabel));
                OnPropertyChanged(nameof(DetailPrimaryInfoText));
            }
        }
    }

    public bool HasResolution => PixelWidth > 0 && PixelHeight > 0;
    public string ResolutionText => HasResolution
        ? $"{PixelWidth:N0} × {PixelHeight:N0} px"
        : "—";
    public string ResolutionStatusText => HasResolution ? ResolutionText : "Analiz ediliyor…";
    public string DetailPrimaryInfoLabel => Category is "Fotoğraf" or "Video" ? "Çözünürlük" : "Kaynak";
    public string DetailPrimaryInfoText => Category is "Fotoğraf" or "Video"
        ? ResolutionStatusText
        : LocationText;

    public void SetResolution(uint width, uint height)
    {
        if (width == 0 || height == 0)
            return;

        bool widthChanged = SetProperty(ref _pixelWidth, width, nameof(PixelWidth));
        bool heightChanged = SetProperty(ref _pixelHeight, height, nameof(PixelHeight));
        if (widthChanged || heightChanged)
        {
            OnPropertyChanged(nameof(HasResolution));
            OnPropertyChanged(nameof(ResolutionText));
            OnPropertyChanged(nameof(ResolutionStatusText));
            OnPropertyChanged(nameof(DetailPrimaryInfoLabel));
            OnPropertyChanged(nameof(DetailPrimaryInfoText));
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }


    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (SetProperty(ref _previewImage, value))
                OnPropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => PreviewImage is not null;

    public int ReferenceSimilarityScore
    {
        get => _referenceSimilarityScore;
        private set
        {
            if (SetProperty(ref _referenceSimilarityScore, value))
            {
                OnPropertyChanged(nameof(ReferenceSimilarityText));
                OnPropertyChanged(nameof(ReferenceSearchSortValue));
            }
        }
    }

    public bool IsReferenceSearchMatch
    {
        get => _isReferenceSearchMatch;
        private set => SetProperty(ref _isReferenceSearchMatch, value);
    }

    public string ReferenceMatchLabel
    {
        get => _referenceMatchLabel;
        private set => SetProperty(ref _referenceMatchLabel, value ?? string.Empty);
    }

    public string ReferenceSimilarityText => ReferenceSimilarityScore >= 0
        ? $"Benzerlik %{ReferenceSimilarityScore}"
        : string.Empty;
    public int ReferenceSearchSortValue => ReferenceSimilarityScore;

    public void SetReferenceSearchMatch(int score, string label)
    {
        ReferenceSimilarityScore = Math.Clamp(score, 0, 100);
        ReferenceMatchLabel = label;
        IsReferenceSearchMatch = true;
    }

    public void ClearReferenceSearchMatch()
    {
        ReferenceSimilarityScore = -1;
        ReferenceMatchLabel = string.Empty;
        IsReferenceSearchMatch = false;
    }

    public string? RepairedFilePath
    {
        get => _repairedFilePath;
        set
        {
            if (SetProperty(ref _repairedFilePath, value))
            {
                OnPropertyChanged(nameof(IsRepaired));
                OnPropertyChanged(nameof(EffectiveRecoveryState));
            }
        }
    }

    public string? RepairMessage
    {
        get => _repairMessage;
        set => SetProperty(ref _repairMessage, value);
    }

    public int? VideoHealthScore
    {
        get => _videoHealthScore;
        set
        {
            if (SetProperty(ref _videoHealthScore, value))
            {
                OnPropertyChanged(nameof(EffectiveRecoveryState));
                OnPropertyChanged(nameof(VideoHealthText));
            }
        }
    }

    public string? VideoHealthGrade
    {
        get => _videoHealthGrade;
        set
        {
            if (SetProperty(ref _videoHealthGrade, value))
                OnPropertyChanged(nameof(VideoHealthText));
        }
    }

    public string? VideoHealthSummary
    {
        get => _videoHealthSummary;
        set => SetProperty(ref _videoHealthSummary, value);
    }

    public bool IsRepaired => !string.IsNullOrWhiteSpace(_repairedFilePath) && File.Exists(_repairedFilePath);
    public string EffectiveRecoveryState => IsRepaired
        ? VideoHealthScore.HasValue ? $"Onarıldı • %{VideoHealthScore.Value}" : "Onarıldı"
        : RecoveryState;
    public string VideoHealthText => VideoHealthScore.HasValue
        ? $"%{VideoHealthScore.Value} • {VideoHealthGrade ?? "Analiz edildi"}"
        : "—";

    public string SizeText => FormatBytes(SizeBytes);
    public string LocationText => SourceKind switch
    {
        RecoverySourceKind.RawContiguous => SourceText.Contains("video akışı", StringComparison.OrdinalIgnoreCase)
            ? $"Video akışı • 0x{SourceOffset:X}"
            : SourceText.Contains("yeniden", StringComparison.OrdinalIgnoreCase)
                ? $"Video yeniden oluşturma • 0x{SourceOffset:X}"
                : $"RAW • 0x{SourceOffset:X}",
        RecoverySourceKind.NtfsResident => !string.IsNullOrWhiteSpace(RecoveredOriginalPath)
            ? $"NTFS • {RecoveredFullPath}"
            : SourceText.StartsWith("Klasör •", StringComparison.OrdinalIgnoreCase) ? SourceText : "NTFS • Dosya sistemi kaydı",
        RecoverySourceKind.NtfsRunList => !string.IsNullOrWhiteSpace(RecoveredOriginalPath)
            ? $"NTFS • {RecoveredFullPath}"
            : SourceText.StartsWith("Klasör •", StringComparison.OrdinalIgnoreCase) ? SourceText : "NTFS • küme zinciri",
        RecoverySourceKind.FatContiguous => !string.IsNullOrWhiteSpace(RecoveredOriginalPath)
            ? $"FAT • {RecoveredFullPath}"
            : IsExistingFile ? "FAT • mevcut dosya" : "FAT32 • silinmiş kayıt",
        RecoverySourceKind.ExFatContiguous => !string.IsNullOrWhiteSpace(RecoveredOriginalPath)
            ? $"exFAT • {RecoveredFullPath}"
            : IsExistingFile ? "exFAT • mevcut dosya" : "exFAT • silinmiş kayıt",
        RecoverySourceKind.Extents => SourceText,
        _ => SourceText
    };

    public string Category => FileTypeHelper.GetCategory(Extension);

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int index = 0;
        while (value >= 1024d && index < units.Length - 1)
        {
            value /= 1024d;
            index++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, System.Globalization.CultureInfo.CurrentCulture)} {units[index]}";
    }
}

public static class FileTypeHelper
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "JPG", "JPEG", "JPE", "JFIF", "PNG", "APNG", "WEBP", "GIF", "BMP", "DIB", "TIF", "TIFF",
        "HEIC", "HEIF", "AVIF", "JXL", "ICO", "CUR", "TGA", "PCX", "PSD", "PSB",
        "JP2", "J2K", "JPF", "JPX", "JPM", "MNG", "EXR", "HDR", "DDS",
        "PBM", "PGM", "PPM", "PNM", "PAM", "SVG", "SVGZ", "WMF", "EMF", "DCM", "DICOM",
        "3FR", "ARW", "CR2", "CR3", "CRW", "DNG", "ERF", "IIQ", "KDC", "MEF", "MOS", "MRW",
        "NEF", "NRW", "ORF", "PEF", "RAF", "RAW", "RW2", "RWL", "SR2", "SRF", "SRW", "X3F"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP4", "M4V", "MOV", "QT", "3GP", "3G2", "F4V",
        "AVI", "DIVX", "XVID", "WMV", "ASF", "DVR-MS", "WTV", "MKV", "WEBM",
        "MPG", "MPEG", "MPE", "MPV", "M1V", "M2V", "VOB", "EVO",
        "TS", "MTS", "M2TS", "M2T", "TP", "TRP", "MOD", "TOD",
        "H264", "AVC", "H265", "HEVC",
        "FLV", "OGV", "MXF", "DV", "RM", "RMVB", "NSV", "ROQ", "BIK", "BINK", "BK2", "BIK2", "SMK", "IVF", "AV1"
    };

    // Tek tarama motorunun kapsami UI'daki TÜR kataloguyla birebir aynı tutulur.
    // Böylece metadata taraması ses/arşiv/e-posta/diğer dosyaları "desteklenmiyor"
    // diye düşürmez; RAW tarafında ise yalnız doğrulanabilir imzalar aday üretir.
    private static readonly HashSet<string> SupportedExtensions = BuildSupportedExtensions();

    private static HashSet<string> BuildSupportedExtensions()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecoveryTypeCatalog.Group group in RecoveryTypeCatalog.AllGroups)
        {
            foreach (string extension in group.Extensions)
            {
                string normalized = Normalize(extension);
                if (normalized.Length > 0 &&
                    normalized is not "UZANTISIZ" and not "BILINMEYEN")
                {
                    result.Add(normalized);
                }
            }
        }

        // Mevcut fotoğraf/video motorlarının daha geniş uzantı sözlüğünü de aynen koru.
        // V5.2 bir kapsam genişletmesidir; daha önce çalışan formatlardan geri gitmez.
        result.UnionWith(PhotoExtensions);
        result.UnionWith(VideoExtensions);

        // Dosya sistemi/forensic motorlarının özel uzantıları. Bazıları UI katalogunda
        // eşanlamlı bir grup altında toplanır ama tarama doğrulamasında ayrıca gerekir.
        foreach (string extension in new[]
                 {
                     "DB3", "SQLITE3", "WAL", "JOURNAL", "M4B", "SVGZ"
                 })
        {
            result.Add(extension);
        }

        return result;
    }


    public static bool IsSupported(string extension)
    {
        string ext = Normalize(extension);
        return SupportedExtensions.Contains(ext);
    }

    public static bool IsPhoto(string extension)
    {
        string ext = Normalize(extension);
        return PhotoExtensions.Contains(ext);
    }

    public static bool IsVideo(string extension)
    {
        string ext = Normalize(extension);
        return VideoExtensions.Contains(ext);
    }

    public static int GetVideoPriority(string extension)
    {
        string ext = Normalize(extension);
        return ext switch
        {
            "MTS" => 0,
            "M2TS" => 1,
            "MP4" => 2,
            "MPG" => 3,
            "MPEG" => 4,
            "AVI" => 5,
            "MOV" => 6,

            // Aynı kapsayıcı ailelerinin yakın varyantları ana önceliklerin hemen arkasından gelir.
            "M2T" or "TS" or "TP" or "TRP" or "MOD" or "TOD" => 7,
            "M4V" or "F4V" or "3GP" or "3G2" => 8,
            "QT" => 9,
            "MPE" or "MPV" or "M1V" or "M2V" or "VOB" or "EVO" => 10,
            "DIVX" or "XVID" => 11,
            "MKV" or "WEBM" => 12,
            "WMV" or "ASF" or "DVR-MS" or "WTV" => 13,
            "H264" or "AVC" or "H265" or "HEVC" => 14,
            "FLV" or "OGV" or "MXF" or "DV" or "IVF" or "AV1" => 15,
            "RM" or "RMVB" or "NSV" or "ROQ" or "BIK" or "BINK" or "BK2" or "BIK2" or "SMK" => 16,
            _ => int.MaxValue
        };
    }

    public static string GetCategory(string extension)
    {
        string ext = Normalize(extension);
        if (PhotoExtensions.Contains(ext)) return "Fotoğraf";
        if (VideoExtensions.Contains(ext)) return "Video";
        return "Belge";
    }

    public static string GetGlyph(string extension)
    {
        string category = GetCategory(extension);
        return category switch
        {
            "Fotoğraf" => "\uEB9F",
            "Video" => "\uE714",
            _ => "\uE8A5"
        };
    }

    public static string Normalize(string extension) =>
        (extension ?? string.Empty).Trim().TrimStart('.').ToUpperInvariant();

    public static string SanitizeFileName(string fileName)
    {
        string result = fileName;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            result = result.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(result) ? "Kurtarilan_Dosya.bin" : result;
    }
}
