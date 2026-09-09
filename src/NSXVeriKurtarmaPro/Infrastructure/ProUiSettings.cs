using System.IO;
using System.Text.Json;

namespace NSXVeriKurtarmaPro.Infrastructure;

public sealed class ProWindowLayoutState
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width >= 320d &&
        Height >= 240d;
}

public sealed class ProUiSettingsState
{
    public bool StartInWorkArea { get; set; } = true;
    public bool RememberMainWindowLayout { get; set; } = true;
    public bool RememberPreviewWindowLayout { get; set; } = true;
    public bool AutoRefreshDevices { get; set; } = true;
    public bool AutoGenerateThumbnails { get; set; } = true;
    public double RecoveryNavigationPaneWidth { get; set; } = 250d;
    public ProWindowLayoutState MainWindowLayout { get; set; } = new();
    public ProWindowLayoutState PreviewWindowLayout { get; set; } = new();
}

public static class ProUiSettings
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static ProUiSettingsState _current = LoadCore();

    public static ProUiSettingsState Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static event EventHandler? SettingsChanged;

    public static void Update(Action<ProUiSettingsState> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (Sync)
        {
            update(_current);
            SaveCore(_current);
        }

        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _current = new ProUiSettingsState();
            SaveCore(_current);
        }

        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SaveMainWindowLayout(double left, double top, double width, double height)
    {
        lock (Sync)
        {
            if (!_current.RememberMainWindowLayout)
                return;

            _current.MainWindowLayout.Left = left;
            _current.MainWindowLayout.Top = top;
            _current.MainWindowLayout.Width = width;
            _current.MainWindowLayout.Height = height;
            SaveCore(_current);
        }
    }

    public static void SavePreviewWindowLayout(double left, double top, double width, double height)
    {
        lock (Sync)
        {
            if (!_current.RememberPreviewWindowLayout)
                return;

            _current.PreviewWindowLayout.Left = left;
            _current.PreviewWindowLayout.Top = top;
            _current.PreviewWindowLayout.Width = width;
            _current.PreviewWindowLayout.Height = height;
            SaveCore(_current);
        }
    }

    private static ProUiSettingsState LoadCore()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
                return new ProUiSettingsState();

            ProUiSettingsState? loaded = JsonSerializer.Deserialize<ProUiSettingsState>(File.ReadAllText(path));
            if (loaded is null)
                return new ProUiSettingsState();

            loaded.MainWindowLayout ??= new ProWindowLayoutState();
            loaded.PreviewWindowLayout ??= new ProWindowLayoutState();
            return loaded;
        }
        catch
        {
            return new ProUiSettingsState();
        }
    }

    private static void SaveCore(ProUiSettingsState settings)
    {
        try
        {
            string path = GetSettingsPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporaryPath, path, true);
        }
        catch
        {
            // Ayar kaydı program akışını hiçbir zaman kesmemelidir.
        }
    }

    private static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NSX Yazilim",
        "NSXVeriKurtarmaPro",
        "ui-settings.json");
}
