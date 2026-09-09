using System.Windows;
using System.Windows.Input;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro;

public enum ScanExitPromptMode
{
    StopScan,
    StopScanAndReturnHome,
    ResetResultsAndReturnHome
}

public partial class ScanExitPromptWindow : Window
{
    private readonly ScanExitPromptMode _mode;
    private readonly int _foundCount;
    private readonly string _foundSize;
    private readonly string _elapsed;

    public bool Confirmed { get; private set; }

    public ScanExitPromptWindow(
        Window owner,
        ScanExitPromptMode mode,
        int foundCount,
        string foundSize,
        string elapsed)
    {
        InitializeComponent();
        Owner = owner;
        _mode = mode;
        _foundCount = Math.Max(0, foundCount);
        _foundSize = string.IsNullOrWhiteSpace(foundSize) ? "0 B" : foundSize;
        _elapsed = string.IsNullOrWhiteSpace(elapsed) ? "—" : elapsed;

        Loaded += Window_Loaded;
        Closed += Window_Closed;
        LocalizationService.Current.LanguageChanged += LocalizationService_LanguageChanged;
        RefreshLocalizedContent();
    }

    private static string L(string source) => LocalizationService.Current.Translate(source);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner is not null)
        {
            Left = Owner.Left;
            Top = Owner.Top;
            Width = Math.Max(1, Owner.ActualWidth);
            Height = Math.Max(1, Owner.ActualHeight);
        }

        CancelButton.Focus();
    }

    private void RefreshLocalizedContent()
    {
        Title = L("Tarama Uyarısı");
        FoundCountText.Text = _foundCount.ToString("N0");
        FoundLabelText.Text = L("Dosyalar bulundu");

        if (_mode == ScanExitPromptMode.ResetResultsAndReturnHome)
        {
            TitleText.Text = L("Ana sayfaya dönmeden önce mevcut sonuçlar sıfırlansın mı?");
            SecondaryValueText.Text = _foundSize;
            SecondaryLabelText.Text = L("Toplam boyut");
            DetailText.Text = L("Onay verirseniz tarama sonuçları ve seçimler temizlenir. Sonuçlarda kalırsanız hiçbir veri sıfırlanmaz.");
            ConfirmButton.Content = L("Sonuçları Sıfırla");
            CancelButton.Content = L("Sonuçlarda Kal");
            return;
        }

        TitleText.Text = L(_mode == ScanExitPromptMode.StopScanAndReturnHome
            ? "Taramayı durdurup ana sayfaya dönmek istiyor musunuz?"
            : "Kayıp verileri aramayı durdurmak mı istiyorsunuz?");
        SecondaryValueText.Text = _elapsed;
        SecondaryLabelText.Text = L("Kullanılan zaman");
        DetailText.Text = L(_mode == ScanExitPromptMode.StopScanAndReturnHome
            ? "Tarama güvenli biçimde durdurulur. Ardından bulunan dosyalar ve mevcut tarama oturumu açık onayınızla sıfırlanır."
            : "Tarama güvenli biçimde durdurulur; şimdiye kadar bulunan dosyalar sonuç ekranında korunur ve kurtarılabilir.");
        ConfirmButton.Content = L(_mode == ScanExitPromptMode.StopScanAndReturnHome ? "Durdur ve Ana Sayfa" : "Taramayı Durdur");
        CancelButton.Content = L("Taramaya Devam Et");
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(RefreshLocalizedContent);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        Confirmed = false;
        DialogResult = false;
    }

    private void Window_Closed(object? sender, EventArgs e) =>
        LocalizationService.Current.LanguageChanged -= LocalizationService_LanguageChanged;
}
