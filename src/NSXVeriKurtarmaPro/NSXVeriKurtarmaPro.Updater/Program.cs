using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace NSXVeriKurtarmaPro.Updater;

internal static class Program
{
    private const string MainExeName = "NSXVeriKurtarmaPro.exe";
    private const string UpdaterExeName = "NSXVeriKurtarmaPro.Updater.exe";

    private static string _appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static string _mainExePath = Path.Combine(_appDir, MainExeName);
    private static string _logPath = Path.Combine(GetLogsFolder(), "update-log.txt");
    private static string _errorPath = Path.Combine(GetLogsFolder(), "update-error.log");
    private static Form? _form;
    private static Label? _label;
    private static ProgressBar? _bar;
    private static int _lastShownPercent = -1;
    private static string _lastShownMessage = string.Empty;
    private static string _languageCode = "tr-TR";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Dictionary<string, string> initialArgs = ParseArgs(args);
            _languageCode = initialArgs.GetValueOrDefault("language", "tr-TR");
            CreateWindow();
            RunAsync(args).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log("HATA: " + ex);
            try { File.WriteAllText(_errorPath, ex.ToString()); } catch { }
            ShowStatus(T("Güncelleme tamamlanamadı. Program tekrar açılıyor..."), 100, forceLog: true);
            TryRestartAfterFailure();
            Thread.Sleep(1800);
            return 1;
        }
        finally
        {
            try { _form?.Close(); } catch { }
        }
    }

    private static async Task RunAsync(string[] args)
    {
        Dictionary<string, string> argsMap = ParseArgs(args);
        string url = argsMap.GetValueOrDefault("url", string.Empty);
        _appDir = NormalizeDirectory(argsMap.GetValueOrDefault("appDir", AppContext.BaseDirectory));
        _mainExePath = argsMap.GetValueOrDefault("exe", Path.Combine(_appDir, MainExeName));
        int pid = int.TryParse(argsMap.GetValueOrDefault("pid", "0"), out int parsedPid) ? parsedPid : 0;
        string version = argsMap.GetValueOrDefault("version", string.Empty);
        string expectedSha256 = NormalizeSha256(argsMap.GetValueOrDefault("sha256", string.Empty));

        _logPath = Path.Combine(GetLogsFolder(), "update-log.txt");
        _errorPath = Path.Combine(GetLogsFolder(), "update-error.log");

        Directory.CreateDirectory(_appDir);
        Log("Updater başladı.");
        Log("URL: " + url);
        Log("AppDir: " + _appDir);
        Log("Exe: " + _mainExePath);
        Log("Version: " + version);
        Log("PID: " + pid);
        Log("SHA256: " + (string.IsNullOrWhiteSpace(expectedSha256) ? "sunucudan gelmedi" : expectedSha256));

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Güncelleme indirme bağlantısı güvenli bir HTTPS adresi değil.");
        }

        ShowStatus(T("Ana program kapanıyor..."), 5, forceLog: true);
        WaitForMainProcess(pid);
        await Task.Delay(900);

        string tempRoot = Path.Combine(GetTempFolder(), "NSXVeriKurtarmaProUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string zipPath = Path.Combine(tempRoot, "update.zip");
        string extractPath = Path.Combine(tempRoot, "extract");

        try
        {
            ShowStatus(T("Güncelleme indiriliyor..."), 10, forceLog: true);
            await DownloadWithRetryAsync(url, zipPath);

            if (!string.IsNullOrWhiteSpace(expectedSha256))
                ValidateSha256(zipPath, expectedSha256);

            ValidateZipFile(zipPath);

            ShowStatus(T("Güncelleme paketi doğrulanıyor..."), 60, forceLog: true);
            ValidateArchivePaths(zipPath, extractPath);
            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);

            string payloadRoot = FindPayloadRoot(extractPath);
            Log("Kopyalama kökü: " + payloadRoot);

            string payloadExe = Path.Combine(payloadRoot, MainExeName);
            ValidatePayloadVersion(payloadExe, version);

            List<string> files = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0)
                throw new InvalidOperationException("Güncelleme paketinde dosya bulunamadı.");

            ShowStatus(T("Dosyalar değiştiriliyor..."), 70, forceLog: true);
            int copied = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string sourcePath = files[i];
                string relative = Path.GetRelativePath(payloadRoot, sourcePath);
                if (ShouldSkip(relative))
                {
                    Log("Atlandı: " + relative);
                    continue;
                }

                string targetPath = Path.Combine(_appDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                if (IsUpdaterFile(Path.GetFileName(targetPath)))
                {
                    Log("Çalışan updater atlandı: " + relative);
                    continue;
                }

                CopyFileSafe(sourcePath, targetPath);
                copied++;
                Log("Kopyalandı: " + relative);

                int percent = 70 + (int)Math.Min(22, ((long)(i + 1) * 22 / files.Count));
                ShowStatus(T("Dosyalar değiştiriliyor... %{0}", percent), percent);
            }

            Log("Toplam kopyalanan dosya: " + copied);
            ValidatePayloadVersion(_mainExePath, version);

            ShowStatus(T("Program yeniden başlatılıyor..."), 96, forceLog: true);
            bool restarted = RestartMainApplication(_mainExePath, _appDir);
            ShowStatus(
                restarted ? T("Güncelleme tamamlandı. Program açılıyor...") : T("Güncelleme tamamlandı. Programı manuel açabilirsiniz."),
                100,
                forceLog: true);
            Log("Updater başarıyla tamamlandı. Restart=" + restarted);
            Thread.Sleep(1400);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void WaitForMainProcess(int pid)
    {
        if (pid <= 0)
            return;

        try
        {
            Process process = Process.GetProcessById(pid);
            if (!process.WaitForExit(30000))
                throw new TimeoutException("Ana program 30 saniye içinde kapanmadı.");
            Log("Ana program kapandı.");
        }
        catch (ArgumentException)
        {
            Log("Ana program zaten kapalı.");
        }
    }

    private static async Task DownloadWithRetryAsync(string url, string zipPath)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                Log($"İndirme denemesi: {attempt}/3");
                await DownloadFileAsync(url, zipPath);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"İndirme denemesi başarısız ({attempt}/3): {ex.Message}");
                if (attempt < 3)
                {
                    ShowStatus(T("İndirme tekrar deneniyor... ({0}/3)", attempt + 1), 14, forceLog: true);
                    await Task.Delay(1200 * attempt);
                }
            }
        }

        throw new InvalidOperationException("Güncelleme dosyası indirilemedi.", lastError);
    }

    private static async Task DownloadFileAsync(string url, string zipPath)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NSXVeriKurtarmaProUpdater/1.3.0");

        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(
            zipPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 256,
            useAsync: true);

        byte[] buffer = new byte[1024 * 256];
        long readTotal = 0;
        int read;
        int lastPercent = -1;

        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;

            if (total.HasValue && total.Value > 0)
            {
                int percent = 10 + (int)Math.Min(46, readTotal * 46 / total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    ShowStatus(T("Güncelleme indiriliyor... %{0}", percent), percent);
                }
            }
        }

        await output.FlushAsync();
        Log("ZIP indirildi. Boyut: " + readTotal + " byte");

        if (total.HasValue && total.Value > 0 && readTotal != total.Value)
            throw new InvalidOperationException($"İndirilen dosya boyutu eksik. Beklenen: {total.Value}, Gelen: {readTotal}");
    }

    private static void ValidateSha256(string zipPath, string expectedSha256)
    {
        using FileStream stream = File.OpenRead(zipPath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Güncelleme paketi SHA-256 doğrulamasından geçemedi.");

        Log("SHA-256 doğrulaması başarılı.");
    }

    private static void ValidateZipFile(string zipPath)
    {
        var zipInfo = new FileInfo(zipPath);
        if (!zipInfo.Exists || zipInfo.Length < 1024)
            throw new InvalidOperationException("İndirilen güncelleme dosyası çok küçük veya boş.");

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidOperationException("İndirilen ZIP dosyası boş.");

        bool hasMainExe = archive.Entries.Any(entry =>
            string.Equals(Path.GetFileName(entry.FullName), MainExeName, StringComparison.OrdinalIgnoreCase));
        if (!hasMainExe)
            throw new InvalidOperationException("Güncelleme paketinde NSX Veri Kurtarma Pro ana programı bulunamadı.");

        Log("ZIP doğrulandı. Entry sayısı: " + archive.Entries.Count);
    }

    private static void ValidateArchivePaths(string zipPath, string extractPath)
    {
        string root = Path.GetFullPath(extractPath + Path.DirectorySeparatorChar);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Güncelleme paketinde güvenli olmayan dosya yolu bulundu.");
        }
    }

    private static void ValidatePayloadVersion(string exePath, string expectedVersion)
    {
        if (!File.Exists(exePath))
            throw new InvalidOperationException("Güncelleme paketinde ana program bulunamadı: " + exePath);

        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            Log("Hedef sürüm belirtilmediği için EXE sürüm doğrulaması atlandı.");
            return;
        }

        string actualText = FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? string.Empty;
        if (!Version.TryParse(NormalizeVersion(expectedVersion), out Version? expected) ||
            !Version.TryParse(NormalizeVersion(actualText), out Version? actual))
        {
            throw new InvalidOperationException(
                $"Güncelleme sürümü doğrulanamadı. Beklenen: {expectedVersion}, paketteki: {actualText}");
        }

        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Yanlış güncelleme paketi indirildi. Beklenen sürüm: {expectedVersion}, paketteki sürüm: {actualText}");
        }

        Log("EXE sürüm doğrulaması başarılı: " + actual);
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

    private static string NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 64 ? normalized : string.Empty;
    }

    private static void CopyFileSafe(string sourcePath, string targetPath)
    {
        string tempTarget = targetPath + ".nsxnew";
        if (File.Exists(tempTarget))
            File.Delete(tempTarget);

        File.Copy(sourcePath, tempTarget, overwrite: true);

        var sourceInfo = new FileInfo(sourcePath);
        var tempInfo = new FileInfo(tempTarget);
        if (sourceInfo.Length != tempInfo.Length)
            throw new IOException("Dosya eksik kopyalandı: " + Path.GetFileName(targetPath));

        File.Copy(tempTarget, targetPath, overwrite: true);
        File.Delete(tempTarget);
    }

    private static bool RestartMainApplication(string exePath, string appDir)
    {
        if (!File.Exists(exePath))
        {
            Log("Ana exe bulunamadı: " + exePath);
            return false;
        }

        Thread.Sleep(1200);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = appDir,
                    UseShellExecute = true,
                    Verb = "open"
                });

                if (process is not null)
                {
                    Log("Program başlatıldı. PID: " + process.Id);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Program başlatma denemesi {attempt}/3 başarısız: {ex.Message}");
            }

            Thread.Sleep(1200);
        }

        return StartDelayedRestartLauncher(exePath, appDir);
    }

    private static bool StartDelayedRestartLauncher(string exePath, string appDir)
    {
        try
        {
            string launcherPath = Path.Combine(GetTempFolder(), "NSXVeriKurtarmaPro_Restart_" + Guid.NewGuid().ToString("N") + ".cmd");
            string launcher = string.Join(Environment.NewLine, new[]
            {
                "@echo off",
                "timeout /t 2 /nobreak >nul",
                $"cd /d \"{appDir}\"",
                $"start \"\" \"{exePath}\"",
                "exit /b 0"
            });

            File.WriteAllText(launcherPath, launcher);
            Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = appDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Log("Gecikmeli yeniden başlatma yardımcısı oluşturuldu.");
            return true;
        }
        catch (Exception ex)
        {
            Log("Gecikmeli yeniden başlatma hatası: " + ex);
            return false;
        }
    }

    private static void TryRestartAfterFailure()
    {
        try
        {
            if (File.Exists(_mainExePath))
                StartDelayedRestartLauncher(_mainExePath, _appDir);
        }
        catch
        {
        }
    }

    private static string T(string source, params object[] args)
    {
        string language = (_languageCode ?? "tr-TR").Split('-')[0].Trim().ToLowerInvariant();
        string translated = language switch
        {
            "en" => source switch
            {
                "NSX Veri Kurtarma Pro - Güncelleme" => "NSX Data Recovery Pro - Update",
                "Güncelleme hazırlanıyor..." => "Preparing update...",
                "Güncelleme tamamlanamadı. Program tekrar açılıyor..." => "The update could not be completed. Restarting the program...",
                "Ana program kapanıyor..." => "Closing the main program...",
                "Güncelleme indiriliyor..." => "Downloading update...",
                "Güncelleme indiriliyor... %{0}" => "Downloading update... {0}%",
                "Güncelleme paketi doğrulanıyor..." => "Verifying update package...",
                "Dosyalar değiştiriliyor..." => "Replacing files...",
                "Dosyalar değiştiriliyor... %{0}" => "Replacing files... {0}%",
                "Program yeniden başlatılıyor..." => "Restarting the program...",
                "Güncelleme tamamlandı. Program açılıyor..." => "Update completed. Opening the program...",
                "Güncelleme tamamlandı. Programı manuel açabilirsiniz." => "Update completed. You can open the program manually.",
                "İndirme tekrar deneniyor... ({0}/3)" => "Retrying download... ({0}/3)",
                _ => source
            },
            "de" => source switch
            {
                "NSX Veri Kurtarma Pro - Güncelleme" => "NSX Datenrettung Pro - Update",
                "Güncelleme hazırlanıyor..." => "Update wird vorbereitet...",
                "Güncelleme tamamlanamadı. Program tekrar açılıyor..." => "Das Update konnte nicht abgeschlossen werden. Das Programm wird neu gestartet...",
                "Ana program kapanıyor..." => "Hauptprogramm wird geschlossen...",
                "Güncelleme indiriliyor..." => "Update wird heruntergeladen...",
                "Güncelleme indiriliyor... %{0}" => "Update wird heruntergeladen... {0}%",
                "Güncelleme paketi doğrulanıyor..." => "Update-Paket wird geprüft...",
                "Dosyalar değiştiriliyor..." => "Dateien werden ersetzt...",
                "Dosyalar değiştiriliyor... %{0}" => "Dateien werden ersetzt... {0}%",
                "Program yeniden başlatılıyor..." => "Programm wird neu gestartet...",
                "Güncelleme tamamlandı. Program açılıyor..." => "Update abgeschlossen. Das Programm wird geöffnet...",
                "Güncelleme tamamlandı. Programı manuel açabilirsiniz." => "Update abgeschlossen. Sie können das Programm manuell öffnen.",
                "İndirme tekrar deneniyor... ({0}/3)" => "Download wird erneut versucht... ({0}/3)",
                _ => source
            },
            "es" => source switch
            {
                "NSX Veri Kurtarma Pro - Güncelleme" => "NSX Recuperación de Datos Pro - Actualización",
                "Güncelleme hazırlanıyor..." => "Preparando la actualización...",
                "Güncelleme tamamlanamadı. Program tekrar açılıyor..." => "No se pudo completar la actualización. Reiniciando el programa...",
                "Ana program kapanıyor..." => "Cerrando el programa principal...",
                "Güncelleme indiriliyor..." => "Descargando actualización...",
                "Güncelleme indiriliyor... %{0}" => "Descargando actualización... {0}%",
                "Güncelleme paketi doğrulanıyor..." => "Verificando el paquete de actualización...",
                "Dosyalar değiştiriliyor..." => "Reemplazando archivos...",
                "Dosyalar değiştiriliyor... %{0}" => "Reemplazando archivos... {0}%",
                "Program yeniden başlatılıyor..." => "Reiniciando el programa...",
                "Güncelleme tamamlandı. Program açılıyor..." => "Actualización completada. Abriendo el programa...",
                "Güncelleme tamamlandı. Programı manuel açabilirsiniz." => "Actualización completada. Puede abrir el programa manualmente.",
                "İndirme tekrar deneniyor... ({0}/3)" => "Reintentando la descarga... ({0}/3)",
                _ => source
            },
            "fr" => source switch
            {
                "NSX Veri Kurtarma Pro - Güncelleme" => "NSX Récupération de Données Pro - Mise à jour",
                "Güncelleme hazırlanıyor..." => "Préparation de la mise à jour...",
                "Güncelleme tamamlanamadı. Program tekrar açılıyor..." => "La mise à jour n’a pas pu être terminée. Redémarrage du programme...",
                "Ana program kapanıyor..." => "Fermeture du programme principal...",
                "Güncelleme indiriliyor..." => "Téléchargement de la mise à jour...",
                "Güncelleme indiriliyor... %{0}" => "Téléchargement de la mise à jour... {0}%",
                "Güncelleme paketi doğrulanıyor..." => "Vérification du paquet de mise à jour...",
                "Dosyalar değiştiriliyor..." => "Remplacement des fichiers...",
                "Dosyalar değiştiriliyor... %{0}" => "Remplacement des fichiers... {0}%",
                "Program yeniden başlatılıyor..." => "Redémarrage du programme...",
                "Güncelleme tamamlandı. Program açılıyor..." => "Mise à jour terminée. Ouverture du programme...",
                "Güncelleme tamamlandı. Programı manuel açabilirsiniz." => "Mise à jour terminée. Vous pouvez ouvrir le programme manuellement.",
                "İndirme tekrar deneniyor... ({0}/3)" => "Nouvelle tentative de téléchargement... ({0}/3)",
                _ => source
            },
            "it" => source switch
            {
                "NSX Veri Kurtarma Pro - Güncelleme" => "NSX Recupero Dati Pro - Aggiornamento",
                "Güncelleme hazırlanıyor..." => "Preparazione aggiornamento...",
                "Güncelleme tamamlanamadı. Program tekrar açılıyor..." => "Impossibile completare l’aggiornamento. Riavvio del programma...",
                "Ana program kapanıyor..." => "Chiusura del programma principale...",
                "Güncelleme indiriliyor..." => "Download aggiornamento...",
                "Güncelleme indiriliyor... %{0}" => "Download aggiornamento... {0}%",
                "Güncelleme paketi doğrulanıyor..." => "Verifica del pacchetto di aggiornamento...",
                "Dosyalar değiştiriliyor..." => "Sostituzione dei file...",
                "Dosyalar değiştiriliyor... %{0}" => "Sostituzione dei file... {0}%",
                "Program yeniden başlatılıyor..." => "Riavvio del programma...",
                "Güncelleme tamamlandı. Program açılıyor..." => "Aggiornamento completato. Apertura del programma...",
                "Güncelleme tamamlandı. Programı manuel açabilirsiniz." => "Aggiornamento completato. Puoi aprire il programma manualmente.",
                "İndirme tekrar deneniyor... ({0}/3)" => "Nuovo tentativo di download... ({0}/3)",
                _ => source
            },
            "ru" => source switch
            {
                "NSX Veri Kurtarma Pro - Güncelleme" => "NSX Восстановление данных Pro - Обновление",
                "Güncelleme hazırlanıyor..." => "Подготовка обновления...",
                "Güncelleme tamamlanamadı. Program tekrar açılıyor..." => "Не удалось завершить обновление. Перезапуск программы...",
                "Ana program kapanıyor..." => "Закрытие основной программы...",
                "Güncelleme indiriliyor..." => "Загрузка обновления...",
                "Güncelleme indiriliyor... %{0}" => "Загрузка обновления... {0}%",
                "Güncelleme paketi doğrulanıyor..." => "Проверка пакета обновления...",
                "Dosyalar değiştiriliyor..." => "Замена файлов...",
                "Dosyalar değiştiriliyor... %{0}" => "Замена файлов... {0}%",
                "Program yeniden başlatılıyor..." => "Перезапуск программы...",
                "Güncelleme tamamlandı. Program açılıyor..." => "Обновление завершено. Запуск программы...",
                "Güncelleme tamamlandı. Programı manuel açabilirsiniz." => "Обновление завершено. Программу можно открыть вручную.",
                "İndirme tekrar deneniyor... ({0}/3)" => "Повторная попытка загрузки... ({0}/3)",
                _ => source
            },
            _ => source
        };

        return args.Length == 0 ? translated : string.Format(translated, args);
    }

    private static void CreateWindow()
    {
        _form = new Form
        {
            Text = T("NSX Veri Kurtarma Pro - Güncelleme"),
            Width = 500,
            Height = 176,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
            BackColor = System.Drawing.Color.White
        };

        _label = new Label
        {
            Left = 24,
            Top = 24,
            Width = 440,
            Height = 42,
            Text = T("Güncelleme hazırlanıyor..."),
            Font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold),
            ForeColor = System.Drawing.Color.FromArgb(15, 23, 42)
        };

        _bar = new ProgressBar
        {
            Left = 24,
            Top = 82,
            Width = 440,
            Height = 22,
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };

        _form.Controls.Add(_label);
        _form.Controls.Add(_bar);
        _form.Show();
        Application.DoEvents();
    }

    private static void ShowStatus(string message, int percent, bool forceLog = false)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        try
        {
            if (_label is not null)
                _label.Text = message;
            if (_bar is not null && _bar.Value != percent)
                _bar.Value = percent;
            Application.DoEvents();
        }
        catch
        {
        }

        if (forceLog || percent != _lastShownPercent || !string.Equals(message, _lastShownMessage, StringComparison.Ordinal))
        {
            _lastShownPercent = percent;
            _lastShownMessage = message;
            Log(message);
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                continue;

            string key = args[index][2..];
            string value = index + 1 < args.Length ? args[index + 1] : string.Empty;
            map[key] = value;
            index++;
        }

        return map;
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string FindPayloadRoot(string extractPath)
    {
        string[] filesAtRoot = Directory.GetFiles(extractPath);
        string[] dirsAtRoot = Directory.GetDirectories(extractPath);

        if (filesAtRoot.Length == 0 && dirsAtRoot.Length == 1)
        {
            string onlyDir = dirsAtRoot[0];
            if (Directory.GetFiles(onlyDir, "*", SearchOption.AllDirectories).Any())
                return onlyDir;
        }

        return extractPath;
    }

    private static bool IsUpdaterFile(string fileName) =>
        fileName.Equals(UpdaterExeName, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkip(string relativePath)
    {
        string file = Path.GetFileName(relativePath).ToLowerInvariant();
        string ext = Path.GetExtension(relativePath).ToLowerInvariant();

        if (file is "license-state.dat" or "ui-settings.json" or "pro-filter-settings.json" or
            "update-log.txt" or "update-error.log" or "update-start.log")
        {
            return true;
        }

        if (ext is ".db" or ".sqlite" or ".sqlite3" or ".bak" or ".nsx")
            return true;

        return false;
    }

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

    private static string GetTempFolder()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NSX Yazılım",
            "NSX Veri Kurtarma Pro",
            "UpdateTemp");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
