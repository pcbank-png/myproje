using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace NSXVeriKurtarmaPro.Services;

public static class UpdateService
{
    public const string ProductCode = LicenseService.ProductCode;
    public const string ProductName = LicenseService.ProductName;

    private const string CheckUrl = "https://www.nsxyazilim.com/api/update/check";
    private const string UpdaterFileName = "NSXVeriKurtarmaPro.Updater.exe";

    public sealed class UpdateStatus
    {
        public bool HasUpdate { get; init; }
        public string CurrentVersion { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public bool Required { get; init; }
        public string Url { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public DateTime CheckedAt { get; init; } = DateTime.Now;
    }

    public static string GetCurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.0";

    public static async Task<UpdateStatus> GetUpdateStatusAsync(CancellationToken cancellationToken = default)
    {
        string currentVersion = GetCurrentVersion();
        string logPath = GetUpdateLogPath();

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"NSX-VeriKurtarmaPro/{currentVersion}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            string url = $"{CheckUrl}?productCode={Uri.EscapeDataString(ProductCode)}&version={Uri.EscapeDataString(currentVersion)}";
            AppendLog(logPath, "Güncelleme kontrolü: " + url);

            using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            AppendLog(logPath, $"HTTP {(int)response.StatusCode} • {Shorten(json, 1200)}");

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateStatus
                {
                    CurrentVersion = currentVersion,
                    ErrorMessage = "Güncelleme sunucusu isteği tamamlayamadı.",
                    CheckedAt = DateTime.Now
                };
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return new UpdateStatus
                {
                    CurrentVersion = currentVersion,
                    ErrorMessage = "Güncelleme sunucusu boş yanıt verdi.",
                    CheckedAt = DateTime.Now
                };
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = GetResponsePayload(document.RootElement);

            bool hasUpdate = ReadBool(root, false, "hasUpdate", "HasUpdate", "updateAvailable", "UpdateAvailable");
            string version = ReadString(root, "version", "Version", "latestVersion", "LatestVersion", "newVersion", "NewVersion");
            bool required = ReadBool(root, false, "required", "Required", "isRequired", "IsRequired", "mandatory", "Mandatory", "force", "Force");
            string updateUrl = ReadString(root, "url", "Url", "downloadUrl", "DownloadUrl", "packageUrl", "PackageUrl");
            string notes = ReadString(root, "notes", "Notes", "releaseNotes", "ReleaseNotes", "description", "Description");
            string sha256 = NormalizeSha256(ReadString(root, "sha256", "Sha256", "SHA256", "checksum", "Checksum", "hash", "Hash"));

            if (hasUpdate && !string.IsNullOrWhiteSpace(version) && !IsVersionNewer(version, currentVersion))
            {
                AppendLog(logPath, $"Sunucu HasUpdate döndürdü fakat sürüm daha yeni değil. Sunucu={version}, Mevcut={currentVersion}");
                hasUpdate = false;
            }

            if (hasUpdate && string.IsNullOrWhiteSpace(updateUrl))
            {
                return new UpdateStatus
                {
                    CurrentVersion = currentVersion,
                    Version = version,
                    Required = required,
                    ErrorMessage = "Yeni sürüm bulundu ancak indirme bağlantısı sunucudan gelmedi.",
                    CheckedAt = DateTime.Now
                };
            }

            return new UpdateStatus
            {
                HasUpdate = hasUpdate,
                CurrentVersion = currentVersion,
                Version = version,
                Required = required,
                Url = updateUrl,
                Notes = notes,
                Sha256 = sha256,
                CheckedAt = DateTime.Now
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateStatus
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Güncelleme kontrolü iptal edildi.",
                CheckedAt = DateTime.Now
            };
        }
        catch (TaskCanceledException)
        {
            return new UpdateStatus
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Güncelleme sunucusu zaman aşımına uğradı.",
                CheckedAt = DateTime.Now
            };
        }
        catch (HttpRequestException ex)
        {
            AppendLog(logPath, "Bağlantı hatası: " + ex.Message);
            return new UpdateStatus
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Güncelleme sunucusuna bağlanılamadı.",
                CheckedAt = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            AppendLog(logPath, "Genel hata: " + ex);
            return new UpdateStatus
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Güncelleme denetlenemedi.",
                CheckedAt = DateTime.Now
            };
        }
    }

    public static bool StartUpdate(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return StartUpdate(status.Url, status.Version, status.Sha256);
    }

    public static bool StartUpdate(string downloadUrl, string version, string? sha256 = null)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                LocalizationService.Current.Translate("Güncelleme indirme bağlantısı geçersiz veya güvenli HTTPS bağlantısı değil."),
                LocalizationService.Current.Translate("Güncelleme"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string updaterExe = Path.Combine(appDir, UpdaterFileName);
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(appDir, "NSXVeriKurtarmaPro.exe");
        string startLog = GetUpdateStartLogPath();

        try
        {
            File.WriteAllText(startLog,
                $"Updater başlatılıyor...{Environment.NewLine}" +
                $"Url: {downloadUrl}{Environment.NewLine}" +
                $"AppDir: {appDir}{Environment.NewLine}" +
                $"Exe: {currentExe}{Environment.NewLine}" +
                $"Version: {version}{Environment.NewLine}" +
                $"Sha256: {sha256}{Environment.NewLine}" +
                $"Pid: {Environment.ProcessId}{Environment.NewLine}" +
                $"UpdaterExe: {updaterExe}{Environment.NewLine}");
        }
        catch
        {
        }

        if (!File.Exists(updaterExe))
        {
            MessageBox.Show(
                LocalizationService.Current.Translate("Güncelleme yardımcısı bulunamadı. Programı resmi kurulum paketiyle yeniden kurun."),
                LocalizationService.Current.Translate("Güncelleme"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = updaterExe,
                WorkingDirectory = appDir,
                UseShellExecute = true,
                Arguments = BuildArguments(downloadUrl, appDir, currentExe, version, sha256)
            };

            Process? updater = Process.Start(psi);
            AppendLog(startLog, "Updater başlatıldı. PID: " + (updater?.Id.ToString() ?? "bilinmiyor"));
            return updater is not null;
        }
        catch (Exception ex)
        {
            AppendLog(startLog, "Updater başlatma hatası: " + ex);
            MessageBox.Show(
                LocalizationService.Current.Translate("Güncelleme başlatılamadı."),
                LocalizationService.Current.Translate("Güncelleme"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    public static async Task CheckAndPromptAsync(Window? owner = null, bool showNoUpdateMessage = false)
    {
        UpdateStatus status = await GetUpdateStatusAsync();

        if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
        {
            if (showNoUpdateMessage)
            {
                MessageBox.Show(
                    owner,
                    LocalizationService.Current.Translate(status.ErrorMessage),
                    LocalizationService.Current.Translate("Güncellemeleri Denetle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return;
        }

        if (!status.HasUpdate)
        {
            if (showNoUpdateMessage)
            {
                MessageBox.Show(
                    owner,
                    LocalizationService.Current.Translate("NSX Veri Kurtarma Pro güncel. Yeni bir sürüm bulunamadı."),
                    LocalizationService.Current.Translate("Güncellemeleri Denetle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        string detail = string.Format(
            LocalizationService.Current.Translate("Yeni sürüm hazır: {0}\nMevcut sürüm: {1}"),
            status.Version,
            status.CurrentVersion);
        if (!string.IsNullOrWhiteSpace(status.Notes))
            detail += Environment.NewLine + Environment.NewLine + status.Notes;
        detail += Environment.NewLine + Environment.NewLine + LocalizationService.Current.Translate("Güncellemeyi şimdi indirip kurmak ister misiniz?");

        MessageBoxResult answer = MessageBox.Show(
            owner,
            detail,
            LocalizationService.Current.Translate(status.Required ? "Zorunlu Güncelleme" : "Güncelleme Hazır"),
            MessageBoxButton.YesNo,
            status.Required ? MessageBoxImage.Warning : MessageBoxImage.Information,
            status.Required ? MessageBoxResult.Yes : MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            if (StartUpdate(status))
                Application.Current.Shutdown();
            return;
        }

        if (status.Required)
            Application.Current.Shutdown();
    }

    private static string BuildArguments(string downloadUrl, string appDir, string currentExe, string version, string? sha256)
    {
        string languageCode = LocalizationService.Current.CurrentLanguage?.Code ?? "tr-TR";
        string arguments =
            $"--url {Quote(downloadUrl)} " +
            $"--appDir {Quote(appDir)} " +
            $"--exe {Quote(currentExe)} " +
            $"--pid {Environment.ProcessId} " +
            $"--version {Quote(version)} " +
            $"--language {Quote(languageCode)}";

        if (!string.IsNullOrWhiteSpace(sha256))
            arguments += $" --sha256 {Quote(NormalizeSha256(sha256))}";

        return arguments;
    }

    private static bool IsVersionNewer(string candidateText, string currentText)
    {
        if (!Version.TryParse(NormalizeVersion(candidateText), out Version? candidate) ||
            !Version.TryParse(NormalizeVersion(currentText), out Version? current))
        {
            return true;
        }

        return candidate > current;
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        int separator = normalized.IndexOfAny(new[] { '-', '+' });
        if (separator >= 0)
            normalized = normalized[..separator];

        List<string> parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 4)
            parts.Add("0");

        return string.Join('.', parts.Take(4));
    }

    private static JsonElement GetResponsePayload(JsonElement root)
    {
        foreach (string name in new[] { "data", "Data", "result", "Result", "update", "Update" })
        {
            if (root.TryGetProperty(name, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                return nested;
        }

        return root;
    }

    private static bool ReadBool(JsonElement root, bool defaultValue, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                continue;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (bool.TryParse(value.ToString(), out bool parsed))
                return parsed;
            if (int.TryParse(value.ToString(), out int number))
                return number != 0;
        }

        return defaultValue;
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                continue;

            string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return string.Empty;
    }

    private static string NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 64 ? normalized : string.Empty;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Shorten(string? value, int maxLength)
    {
        string text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string GetUpdateLogPath() => Path.Combine(GetLogsFolder(), "update-check.log");
    private static string GetUpdateStartLogPath() => Path.Combine(GetLogsFolder(), "update-start.log");

    private static string GetLogsFolder()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NSX Yazılım",
            "NSX Veri Kurtarma Pro",
            "Logs");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
