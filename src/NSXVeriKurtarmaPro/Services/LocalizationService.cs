using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NSXVeriKurtarmaPro.Services;

public sealed class LanguagePack
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string NativeName { get; init; } = string.Empty;
    public string FlagFile { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Translations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    internal IReadOnlyList<LocalizedPatternTranslation> PatternTranslations { get; init; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(NativeName) ? Name : NativeName;
}

/// <summary>
/// Dil paketlerini uygulamanın kendi dizinindeki Languages klasöründen okur.
/// Paketlerin anahtarları, uygulamadaki Türkçe kaynak metinlerin birebir kendisidir.
/// Böylece yeni bir dil eklemek için kod derlemek gerekmez; JSON + SVG bayrak yeterlidir.
/// </summary>

internal sealed class LocalizedPatternTranslation
{
    public required Regex Pattern { get; init; }
    public required string TargetTemplate { get; init; }
    public required int ArgumentCount { get; init; }
}

public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string DefaultLanguageCode = "tr-TR";
    // en-US, tum harici dil paketleri icin kanonik anahtar/fallback paketidir.
    // Yeni bir dil en-US.json baz alinarak hazirlandiginda tum statik ve dinamik
    // metinler ayni anahtar setiyle otomatik calisir.
    private const string CanonicalFallbackLanguageCode = "en-US";
    private const string SettingsFileName = "language.json";

    // Final safety net for non-Turkish packs. Normal UI text should always resolve through
    // en-US.json first; this guard only prevents a newly introduced engine/status string
    // from ever falling back to raw Turkish in an English/global session.
    private static readonly Regex TurkishUiTextRegex = new(
        @"[çğıöşüÇĞİÖŞÜ]|\b(?:veri|kurtarma|lisans|tarama|dosya|klasor|klasör|kaynak|hedef|surucu|sürücü|bolum|bölüm|guncelleme|güncelleme|hakkinda|hakkında|urun|ürün|surum|sürüm|bekleniyor|bulundu|secim|seçim|guven|güven|resim|yazilim|yazılım|baslangic|başlangıç|hazir|hazır|onar|referans|kayip|kayıp|bagli|bağlı|bos|boş|toplam|bilinmiyor|silinmis|silinmiş|mevcut|kayit|kayıt|dogrula|doğrula|kullanici|kullanıcı|durdur|duraklat|devam|tamamlandi|tamamlandı|yukleniyor|yükleniyor|aciliyor|açılıyor|uygulama|bilesen|bileşen|cevrimici|çevrimiçi|sifre|şifre|anahtar|onizleme|önizleme)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed class LanguagePackFile
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? NativeName { get; set; }
        public string? Flag { get; set; }
        public Dictionary<string, string>? Translations { get; set; }
    }

    private sealed class LanguageSetting
    {
        public string? Code { get; set; }
    }

    private sealed class OriginalTextState
    {
        public Dictionary<DependencyProperty, string> Values { get; } = [];
    }

    private readonly object _sync = new();
    private readonly ObservableCollection<LanguagePack> _languages = [];
    private readonly ReadOnlyObservableCollection<LanguagePack> _readOnlyLanguages;
    private readonly ConditionalWeakTable<DependencyObject, OriginalTextState> _originalTexts = new();
    private readonly Dictionary<string, ImageSource?> _flagCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _canonicalSourcesByTranslation = new(StringComparer.Ordinal);
    private bool _initialized;
    private LanguagePack? _currentLanguage;
    private int _revision;

    private LocalizationService()
    {
        _readOnlyLanguages = new ReadOnlyObservableCollection<LanguagePack>(_languages);
    }

    public static LocalizationService Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public ReadOnlyObservableCollection<LanguagePack> AvailableLanguages => _readOnlyLanguages;

    public LanguagePack? CurrentLanguage => _currentLanguage;

    public int Revision => _revision;

    public string LanguagesDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        ReloadLanguages(raiseLanguageChanged: false);

        string? savedCode = LoadSavedLanguageCode();
        string? preferredCode = ResolvePreferredLanguageCode(savedCode);
        SetLanguage(preferredCode ?? DefaultLanguageCode, persist: false, raiseLanguageChanged: false);
    }

    /// <summary>
    /// Klasör her menü açılışında tekrar okunabildiği için uygulama çalışırken kopyalanan
    /// yeni dil paketi de yeniden başlatma gerektirmeden görünür.
    /// </summary>
    public void ReloadLanguages(bool raiseLanguageChanged = true)
    {
        lock (_sync)
        {
            string? currentCode = _currentLanguage?.Code;
            List<LanguagePack> discovered = DiscoverLanguagePacks();

            _languages.Clear();
            foreach (LanguagePack language in discovered)
                _languages.Add(language);

            _flagCache.Clear();
            RebuildCanonicalSourceIndex();

            LanguagePack? refreshedCurrent = FindLanguage(currentCode)
                ?? FindLanguage(DefaultLanguageCode)
                ?? _languages.FirstOrDefault();

            _currentLanguage = refreshedCurrent;
            _revision++;
        }

        OnPropertyChanged(nameof(AvailableLanguages));
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged("Item[]");

        if (raiseLanguageChanged)
            LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool SetLanguage(string? code, bool persist = true, bool raiseLanguageChanged = true)
    {
        if (!_initialized)
            Initialize();

        LanguagePack? selected = FindLanguage(code)
            ?? FindLanguage(DefaultLanguageCode)
            ?? _languages.FirstOrDefault();

        if (selected is null)
            return false;

        bool changed = !string.Equals(_currentLanguage?.Code, selected.Code, StringComparison.OrdinalIgnoreCase);
        _currentLanguage = selected;
        _revision++;

        if (persist)
            SaveLanguageCode(selected.Code);

        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged("Item[]");

        if (raiseLanguageChanged && (changed || persist))
            LanguageChanged?.Invoke(this, EventArgs.Empty);

        return true;
    }

    public string Translate(string? sourceText) => TranslateCore(NormalizeSourceText(sourceText), 0);

    /// <summary>
    /// Bir kontrol ilk kez seçili dilde yüklendiyse görünen çeviriyi yeniden Türkçe
    /// kanonik anahtara çözer. Böylece geç oluşturulan şablonlar EN -> TR geçişinde
    /// PATH/TYPE gibi önceki dil değerlerini "orijinal metin" olarak kilitlemez.
    /// </summary>
    public string NormalizeSourceText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        lock (_sync)
        {
            // Metin zaten bir kaynak anahtarsa ters eşleme uygulama. Aynı sözcüğün
            // başka bir pakette çeviri değeri olması kanonik kaynağı değiştirmemeli.
            if (_languages.Any(language => language.Translations.ContainsKey(text)))
                return text;

            return _canonicalSourcesByTranslation.TryGetValue(text, out string? source)
                ? source
                : text;
        }
    }

    private string TranslateCore(string? sourceText, int depth)
    {
        if (string.IsNullOrEmpty(sourceText))
            return sourceText ?? string.Empty;

        LanguagePack? language = _currentLanguage;
        if (language is null)
            return sourceText;

        if (TryTranslateWithPack(language, sourceText, depth, out string? translated))
            return translated;

        // Türkçe paket yalnızca İngilizce/karma kaynakların temiz Türkçe karşılıklarını
        // taşımak zorundadır. Anahtar yoksa zaten Türkçe olan kanonik kaynak gösterilir.
        if (string.Equals(language.Code, DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
            return sourceText;

        // Harici bir dil paketi en-US.json'daki yeni bir anahtari henuz icermiyorsa
        // kullaniciya Turkce sizmasin. Kanonik Ingilizce pakete dus. Tam cevrilmis
        // paketlerde bu fallback hic devreye girmez.
        if (!string.Equals(language.Code, CanonicalFallbackLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            LanguagePack? englishFallback = FindLanguage(CanonicalFallbackLanguageCode);
            if (englishFallback is not null &&
                TryTranslateWithPack(englishFallback, sourceText, depth, out translated))
            {
                return translated;
            }
        }

        if (LooksLikeTurkishUiText(sourceText))
            return BuildEnglishLeakFallback(sourceText);

        return sourceText;
    }

    private static bool LooksLikeTurkishUiText(string sourceText)
        => TurkishUiTextRegex.IsMatch(sourceText);

    private static string BuildEnglishLeakFallback(string sourceText)
    {
        string normalized = sourceText.ToLowerInvariant();

        if (normalized.Contains("lisans") || normalized.Contains("license"))
            return "License service status updated.";
        if (normalized.Contains("güncelle") || normalized.Contains("guncelle"))
            return "Update status updated.";
        if (normalized.Contains("başarısız") || normalized.Contains("basarisiz") ||
            normalized.Contains("hata") || normalized.Contains("bulunamad") ||
            normalized.Contains("doğrulanamad") || normalized.Contains("dogrulanamad") ||
            normalized.Contains("açılamad") || normalized.Contains("acilamad") ||
            normalized.Contains("oluşturulamad") || normalized.Contains("olusturulamad") ||
            normalized.Contains("kullanılam") || normalized.Contains("kullanilam") ||
            normalized.Contains("geçersiz") || normalized.Contains("gecersiz"))
        {
            return "A recovery operation could not be completed.";
        }
        if (normalized.Contains("tamamland") || normalized.Contains("doğruland") || normalized.Contains("dogruland"))
            return "Recovery operation completed.";
        if (normalized.Contains("bekleniyor"))
            return "Waiting for the recovery operation.";
        if (normalized.Contains("duraklat"))
            return "Recovery operation paused.";
        if (normalized.Contains("durdur"))
            return "Recovery operation stopped.";
        if (normalized.Contains("video") && normalized.Contains("onar"))
            return "Repairing and validating video data...";
        if ((normalized.Contains("resim") || normalized.Contains("görsel") || normalized.Contains("gorsel")) && normalized.Contains("onar"))
            return "Repairing and validating image data...";
        if (normalized.Contains("öniz") || normalized.Contains("oniz"))
            return "Preparing file preview...";
        if (normalized.Contains("tarama") || normalized.Contains("taran") || normalized.Contains("analiz"))
            return "Scanning and analyzing recovery data...";
        if (normalized.Contains("kurtar"))
            return "Processing recovery data...";
        if (normalized.Contains("hazırla") || normalized.Contains("hazirla") ||
            normalized.Contains("yüklen") || normalized.Contains("yuklen") ||
            normalized.Contains("açılı") || normalized.Contains("acili"))
        {
            return "Preparing recovery components...";
        }

        return "Recovery operation status updated.";
    }

    private bool TryTranslateWithPack(LanguagePack language, string sourceText, int depth, out string translated)
    {
        if (language.Translations.TryGetValue(sourceText, out string? direct) && !string.IsNullOrWhiteSpace(direct))
        {
            translated = direct;
            return true;
        }

        if (depth < 4)
        {
            foreach (LocalizedPatternTranslation pattern in language.PatternTranslations)
            {
                Match match = pattern.Pattern.Match(sourceText);
                if (!match.Success)
                    continue;

                var args = new object[pattern.ArgumentCount];
                for (int index = 0; index < args.Length; index++)
                {
                    Group group = match.Groups[$"p{index}"];
                    args[index] = group.Success ? TranslateCore(group.Value, depth + 1) : string.Empty;
                }

                try
                {
                    translated = string.Format(System.Globalization.CultureInfo.CurrentCulture, pattern.TargetTemplate, args);
                }
                catch (FormatException)
                {
                    translated = pattern.TargetTemplate;
                }

                return true;
            }
        }

        translated = string.Empty;
        return false;
    }

    public ImageSource? GetFlagImage(LanguagePack? language, int maxPixels = 96)
    {
        if (language is null || string.IsNullOrWhiteSpace(language.FlagFile))
            return null;

        string cacheKey = $"{language.FlagFile}|{maxPixels}";
        lock (_sync)
        {
            if (_flagCache.TryGetValue(cacheKey, out ImageSource? cached))
                return cached;
        }

        ImageSource? image = null;
        try
        {
            if (File.Exists(language.FlagFile) &&
                string.Equals(Path.GetExtension(language.FlagFile), ".svg", StringComparison.OrdinalIgnoreCase))
            {
                image = GlobalImageCodec.LoadPreview(language.FlagFile, Math.Clamp(maxPixels, 24, 512));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Dil bayrağı yüklenemedi: {language.FlagFile}", ex);
        }

        lock (_sync)
            _flagCache[cacheKey] = image;

        return image;
    }

    /// <summary>
    /// XAML içinde doğrudan yazılmış (binding olmayan) metinleri çevirir.
    /// İlk görülen Türkçe değer saklanır; böylece kullanıcı TR -> EN -> TR arasında
    /// geçerken çeviri üstüne çeviri yapılmaz.
    /// </summary>
    public void ApplyElement(FrameworkElement element)
    {
        if (element is Window window)
            LocalizeProperty(window, Window.TitleProperty);

        if (element is TextBlock textBlock)
            LocalizeProperty(textBlock, TextBlock.TextProperty);

        if (element is ContentControl contentControl)
            LocalizeProperty(contentControl, ContentControl.ContentProperty);

        if (element is HeaderedContentControl headeredContentControl)
            LocalizeProperty(headeredContentControl, HeaderedContentControl.HeaderProperty);

        if (element is HeaderedItemsControl headeredItemsControl)
            LocalizeProperty(headeredItemsControl, HeaderedItemsControl.HeaderProperty);

        LocalizeProperty(element, ToolTipService.ToolTipProperty);
    }

    public void ApplyWindow(Window window)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        WalkVisualTree(window, visited);
    }

    private void WalkVisualTree(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is FrameworkElement element)
        {
            ApplyElement(element);

            if (element.ContextMenu is ContextMenu contextMenu)
                WalkVisualTree(contextMenu, visited);
        }

        int visualChildren = 0;
        try
        {
            visualChildren = VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
        }

        for (int index = 0; index < visualChildren; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, index);
            WalkVisualTree(child, visited);
        }

        if (node is ItemsControl itemsControl)
        {
            foreach (object item in itemsControl.Items)
            {
                if (item is DependencyObject dependencyObject)
                    WalkVisualTree(dependencyObject, visited);
            }
        }
    }

    private void LocalizeProperty(DependencyObject target, DependencyProperty property)
    {
        if (BindingOperations.IsDataBound(target, property))
            return;

        object value = target.GetValue(property);
        if (value is not string current || string.IsNullOrWhiteSpace(current))
            return;

        OriginalTextState state = _originalTexts.GetOrCreateValue(target);
        if (!state.Values.TryGetValue(property, out string? source))
        {
            source = NormalizeSourceText(current);
            state.Values[property] = source;
        }

        string translated = Translate(source);
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            target.SetValue(property, translated);
    }

    private List<LanguagePack> DiscoverLanguagePacks()
    {
        var result = new List<LanguagePack>();
        string root = LanguagesDirectory;

        try
        {
            Directory.CreateDirectory(root);
        }
        catch
        {
            // Program Files altında normal kullanıcı yazamayabilir; klasör publish ile zaten gelir.
        }

        if (!Directory.Exists(root))
            return result;

        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using FileStream stream = File.OpenRead(file);
                LanguagePackFile? data = JsonSerializer.Deserialize<LanguagePackFile>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                string code = (data?.Code ?? string.Empty).Trim();
                string nativeName = (data?.NativeName ?? string.Empty).Trim();
                string name = (data?.Name ?? nativeName).Trim();
                string relativeFlag = (data?.Flag ?? string.Empty).Trim();

                if (code.Length is < 2 or > 32 || nativeName.Length == 0)
                    continue;

                if (result.Any(existing => string.Equals(existing.Code, code, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string flagPath = string.Empty;
                if (!string.IsNullOrWhiteSpace(relativeFlag))
                {
                    string candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, relativeFlag));
                    if (candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetExtension(candidate), ".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        flagPath = candidate;
                    }
                }

                var translations = new Dictionary<string, string>(StringComparer.Ordinal);
                if (data?.Translations is not null)
                {
                    foreach ((string source, string translated) in data.Translations)
                    {
                        if (!string.IsNullOrWhiteSpace(source) && translated is not null)
                            translations[source] = translated;
                    }
                }

                result.Add(new LanguagePack
                {
                    Code = code,
                    Name = name.Length == 0 ? nativeName : name,
                    NativeName = nativeName,
                    FlagFile = flagPath,
                    SourceFile = file,
                    Translations = translations,
                    PatternTranslations = BuildPatternTranslations(translations)
                });
            }
            catch (Exception ex)
            {
                AppLog.Error($"Dil paketi okunamadı: {file}", ex);
            }
        }

        return result
            .OrderBy(language => GetLanguageSortOrder(language.Code))
            .ThenBy(language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<LocalizedPatternTranslation> BuildPatternTranslations(
        IReadOnlyDictionary<string, string> translations)
    {
        var patterns = new List<(int LiteralLength, LocalizedPatternTranslation Translation)>();
        var placeholderRegex = new Regex(@"\{(?<index>\d+)\}", RegexOptions.CultureInvariant);

        foreach ((string source, string target) in translations)
        {
            MatchCollection placeholders = placeholderRegex.Matches(source);
            if (placeholders.Count == 0)
                continue;

            int maxIndex = placeholders.Cast<Match>()
                .Select(match => int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(-1)
                .Max();
            if (maxIndex < 0 || maxIndex > 15)
                continue;

            var regex = new StringBuilder("^");
            int cursor = 0;
            var seen = new HashSet<int>();
            foreach (Match placeholder in placeholders)
            {
                regex.Append(Regex.Escape(source[cursor..placeholder.Index]));
                int index = int.Parse(placeholder.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture);
                regex.Append(seen.Add(index) ? $"(?<p{index}>.+?)" : $"\\k<p{index}>");
                cursor = placeholder.Index + placeholder.Length;
            }
            regex.Append(Regex.Escape(source[cursor..]));
            regex.Append('$');

            int literalLength = placeholderRegex.Replace(source, string.Empty).Length;
            patterns.Add((literalLength, new LocalizedPatternTranslation
            {
                Pattern = new Regex(regex.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant),
                TargetTemplate = target,
                ArgumentCount = maxIndex + 1
            }));
        }

        return patterns
            .OrderByDescending(item => item.LiteralLength)
            .Select(item => item.Translation)
            .ToList();
    }

    private void RebuildCanonicalSourceIndex()
    {
        _canonicalSourcesByTranslation.Clear();

        foreach (LanguagePack language in _languages)
        {
            foreach ((string source, string translated) in language.Translations)
            {
                if (string.IsNullOrWhiteSpace(translated) ||
                    string.Equals(source, translated, StringComparison.Ordinal))
                {
                    continue;
                }

                _canonicalSourcesByTranslation.TryAdd(translated, source);
            }
        }
    }

    private LanguagePack? FindLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        return _languages.FirstOrDefault(language =>
            string.Equals(language.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolvePreferredLanguageCode(string? savedCode)
    {
        // Mevcut ürün davranışı Türkçe açılıştır. Kullanıcı bir dil seçtiyse onu hatırla;
        // ilk kurulumda Windows diline göre sürpriz bir dil değişimi yapma.
        if (FindLanguage(savedCode) is not null)
            return savedCode;

        return FindLanguage(DefaultLanguageCode)?.Code
            ?? _languages.FirstOrDefault()?.Code;
    }

    private string? LoadSavedLanguageCode()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
                return null;

            LanguageSetting? setting = JsonSerializer.Deserialize<LanguageSetting>(File.ReadAllText(path));
            return setting?.Code;
        }
        catch
        {
            return null;
        }
    }

    private void SaveLanguageCode(string code)
    {
        try
        {
            string path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new LanguageSetting { Code = code }));
        }
        catch (Exception ex)
        {
            AppLog.Error("Dil tercihi kaydedilemedi.", ex);
        }
    }

    private static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NSX Yazılım",
        "NSX Veri Kurtarma Pro",
        SettingsFileName);

    private static int GetLanguageSortOrder(string code)
    {
        if (string.Equals(code, "tr-TR", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(code, "en-US", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "en-GB", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 10;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
