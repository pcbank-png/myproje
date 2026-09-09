using System.Windows;
using System.Windows.Input;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro;

public partial class ScanCompletedWindow : Window
{
    private readonly int _foundCount;
    private readonly string _foundSize;
    private readonly string _elapsed;

    public bool RecoveryRequested { get; private set; }

    public ScanCompletedWindow(Window owner, int foundCount, string foundSize, string elapsed)
    {
        InitializeComponent();
        Owner = owner;
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
        MatchOwnerBounds();
        RecoveryButton.Focus();
    }

    private void MatchOwnerBounds()
    {
        if (Owner is null)
            return;

        Left = Owner.Left;
        Top = Owner.Top;
        Width = Math.Max(1, Owner.ActualWidth);
        Height = Math.Max(1, Owner.ActualHeight);
    }

    private void RefreshLocalizedContent()
    {
        Title = L("Tarama Tamamlandı");
        TitleText.Text = L("Tebrikler! Tarama Tamamlandı");
        SubtitleText.Text = L("Tarama sonuçları hazır. Bulunan dosyalar siz ana sayfaya dönmeyi onaylayana kadar ekranda kalır.");
        FoundCountText.Text = _foundCount.ToString("N0");
        FoundLabelText.Text = L("Dosyalar bulundu");
        FoundSizeText.Text = _foundSize;
        SizeLabelText.Text = L("Toplam boyut");
        ElapsedText.Text = _elapsed;
        ElapsedLabelText.Text = L("Tarama süresi");
        QualityText.Text = L(_foundCount > 0
            ? "Harika haber: Bulunan dosyalar kurtarma için hazır."
            : "Tarama tamamlandı ancak kurtarılabilir dosya bulunamadı.");
        RetentionText.Text = L("Bu pencere kapatıldığında sonuçlar silinmez; seçimlerinize kaldığınız yerden devam edebilirsiniz.");
        StayButton.Content = L("Sonuçlarda Kal");
        RecoveryButton.Content = L("Kurtarmaya Git");
        RecoveryButton.IsEnabled = _foundCount > 0;
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(RefreshLocalizedContent);
    }

    private void RecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        RecoveryRequested = true;
        DialogResult = true;
    }

    private void StayButton_Click(object sender, RoutedEventArgs e)
    {
        RecoveryRequested = false;
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        RecoveryRequested = false;
        DialogResult = false;
    }

    private void Window_Closed(object? sender, EventArgs e) =>
        LocalizationService.Current.LanguageChanged -= LocalizationService_LanguageChanged;
}
