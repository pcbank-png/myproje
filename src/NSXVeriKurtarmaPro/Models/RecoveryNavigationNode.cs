using NSXVeriKurtarmaPro.Infrastructure;

namespace NSXVeriKurtarmaPro.Models;

public sealed class RecoveryNavigationNode : ObservableObject
{
    private string _title = string.Empty;
    private int _count;
    private bool _isSelected;
    private bool _isExpanded;
    private bool _isVisible = true;
    private bool _isExpandable;
    private bool _isCheckable;
    private bool? _isChecked;
    private int _selectedCount;

    public required string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }
    public required string FilterKind { get; init; }
    public required string FilterValue { get; init; }
    public string GroupKey { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE8A5";
    public string IconForeground { get; init; } = "#5E86C5";
    public string IconBackground { get; init; } = "#F2F6FC";
    public double IndentWidth { get; init; }
    public bool IsGroupHeader { get; init; }
    public bool IsExpandable
    {
        get => _isExpandable;
        set => SetProperty(ref _isExpandable, value);
    }
    public string ParentGroupKey { get; init; } = string.Empty;

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountText));
                if (_selectedCount > _count)
                    SetProperty(ref _selectedCount, Math.Max(0, _count), nameof(SelectedCount));
                UpdateCheckState();
            }
        }
    }

    public string CountText => Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnPropertyChanged(nameof(ExpandGlyph));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool IsCheckable
    {
        get => _isCheckable;
        set
        {
            if (SetProperty(ref _isCheckable, value))
                UpdateCheckState();
        }
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        set
        {
            int normalized = Math.Clamp(value, 0, Math.Max(0, Count));
            if (SetProperty(ref _selectedCount, normalized))
                UpdateCheckState();
        }
    }

    private void UpdateCheckState()
    {
        IsChecked = !IsCheckable || Count <= 0 || SelectedCount <= 0
            ? false
            : SelectedCount >= Count
                ? true
                : null;
    }

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
}

public static class RecoveryTypeCatalog
{
    public sealed record Group(string Key, string Title, string Glyph, IReadOnlyList<string> Extensions);

    private static readonly Group[] Groups =
    [
        new("images", "Resimler", "\uEB9F",
        [
            "jpg", "png", "jpeg", "gif", "tif", "tiff", "webp", "bmp", "ico", "heic", "heif", "avif",
            "psd", "psb", "raw", "dng", "arw", "cr2", "cr3", "nef", "orf", "raf", "rw2", "srw",
            "svg", "eps", "ai", "tga", "dds", "hdr", "exr", "jp2", "j2k", "jpf", "jpx", "wmf", "emf",
            "pbm", "pgm", "ppm", "pnm", "pam", "pcx", "cur", "jxl", "dcm", "dicom", "3fr", "x3f"
        ]),
        new("videos", "Videolar", "\uE714",
        [
            "mp4", "mov", "avi", "m4v", "3gp", "3g2", "m2ts", "mts", "ts", "mkv", "mpg", "mpeg", "mpe",
            "wmv", "asf", "webm", "flv", "f4v", "vob", "m2v", "m1v", "divx", "xvid", "ogv", "mxf", "dv",
            "mod", "tod", "tp", "trp", "m2t", "qt", "rm", "rmvb", "h264", "h265", "avc", "hevc", "ivf", "av1",
            "swf", "nsv", "roq", "bik", "bk2", "smk"
        ]),
        new("documents", "Belgeler", "\uE8A5",
        [
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "csv", "xml", "html", "htm",
            "odt", "ods", "odp", "one", "md", "json", "log", "tex", "epub", "mobi", "pages", "numbers", "key"
        ]),
        new("audio", "Ses", "\uE8D6",
        [
            "mp3", "wav", "wma", "aac", "m4a", "m4b", "flac", "ogg", "opus", "amr", "aiff", "aif", "ape", "ac3",
            "dts", "mid", "midi", "mka", "ra", "au", "caf"
        ]),
        new("archives", "Arşiv", "\uF012",
        [
            "zip", "rar", "7z", "tar", "gz", "gzip", "bz2", "xz", "cab", "iso", "tgz", "tbz", "tbz2", "txz", "z"
        ]),
        new("emails", "E-postalar", "\uE715",
        [
            "pst", "ost", "eml", "msg", "mbox", "dbx", "emlx", "oft", "ics", "vcf"
        ]),
        new("bookmarks", "Yer İmleri", "\uE734",
        [
            "url", "lnk", "webloc", "website", "desktop"
        ]),
        new("other", "Diğerleri", "\uE8B7",
        [
            "exe", "dll", "msi", "sys", "bin", "dat", "db", "sqlite", "sqlite3", "bak", "tmp", "ini", "cfg",
            "reg", "iso", "img", "dmg", "vhd", "vhdx", "vmdk", "apk", "jar", "class", "cs", "cpp", "h", "py",
            "js", "tsc", "css", "sql"
        ]),
        new("unsaved", "Kaydedilmemiş Dosyalar", "\uE7C3",
        [
            "uzantısız", "bilinmeyen"
        ])
    ];

    private static readonly Dictionary<string, string> GroupByExtension = BuildGroupLookup();

    public static IReadOnlyList<Group> AllGroups => Groups;

    public static string NormalizeExtension(string? extension) =>
        (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();

    public static string GetGroupKey(string? extension)
    {
        string ext = NormalizeExtension(extension);
        if (ext.Length == 0)
            return "unsaved";

        return GroupByExtension.TryGetValue(ext, out string? key) ? key : "other";
    }

    public static bool ExtensionBelongsTo(string? extension, string groupKey) =>
        string.Equals(GetGroupKey(extension), groupKey, StringComparison.OrdinalIgnoreCase);

    public static string GetIconForeground(string groupKey) => groupKey.ToLowerInvariant() switch
    {
        "images" => "#E67E22",
        "videos" => "#7656D8",
        "documents" => "#2B7BFF",
        "audio" => "#D94D7A",
        "archives" => "#B7791F",
        "emails" => "#1689A6",
        "bookmarks" => "#2E8B57",
        "other" => "#667085",
        "unsaved" => "#8A94A6",
        _ => "#5E86C5"
    };

    public static string GetIconBackground(string groupKey) => groupKey.ToLowerInvariant() switch
    {
        "images" => "#FFF1E4",
        "videos" => "#F0ECFF",
        "documents" => "#EAF2FF",
        "audio" => "#FCEAF1",
        "archives" => "#FFF4D9",
        "emails" => "#E8F8FB",
        "bookmarks" => "#EAF7EF",
        "other" => "#F1F3F6",
        "unsaved" => "#F4F6F8",
        _ => "#F2F6FC"
    };

    private static Dictionary<string, string> BuildGroupLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Group group in Groups.Where(g => g.Key is not "other" and not "unsaved"))
        {
            foreach (string extension in group.Extensions)
                lookup.TryAdd(NormalizeExtension(extension), group.Key);
        }

        return lookup;
    }
}
