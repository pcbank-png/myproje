using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NSXVeriKurtarmaPro.Infrastructure;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro;

public enum SystemCenterSection
{
    License,
    Update,
    About,
    Settings
}

public partial class SystemCenterWindow : Window
{
    private readonly string _versionText;
    private SystemCenterSection _currentSection;
    private string _licenseActionSource = "Çevrimiçi doğrulama için e-posta adresi ve lisans anahtarı bekleniyor.";
    private bool _loadingSettings;
    private bool _licenseBusy;
    private bool _updateBusy;
    private UpdateUiState _updateUiState = UpdateUiState.Ready;
    private UpdateService.UpdateStatus? _lastUpdateStatus;
    private string _updateErrorSource = string.Empty;

    public SystemCenterWindow(SystemCenterSection initialSection)
    {
        InitializeComponent();

        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        _versionText = version is null ? "1.3.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        _currentSection = initialSection;

        Loaded += SystemCenterWindow_Loaded;
        Closed += SystemCenterWindow_Closed;
        LocalizationService.Current.LanguageChanged += LocalizationService_LanguageChanged;
    }

    private string L(string source) => LocalizationService.Current.Translate(source);

    private void SystemCenterWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.Current.ApplyWindow(this);
        LoadStoredLicenseCredentials();
        RefreshDynamicContent();
        ShowSection(_currentSection);
    }

    private void SystemCenterWindow_Closed(object? sender, EventArgs e)
    {
        LocalizationService.Current.LanguageChanged -= LocalizationService_LanguageChanged;
        Loaded -= SystemCenterWindow_Loaded;
        Closed -= SystemCenterWindow_Closed;
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            LocalizationService.Current.ApplyWindow(this);
            RefreshDynamicContent();
            ShowSection(_currentSection);
        }, DispatcherPriority.DataBind);
    }

    private void RefreshDynamicContent()
    {
        LicenseVersionText.Text = _versionText;
        UpdateVersionText.Text = _versionText;
        AboutVersionText.Text = _versionText;
        AboutVersionLineText.Text = string.Format(L("Sürüm {0}"), _versionText);
        LicenseProductNameText.Text = L(LicenseService.ProductName);
        LicenseActionStatusText.Text = L(_licenseActionSource);
        RefreshLicenseStatusVisual();
        RefreshUpdateStatusVisual();
        LoadSettingsControls();
        RefreshBrandLogo();
    }

    private void LoadStoredLicenseCredentials()
    {
        (string email, string licenseKey) = LicenseService.GetStoredCredentials();
        if (!string.IsNullOrWhiteSpace(email))
            LicenseEmailBox.Text = email;
        if (!string.IsNullOrWhiteSpace(licenseKey))
            LicenseKeyBox.Password = licenseKey;
    }

    private void RefreshLicenseStatusVisual()
    {
        LicenseStatus status = LicenseService.GetStatus();

        if (status.IsLicensed)
        {
            if (status.ExpireDate.HasValue)
            {
                int remainingDays = Math.Max(0, (status.ExpireDate.Value.Date - DateTime.Now.Date).Days);
                string template = status.IsOffline
                    ? "Lisans doğrulandı • çevrimdışı önbellek aktif • {0} gün kaldı."
                    : "Lisans doğrulandı • {0} gün kaldı.";
                LicenseStatusText.Text = string.Format(L(template), remainingDays);
            }
            else
            {
                LicenseStatusText.Text = L(status.IsOffline
                    ? "Ömür boyu lisans doğrulandı • çevrimdışı önbellek aktif."
                    : "Ömür boyu lisans doğrulandı.");
            }

            string badge = status.IsOffline ? "Lisans aktif • Çevrimdışı" : "Lisans aktif";
            SetLicenseBadge(badge, "#17B26A", "#067647");
            return;
        }

        if (status.IsPendingOnlineVerification)
        {
            LicenseStatusText.Text = L("Online doğrulama bekleniyor. Lisans doğrulanana kadar tarama ve önizleme kullanılabilir; dosya kurtarma kilitlidir.");
            SetLicenseBadge("Online doğrulama bekleniyor", "#F79009", "#9A6700");
            return;
        }

        LicenseStatusText.Text = L("Lisans etkin değil. Tarama ve önizleme kullanılabilir; dosya kurtarma için lisans gerekir.");
        SetLicenseBadge("Lisans Gerekli", "#D92D20", "#B42318");
    }

    private void SetLicenseBadge(string source, string dotColor, string textColor)
    {
        LicenseBadgeText.Text = L(source);
        LicenseBadgeDot.Fill = BrushFromHex(dotColor);
        LicenseBadgeText.Foreground = BrushFromHex(textColor);
    }

    private void RefreshUpdateStatusVisual()
    {
        string statusText;
        string packageText;
        string badgeText;
        string dotColor;
        string textColor;

        switch (_updateUiState)
        {
            case UpdateUiState.Checking:
                statusText = L("Güncelleme sunucusu denetleniyor...");
                packageText = L("Denetleniyor");
                badgeText = L("Kontrol ediliyor");
                dotColor = "#0B57C9";
                textColor = "#0B57C9";
                break;

            case UpdateUiState.Error:
                statusText = L(string.IsNullOrWhiteSpace(_updateErrorSource)
                    ? "Güncelleme sunucusuna bağlanılamadı."
                    : _updateErrorSource);
                packageText = L("Kontrol edilemedi");
                badgeText = L("Bağlantı hatası");
                dotColor = "#D92D20";
                textColor = "#B42318";
                break;

            case UpdateUiState.Current:
                statusText = L("NSX Veri Kurtarma Pro güncel. Yeni bir sürüm bulunamadı.");
                packageText = L("Güncel");
                badgeText = L("Güncel sürüm");
                dotColor = "#17B26A";
                textColor = "#067647";
                break;

            case UpdateUiState.Available when _lastUpdateStatus is not null:
                statusText = string.Format(L("Yeni sürüm hazır: {0}"), _lastUpdateStatus.Version);
                packageText = _lastUpdateStatus.Required
                    ? string.Format(L("v{0} • Zorunlu"), _lastUpdateStatus.Version)
                    : string.Format(L("v{0} • İsteğe bağlı"), _lastUpdateStatus.Version);
                badgeText = _lastUpdateStatus.Required ? L("Zorunlu güncelleme") : L("Güncelleme hazır");
                dotColor = _lastUpdateStatus.Required ? "#F04438" : "#F79009";
                textColor = _lastUpdateStatus.Required ? "#B42318" : "#9A6700";
                break;

            default:
                statusText = L("Güncelleme sunucusu bağlantıya hazır.");
                packageText = L("Kontrol edilmedi");
                badgeText = L("Sürüm bilgisi");
                dotColor = "#0B57C9";
                textColor = "#0B57C9";
                break;
        }

        UpdateStatusText.Text = statusText;
        UpdatePackageStatusText.Text = packageText;
        UpdateBadgeText.Text = badgeText;
        UpdateBadgeDot.Fill = BrushFromHex(dotColor);
        UpdateBadgeText.Foreground = BrushFromHex(textColor);

        UpdateLastCheckText.Text = _lastUpdateStatus is null
            ? L("Henüz denetlenmedi")
            : FormatLocalDateTime(_lastUpdateStatus.CheckedAt);
    }

    private static Brush BrushFromHex(string value) =>
        (Brush)new BrushConverter().ConvertFromString(value)!;

    private string FormatLocalDateTime(DateTime value)
    {
        string languageCode = LocalizationService.Current.CurrentLanguage?.Code ?? "tr-TR";
        try
        {
            return value.ToString("g", CultureInfo.GetCultureInfo(languageCode));
        }
        catch
        {
            return value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private void RefreshBrandLogo()
    {
        string code = LocalizationService.Current.CurrentLanguage?.Code ?? "tr-TR";
        bool isTurkish = code.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        string assetName = isTurkish ? "logo_ui.png" : "logo_ui_en.png";

        try
        {
            AboutLogoImage.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{assetName}", UriKind.Absolute));
        }
        catch
        {
            AboutLogoImage.Source = null;
        }
    }

    private void ShowSection(SystemCenterSection section)
    {
        _currentSection = section;

        LicensePage.Visibility = section == SystemCenterSection.License ? Visibility.Visible : Visibility.Collapsed;
        UpdatePage.Visibility = section == SystemCenterSection.Update ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = section == SystemCenterSection.About ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = section == SystemCenterSection.Settings ? Visibility.Visible : Visibility.Collapsed;

        NavigationAssist.SetIsActive(LicenseNavButton, section == SystemCenterSection.License);
        NavigationAssist.SetIsActive(UpdateNavButton, section == SystemCenterSection.Update);
        NavigationAssist.SetIsActive(AboutNavButton, section == SystemCenterSection.About);
        NavigationAssist.SetIsActive(SettingsNavButton, section == SystemCenterSection.Settings);

        FooterPageHintText.Text = section switch
        {
            SystemCenterSection.License => L("Lisans yönetimi"),
            SystemCenterSection.Update => L("Güncelleme yönetimi"),
            SystemCenterSection.Settings => L("Program ayarları"),
            _ => L("Ürün bilgileri")
        };

        Dispatcher.BeginInvoke(() =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            // Gizli sekmeler görünür olduktan sonra statik XAML metinlerini yerelleştir.
            // ApplyWindow dinamik lisans/güncelleme durum metinlerini XAML varsayılanına
            // döndürebildiği için hemen ardından gerçek çalışma durumunu yeniden uygula.
            LocalizationService.Current.ApplyWindow(this);
            RefreshDynamicContent();
        }, DispatcherPriority.Loaded);
    }

    private void LicenseNavButton_Click(object sender, RoutedEventArgs e) => ShowSection(SystemCenterSection.License);
    private void UpdateNavButton_Click(object sender, RoutedEventArgs e) => ShowSection(SystemCenterSection.Update);
    private void AboutNavButton_Click(object sender, RoutedEventArgs e) => ShowSection(SystemCenterSection.About);
    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => ShowSection(SystemCenterSection.Settings);

    private void LoadSettingsControls()
    {
        _loadingSettings = true;
        try
        {
            ProUiSettingsState settings = ProUiSettings.Current;
            StartInWorkAreaToggle.IsChecked = settings.StartInWorkArea;
            RememberMainWindowToggle.IsChecked = settings.RememberMainWindowLayout;
            RememberPreviewWindowToggle.IsChecked = settings.RememberPreviewWindowLayout;
            AutoRefreshDevicesToggle.IsChecked = settings.AutoRefreshDevices;
            AutoGenerateThumbnailsToggle.IsChecked = settings.AutoGenerateThumbnails;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;

        ProUiSettings.Update(settings =>
        {
            settings.StartInWorkArea = StartInWorkAreaToggle.IsChecked == true;
            settings.RememberMainWindowLayout = RememberMainWindowToggle.IsChecked == true;
            settings.RememberPreviewWindowLayout = RememberPreviewWindowToggle.IsChecked == true;
            settings.AutoRefreshDevices = AutoRefreshDevicesToggle.IsChecked == true;
            settings.AutoGenerateThumbnails = AutoGenerateThumbnailsToggle.IsChecked == true;
        });
    }

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            this,
            L("Tüm program ayarları varsayılan değerlere döndürülsün mü? Tarama ve kurtarma verileri etkilenmez."),
            L("Varsayılan Ayarlara Dön"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        ProUiSettings.Reset();
        LoadSettingsControls();
    }

    private async void VerifyLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseBusy)
            return;

        string email = LicenseEmailBox.Text.Trim();
        string licenseKey = LicenseKeyBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(licenseKey))
        {
            SetLicenseAction("Lütfen e-posta adresinizi ve lisans anahtarınızı girin.");
            return;
        }

        if (!IsValidLicenseEmail(email))
        {
            SetLicenseAction("Lütfen geçerli bir e-posta adresi girin.");
            return;
        }

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            SetLicenseAction("Lütfen lisans anahtarınızı girin.");
            return;
        }

        SetLicenseBusy(true);
        SetLicenseAction("Lisans sunucusu ile güvenli bağlantı kuruluyor...");

        try
        {
            LicenseCheckResult result = await LicenseService.ActivateOnlineAsync(email, licenseKey);
            SetLicenseAction(result.Message);

            if (result.Success)
            {
                (string storedEmail, string storedKey) = LicenseService.GetStoredCredentials();
                LicenseEmailBox.Text = storedEmail;
                LicenseKeyBox.Password = storedKey;
            }

            RefreshLicenseStatusVisual();
        }
        finally
        {
            SetLicenseBusy(false);
        }
    }

    private async void RefreshLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseBusy)
            return;

        if (!LicenseService.HasStoredLicenseCredentials())
        {
            SetLicenseAction("Yenilenecek kayıtlı lisans bilgisi bulunamadı. Önce lisansınızı doğrulayın.");
            return;
        }

        SetLicenseBusy(true);
        SetLicenseAction("Lisans bilgileri sunucudan yenileniyor...");

        try
        {
            LicenseCheckResult result = await LicenseService.RefreshStoredLicenseOnlineAsync();
            LicenseStatus status = LicenseService.GetStatus();

            if (result.ApiUnavailable && status.IsLicensed)
                SetLicenseAction("Sunucuya ulaşılamadı; doğrulanmış yerel lisans çevrimdışı kullanılmaya devam ediyor.");
            else if (result.ApiUnavailable)
                SetLicenseAction("Lisans sunucusuna şu anda ulaşılamıyor. İnternet bağlantınızı kontrol edip yeniden deneyin.");
            else
                SetLicenseAction(result.Message);

            RefreshLicenseStatusVisual();
        }
        finally
        {
            SetLicenseBusy(false);
        }
    }

    private void CancelLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseBusy)
            return;

        MessageBoxResult answer = MessageBox.Show(
            this,
            L("Bu cihazdaki kayıtlı lisans bilgileri kaldırılsın mı?"),
            L("Lisansı İptal Et"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        LicenseService.ClearStoredLicense();
        LicenseEmailBox.Clear();
        LicenseKeyBox.Clear();
        SetLicenseAction("Lisans bilgileri bu cihazdan kaldırıldı. Dosya kurtarma yeniden lisans aktivasyonu gerektirir.");
        RefreshLicenseStatusVisual();
        LicenseEmailBox.Focus();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
            return;

        SetUpdateBusy(true);
        _updateUiState = UpdateUiState.Checking;
        RefreshUpdateStatusVisual();

        try
        {
            UpdateService.UpdateStatus status = await UpdateService.GetUpdateStatusAsync();
            _lastUpdateStatus = status;

            if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
            {
                _updateErrorSource = status.ErrorMessage;
                _updateUiState = UpdateUiState.Error;
                RefreshUpdateStatusVisual();
                return;
            }

            if (!status.HasUpdate)
            {
                _updateUiState = UpdateUiState.Current;
                RefreshUpdateStatusVisual();
                return;
            }

            _updateUiState = UpdateUiState.Available;
            RefreshUpdateStatusVisual();

            string detail = string.Format(
                L("Yeni sürüm hazır: {0}\nMevcut sürüm: {1}"),
                status.Version,
                status.CurrentVersion);

            if (!string.IsNullOrWhiteSpace(status.Notes))
                detail += Environment.NewLine + Environment.NewLine + status.Notes;

            detail += Environment.NewLine + Environment.NewLine + L("Güncellemeyi şimdi indirip kurmak ister misiniz?");

            MessageBoxResult answer = MessageBox.Show(
                this,
                detail,
                L(status.Required ? "Zorunlu Güncelleme" : "Güncelleme Hazır"),
                MessageBoxButton.YesNo,
                status.Required ? MessageBoxImage.Warning : MessageBoxImage.Information,
                status.Required ? MessageBoxResult.Yes : MessageBoxResult.No);

            if (answer == MessageBoxResult.Yes)
            {
                if (UpdateService.StartUpdate(status))
                    Application.Current.Shutdown();
                return;
            }

            if (status.Required)
                Application.Current.Shutdown();
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private void SetLicenseAction(string source)
    {
        _licenseActionSource = source;
        LicenseActionStatusText.Text = L(source);
    }

    private void SetLicenseBusy(bool busy)
    {
        _licenseBusy = busy;
        VerifyLicenseButton.IsEnabled = !busy;
        RefreshLicenseButton.IsEnabled = !busy;
        CancelLicenseButton.IsEnabled = !busy;
        LicenseEmailBox.IsEnabled = !busy;
        LicenseKeyBox.IsEnabled = !busy;
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        CheckUpdatesButton.IsEnabled = !busy;
    }

    private static bool IsValidLicenseEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Any(char.IsWhiteSpace))
            return false;

        int atIndex = email.IndexOf('@');
        if (atIndex <= 0 || atIndex != email.LastIndexOf('@') || atIndex >= email.Length - 3)
            return false;

        int dotIndex = email.IndexOf('.', atIndex + 2);
        return dotIndex > atIndex + 1 && dotIndex < email.Length - 1;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private enum UpdateUiState
    {
        Ready,
        Checking,
        Error,
        Current,
        Available
    }
}
