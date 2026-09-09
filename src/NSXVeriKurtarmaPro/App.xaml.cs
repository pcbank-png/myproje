using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro;

public partial class App : Application
{
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const string InstanceLockFileName = "NSXVeriKurtarmaPro.instance.lock";
    private const int MinimumStartupScreenMilliseconds = 3000;

    private FileStream? _instanceLockStream;

    public static string? StartupProjectPath { get; private set; }

    private static string L(string source) => LocalizationService.Current.Translate(source);

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireInstanceLock())
        {
            ActivateRunningInstance();
            Shutdown(0);
            return;
        }

        // Önceki build'lerden açık kalmış ve henüz single-instance kilidi
        // oluşturmayan bir NSX Veri Kurtarma Pro varsa onu da ikinci instance
        // olarak kabul et. Böylece eski build + yeni build birlikte açılmaz.
        if (TryActivateLegacyRunningInstance())
        {
            ReleaseInstanceLock();
            Shutdown(0);
            return;
        }

        StartupProjectPath = e.Args
            .Select(argument => argument.Trim().Trim('"'))
            .FirstOrDefault(argument =>
                File.Exists(argument) &&
                string.Equals(Path.GetExtension(argument), RecoveryProjectService.ProjectExtension, StringComparison.OrdinalIgnoreCase));

        LocalizationService.Current.Initialize();
        LocalizationService.Current.LanguageChanged += LocalizationService_LanguageChanged;

        EventManager.RegisterClassHandler(
            typeof(Window),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(OnWindowPreviewKeyDown));

        // XAML icinde dogrudan yazilmis metinler dil paketiyle otomatik cevrilir.
        // Sonradan olusan DataTemplate/ContextMenu elemanlari da Loaded aninda yerellestirilir.
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnFrameworkElementLoaded));

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        var startupWindow = new StartupLoadingWindow();
        MainWindow = startupWindow;
        startupWindow.Show();
        startupWindow.UpdateProgress(0, "Başlangıç ekranı hazırlanıyor...", "Uygulama açılışı denetleniyor.");

        try
        {
            await RunStartupSequenceAsync(startupWindow);
        }
        catch (Exception ex)
        {
            AppLog.Error("Başlangıç akışı sırasında hata oluştu.", ex);

            try
            {
                if (startupWindow.IsVisible)
                    startupWindow.Close();
            }
            catch
            {
            }

            MessageBox.Show(
                string.Format(
                    L("Uygulama başlangıcı tamamlanamadı.\n\n{0}\n\nLog:\n{1}"),
                    L(ex.Message),
                    AppLog.CurrentLogPath),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LocalizationService.Current.LanguageChanged -= LocalizationService_LanguageChanged;
        ReleaseInstanceLock();
        base.OnExit(e);
    }

    private async Task RunStartupSequenceAsync(StartupLoadingWindow startupWindow)
    {
        var sequenceTimer = Stopwatch.StartNew();
        using var progressCts = new CancellationTokenSource();
        Task progressAnimation = AnimateStartupProgressAsync(startupWindow, sequenceTimer, progressCts.Token);
        NSXVeriKurtarmaPro.MainWindow? mainWindow = null;

        try
        {
            await RunStartupStepAsync(
                startupWindow,
                "Uygulama ortamı hazırlanıyor...",
                "Geçici klasörler, oturum yolları ve log altyapısı hazırlanıyor.",
                () => Task.Run(PrepareApplicationEnvironment));

            await RunStartupStepAsync(
                startupWindow,
                "Dil ve kaynak paketleri yükleniyor...",
                "Kullanılabilir diller ve arayüz kaynakları doğrulanıyor.",
                () => Dispatcher.InvokeAsync(() => LocalizationService.Current.ReloadLanguages(raiseLanguageChanged: false), DispatcherPriority.Background).Task);

            await RunStartupStepAsync(
                startupWindow,
                "Görsel önizleme motoru hazırlanıyor...",
                "Global görsel codec bileşenleri açılışa alınarak ilk önizleme gecikmesi azaltılıyor.",
                () => Task.Run(WarmImageCodec));

            await RunStartupStepAsync(
                startupWindow,
                "Disk ve bölüm servisleri denetleniyor...",
                "Bağlı depolama aygıtları taranıyor ve hızlı tarama kaynakları hazırlanıyor.",
                () => Task.Run(WarmStorageServices));

            await RunStartupStepAsync(
                startupWindow,
                StartupProjectPath is null ? "Oturum sistemi hazırlanıyor..." : "Kayıtlı oturum doğrulanıyor...",
                StartupProjectPath is null
                    ? "Yeni oturum için proje servisleri hazırlanıyor."
                    : $"{Path.GetFileName(StartupProjectPath)} proje dosyası doğrulanıyor.",
                () => Task.Run(ValidateStartupProjectIfAny));

            await RunStartupStepAsync(
                startupWindow,
                "Ana arayüz yükleniyor...",
                "MainWindow ve başlangıç bileşenleri oluşturuluyor.",
                async () =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AppLog.Info("[STARTUP] MainWindow oluşturuluyor.");
                        mainWindow = new NSXVeriKurtarmaPro.MainWindow();
                    }, DispatcherPriority.ApplicationIdle);
                });

            // Gerçek yükleme 7 saniyeden önce biterse yüzde animasyonu kalan süreyi
            // doğal biçimde tamamlar. Yükleme 7 saniyeyi aşarsa %99'da bekler;
            // gerçek işler bitmeden kullanıcıya %100 gösterilmez.
            while (sequenceTimer.ElapsedMilliseconds < MinimumStartupScreenMilliseconds)
                await Task.Delay(40);

            progressCts.Cancel();
            try
            {
                await progressAnimation;
            }
            catch (OperationCanceledException)
            {
            }

            startupWindow.UpdateProgress(100, "Hazır", "NSX Veri Kurtarma Pro açılıyor...");
            await Task.Delay(140);

            mainWindow ??= new NSXVeriKurtarmaPro.MainWindow();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            startupWindow.Close();

            // Cari Takip Pro referansindaki davranis gibi lisans ve guncelleme
            // kontrolleri ana pencere acildiktan sonra sessizce calisir; acilisi geciktirmez.
            _ = RunOnlineMaintenanceAsync(mainWindow);
        }
        finally
        {
            if (!progressCts.IsCancellationRequested)
                progressCts.Cancel();
        }
    }

    private static async Task RunOnlineMaintenanceAsync(Window owner)
    {
        try
        {
            await Task.Delay(1200);
        }
        catch
        {
            return;
        }

        Task licenseRefreshTask = Task.CompletedTask;
        if (LicenseService.HasStoredLicenseCredentials())
        {
            licenseRefreshTask = Task.Run(async () =>
            {
                try
                {
                    LicenseCheckResult result = await LicenseService.RefreshStoredLicenseOnlineAsync();
                    if (result.Success)
                        AppLog.Info("[LICENSE] Kayitli lisans baslangic sonrasi online dogrulandi.");
                    else if (result.ApiUnavailable)
                        AppLog.Info("[LICENSE] Online lisans servisine ulasilamadi; yerel durum korundu.");
                    else
                        AppLog.Info("[LICENSE] Online lisans kontrolu tamamlandi: " + result.Status);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Baslangic sonrasi lisans kontrolu tamamlanamadi; yerel lisans durumu korundu.", ex);
                }
            });
        }

        try
        {
            await UpdateService.CheckAndPromptAsync(owner, showNoUpdateMessage: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Baslangic sonrasi guncelleme kontrolu tamamlanamadi.", ex);
        }

        try
        {
            await licenseRefreshTask;
        }
        catch
        {
            // Yukaridaki gorev kendi hatalarini loglar; uygulama akisi etkilenmez.
        }
    }

    private static async Task AnimateStartupProgressAsync(
        StartupLoadingWindow startupWindow,
        Stopwatch sequenceTimer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            double elapsedRatio = sequenceTimer.Elapsed.TotalMilliseconds / MinimumStartupScreenMilliseconds;
            double timedPercent = Math.Clamp(elapsedRatio * 100d, 0d, 99d);

            // Yüzde tamamen gerçek geçen zamana bağlıdır: 7 saniyeye kadar düzenli akar.
            // Başlık/detay ise o anda gerçekten çalışan bileşeni göstermeye devam eder.
            if (timedPercent > startupWindow.ProgressPercent)
            {
                startupWindow.UpdateProgress(
                    timedPercent,
                    startupWindow.StepTitle,
                    startupWindow.StepDetail);
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task RunStartupStepAsync(
        StartupLoadingWindow startupWindow,
        string title,
        string detail,
        Func<Task> action)
    {
        startupWindow.UpdateProgress(startupWindow.ProgressPercent, title, detail);
        AppLog.Info($"[STARTUP] {title}");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await action();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void PrepareApplicationEnvironment()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NSX Yazılım",
            "NSX Veri Kurtarma Pro");

        Directory.CreateDirectory(appDataDirectory);
        Directory.CreateDirectory(Path.Combine(appDataDirectory, "Logs"));
        Directory.CreateDirectory(Path.Combine(appDataDirectory, "Sessions"));
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "NSXVeriKurtarmaPro", "ThumbnailDecode", Environment.ProcessId.ToString()));
    }

    private static void WarmImageCodec()
    {
        string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
        if (!File.Exists(logoPath))
            return;

        _ = GlobalImageCodec.GetDimensions(logoPath);
        _ = GlobalImageCodec.LoadPreview(logoPath, 256);
    }

    private static void WarmStorageServices()
    {
        var storageService = new StorageDeviceService();
        _ = storageService.GetDevices();
    }

    private static void ValidateStartupProjectIfAny()
    {
        if (string.IsNullOrWhiteSpace(StartupProjectPath) || !File.Exists(StartupProjectPath))
            return;

        var projectService = new RecoveryProjectService();
        _ = projectService.Load(StartupProjectPath);
    }

    private static void OnFrameworkElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            LocalizationService.Current.ApplyElement(element);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            foreach (Window window in Windows.OfType<Window>())
                LocalizationService.Current.ApplyWindow(window);
        }, DispatcherPriority.DataBind);
    }

    private bool TryAcquireInstanceLock()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NSX Yazılım",
                "NSX Veri Kurtarma Pro");

            Directory.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, InstanceLockFileName);

            _instanceLockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReleaseInstanceLock()
    {
        try
        {
            _instanceLockStream?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _instanceLockStream = null;
        }
    }

    private static bool TryActivateLegacyRunningInstance()
    {
        using Process current = Process.GetCurrentProcess();
        Process[] candidates = Process.GetProcessesByName(current.ProcessName);

        try
        {
            foreach (Process process in candidates)
            {
                if (process.Id == current.Id)
                    continue;

                try
                {
                    if (process.SessionId != current.SessionId)
                        continue;

                    process.Refresh();
                    IntPtr windowHandle = process.MainWindowHandle;
                    if (windowHandle == IntPtr.Zero)
                        continue;

                    ActivateWindow(windowHandle);
                    return true;
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        finally
        {
            foreach (Process process in candidates)
                process.Dispose();
        }

        return false;
    }

    private static void ActivateRunningInstance()
    {
        using Process current = Process.GetCurrentProcess();
        Process[] candidates = Process.GetProcessesByName(current.ProcessName);

        try
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                foreach (Process process in candidates)
                {
                    if (process.Id == current.Id)
                        continue;

                    try
                    {
                        if (process.SessionId != current.SessionId)
                            continue;

                        process.Refresh();
                        IntPtr windowHandle = process.MainWindowHandle;
                        if (windowHandle == IntPtr.Zero)
                            continue;

                        ActivateWindow(windowHandle);
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                }

                Thread.Sleep(50);
            }
        }
        finally
        {
            foreach (Process process in candidates)
                process.Dispose();
        }
    }

    private static void ActivateWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            return;

        ShowWindow(windowHandle, IsIconic(windowHandle) ? SwRestore : SwShow);
        BringWindowToTop(windowHandle);
        SetForegroundWindow(windowHandle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Window window || window is MainWindow)
            return;

        e.Handled = true;
        window.Close();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLog.Error("İşlenmeyen AppDomain hatası.", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Gözlemlenmeyen görev hatası.", e.Exception);
        e.SetObserved();
    }

    private static int _dispatcherExceptionDialogActive;

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Önce işaretle: hata penceresi açılırken yeni bir UI hatası oluşursa
        // DispatcherUnhandledException kendini tekrar çağırıp yığın taşmasına dönüşmesin.
        e.Handled = true;

        try
        {
            AppLog.Error("UI işlenmeyen hata.", e.Exception);
        }
        catch
        {
            // Hata raporlama, asıl hatanın üzerine ikinci bir hata bindirmemeli.
        }

        // StackOverflow sırasında yeni WPF pencere/MessageBox üretmek güvenli değildir.
        if (e.Exception is StackOverflowException)
            return;

        if (Interlocked.Exchange(ref _dispatcherExceptionDialogActive, 1) != 0)
            return;

        try
        {
            MessageBox.Show(
                string.Format(
                    L("Beklenmeyen bir hata oluştu.\n\n{0}\n\nLog:\n{1}"),
                    L(e.Exception.Message),
                    AppLog.CurrentLogPath),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Global hata gösterimi de uygulamayı ikinci kez düşürmemeli.
        }
        finally
        {
            Volatile.Write(ref _dispatcherExceptionDialogActive, 0);
        }
    }
}
