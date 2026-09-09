using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using NSXVeriKurtarmaPro.Models;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro;

public partial class RecoveryProgressWindow : Window
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastBytes;
    private TimeSpan _lastElapsed;
    private double _smoothedBytesPerSecond;
    private bool _isRunning = true;
    private bool _cancelRequested;
    private bool _isRecoveryPaused;
    private string _lastRunningTitleSource = "Dosyalar kurtarılıyor";
    private string _lastRunningDetailSource = "Seçili dosyalar işleniyor...";
    private readonly string _destination;
    private readonly int _fileCount;
    private Action? _refreshDynamicText;

    public event EventHandler? CancelRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ResumeRequested;

    private static string L(string source) => LocalizationService.Current.Translate(source);

    public RecoveryProgressWindow(int fileCount, long totalBytes, string destination, bool scanWillResume, bool scanWasAlreadyPaused)
    {
        InitializeComponent();
        _destination = destination;
        _fileCount = fileCount;
        LocalizationService.Current.LanguageChanged += LocalizationService_LanguageChanged;

        FileCountText.Text = L($"{fileCount:N0} dosya");
        DestinationText.Text = L($"Hedef: {destination}");
        TransferredText.Text = totalBytes > 0
            ? $"0 B / {RecoveryFileItem.FormatBytes(totalBytes)}"
            : "0 B";

        PauseResumeButtonText.Text = L("Kurtarmayı Duraklat");

        ResumeInfoText.Text = L(scanWillResume
            ? "Tarama kurtarma işlemi için güvenli biçimde duraklatıldı. Kurtarma tamamlandığında kaldığı noktadan otomatik devam edecek."
            : scanWasAlreadyPaused
                ? "Tarama kurtarma öncesinde zaten duraklatılmıştı; kurtarma sonrasında da duraklatılmış olarak kalacak."
                : "Kaynak disk yalnızca okunur; kurtarılan veriler yalnızca seçilen hedef klasöre yazılır.");

        _refreshDynamicText = () =>
        {
            RefreshCommonText();
            ResumeInfoText.Text = L(scanWillResume
                ? "Tarama kurtarma işlemi için güvenli biçimde duraklatıldı. Kurtarma tamamlandığında kaldığı noktadan otomatik devam edecek."
                : scanWasAlreadyPaused
                    ? "Tarama kurtarma öncesinde zaten duraklatılmıştı; kurtarma sonrasında da duraklatılmış olarak kalacak."
                    : "Kaynak disk yalnızca okunur; kurtarılan veriler yalnızca seçilen hedef klasöre yazılır.");
        };
        _refreshDynamicText();
    }

    public Task WaitForCloseAsync() => _closed.Task;

    public void UpdateProgress(OperationProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProgress(progress));
            return;
        }

        long processed = Math.Max(0, progress.ProcessedBytes);
        long total = Math.Max(0, progress.TotalBytes);
        double percent = Math.Clamp(progress.Percent, 0d, 100d);

        RecoveryProgressBar.Value = percent;
        PercentText.Text = $"{percent:0.0}%";
        string progressTitleSource = string.IsNullOrWhiteSpace(progress.Title) ? "Dosyalar kurtarılıyor" : progress.Title;
        string progressDetailSource = string.IsNullOrWhiteSpace(progress.Detail) ? "Seçili dosyalar işleniyor..." : progress.Detail;
        _lastRunningTitleSource = progressTitleSource;
        _lastRunningDetailSource = progressDetailSource;
        ProgressTitleText.Text = L(progressTitleSource);
        DetailText.Text = L(progressDetailSource);
        TransferredText.Text = total > 0
            ? $"{RecoveryFileItem.FormatBytes(processed)} / {RecoveryFileItem.FormatBytes(total)}"
            : RecoveryFileItem.FormatBytes(processed);

        if (_isRecoveryPaused)
        {
            _lastBytes = processed;
            _lastElapsed = _stopwatch.Elapsed;
            ProgressTitleText.Text = L("Kurtarma duraklatıldı");
            DetailText.Text = L("Aktif dosya güvenli blok sınırında bekliyor. Devam edildiğinde aynı noktadan sürecek.");
            SpeedText.Text = L("Duraklatıldı");
            EtaText.Text = L("Duraklatıldı");
            _refreshDynamicText = () =>
            {
                RefreshCommonText();
                ProgressTitleText.Text = L("Kurtarma duraklatıldı");
                DetailText.Text = L("Aktif dosya güvenli blok sınırında bekliyor. Devam edildiğinde aynı noktadan sürecek.");
                SpeedText.Text = L("Duraklatıldı");
                EtaText.Text = L("Duraklatıldı");
            };
            return;
        }

        TimeSpan elapsed = _stopwatch.Elapsed;
        double intervalSeconds = (elapsed - _lastElapsed).TotalSeconds;
        long intervalBytes = processed - _lastBytes;
        if (intervalSeconds >= 0.20 && intervalBytes >= 0)
        {
            double instantaneous = intervalBytes / intervalSeconds;
            if (instantaneous > 0)
                _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0
                    ? instantaneous
                    : (_smoothedBytesPerSecond * 0.72) + (instantaneous * 0.28);

            _lastElapsed = elapsed;
            _lastBytes = processed;
        }
        else if (_smoothedBytesPerSecond <= 0 && elapsed.TotalSeconds >= 0.75 && processed > 0)
        {
            _smoothedBytesPerSecond = processed / elapsed.TotalSeconds;
        }

        string speedSource = _smoothedBytesPerSecond > 0
            ? $"{RecoveryFileItem.FormatBytes((long)_smoothedBytesPerSecond)}/sn"
            : "Hesaplanıyor";
        SpeedText.Text = L(speedSource);

        string etaSource;
        if (percent >= 100)
        {
            etaSource = "Tamamlandı";
        }
        else if (total > processed && _smoothedBytesPerSecond > 0)
        {
            etaSource = FormatDuration((total - processed) / _smoothedBytesPerSecond);
        }
        else
        {
            etaSource = total > 0 && processed >= total && percent < 100
                ? "Son kontroller"
                : "—";
        }

        EtaText.Text = L(etaSource);
        _refreshDynamicText = () =>
        {
            RefreshCommonText();
            ProgressTitleText.Text = L(progressTitleSource);
            DetailText.Text = L(progressDetailSource);
            SpeedText.Text = L(speedSource);
            EtaText.Text = L(etaSource);
        };
    }

    public void ShowCompleted(RecoveryBatchReport report, bool scanResumed, bool scanStayedPaused)
    {
        _isRunning = false;
        _isRecoveryPaused = false;
        _stopwatch.Stop();
        PauseResumeButton.IsEnabled = false;
        PauseResumeButton.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Visible;
        RecoveryProgressBar.Value = 100;
        PercentText.Text = "100%";
        bool clean = report.Failed == 0;
        ProgressTitleText.Text = L(clean
            ? "Kurtarma tamamlandı"
            : "Kurtarma tamamlandı • bazı dosyalar kurtarılamadı");
        DetailText.Text = L($"{report.Succeeded:N0} kurtarıldı • {report.Partial:N0} kısmi • {report.Failed:N0} başarısız");
        TransferredText.Text = RecoveryFileItem.FormatBytes(report.WrittenBytes);
        string completedSpeedSource = _smoothedBytesPerSecond > 0
            ? $"{RecoveryFileItem.FormatBytes((long)_smoothedBytesPerSecond)}/sn"
            : "Tamamlandı";
        SpeedText.Text = L(completedSpeedSource);
        EtaText.Text = L("Tamamlandı");
        ResumeInfoText.Text = scanResumed
            ? L("Tarama kaldığı noktadan devam ediyor.")
            : scanStayedPaused
                ? L("Tarama kurtarma öncesinde zaten duraklatılmış olduğu için duraklatılmış kalıyor.")
                : L("Kurtarılan dosyalar hedef klasöre yazıldı.");
        CancelButtonText.Text = L("Kapat");
        CancelButton.IsEnabled = true;
        _refreshDynamicText = () =>
        {
            RefreshCommonText();
            ProgressTitleText.Text = L(clean
                ? "Kurtarma tamamlandı"
                : "Kurtarma tamamlandı • bazı dosyalar kurtarılamadı");
            DetailText.Text = L($"{report.Succeeded:N0} kurtarıldı • {report.Partial:N0} kısmi • {report.Failed:N0} başarısız");
            SpeedText.Text = L(completedSpeedSource);
            EtaText.Text = L("Tamamlandı");
            ResumeInfoText.Text = scanResumed
                ? L("Tarama kaldığı noktadan devam ediyor.")
                : scanStayedPaused
                    ? L("Tarama kurtarma öncesinde zaten duraklatılmış olduğu için duraklatılmış kalıyor.")
                    : L("Kurtarılan dosyalar hedef klasöre yazıldı.");
        };
    }

    public void ShowCancelled(bool scanResumed, bool scanStayedPaused)
    {
        _isRunning = false;
        _isRecoveryPaused = false;
        _stopwatch.Stop();
        PauseResumeButton.IsEnabled = false;
        PauseResumeButton.Visibility = Visibility.Collapsed;
        ProgressTitleText.Text = L("Kurtarma durduruldu");
        DetailText.Text = L("Tamamlanan dosyalar hedef klasörde korunuyor.");
        EtaText.Text = L("İptal edildi");
        ResumeInfoText.Text = scanResumed
            ? L("Kurtarma durduruldu; tarama kaldığı noktadan devam ediyor.")
            : scanStayedPaused
                ? L("Kurtarma durduruldu; önceden duraklatılan tarama duraklatılmış olarak korunuyor.")
                : L("Kurtarma iptal edildi; kaynak diskte herhangi bir değişiklik yapılmadı.");
        CancelButtonText.Text = L("Kapat");
        CancelButton.IsEnabled = true;
        _refreshDynamicText = () =>
        {
            RefreshCommonText();
            ProgressTitleText.Text = L("Kurtarma durduruldu");
            DetailText.Text = L("Tamamlanan dosyalar hedef klasörde korunuyor.");
            EtaText.Text = L("İptal edildi");
            ResumeInfoText.Text = scanResumed
                ? L("Kurtarma durduruldu; tarama kaldığı noktadan devam ediyor.")
                : scanStayedPaused
                    ? L("Kurtarma durduruldu; önceden duraklatılan tarama duraklatılmış olarak korunuyor.")
                    : L("Kurtarma iptal edildi; kaynak diskte herhangi bir değişiklik yapılmadı.");
        };
    }

    public void ShowFailed(string message, bool scanResumed, bool scanStayedPaused)
    {
        _isRunning = false;
        _isRecoveryPaused = false;
        _stopwatch.Stop();
        PauseResumeButton.IsEnabled = false;
        PauseResumeButton.Visibility = Visibility.Collapsed;
        ProgressTitleText.Text = L("Kurtarma tamamlanamadı");
        DetailText.Text = message;
        EtaText.Text = L("Hata");
        ResumeInfoText.Text = scanResumed
            ? L("Kurtarma tamamlanamadı; tarama kaldığı noktadan devam ediyor.")
            : scanStayedPaused
                ? L("Kurtarma tamamlanamadı; önceden duraklatılmış tarama duraklatılmış olarak kalacak.")
                : L("Kaynak diske yazılmadı; tamamlanan dosyalar hedef klasörde korunur.");
        CancelButtonText.Text = L("Kapat");
        CancelButton.IsEnabled = true;
        _refreshDynamicText = () =>
        {
            RefreshCommonText();
            ProgressTitleText.Text = L("Kurtarma tamamlanamadı");
            DetailText.Text = message;
            EtaText.Text = L("Hata");
            ResumeInfoText.Text = scanResumed
                ? L("Kurtarma tamamlanamadı; tarama kaldığı noktadan devam ediyor.")
                : scanStayedPaused
                    ? L("Kurtarma tamamlanamadı; önceden duraklatılmış tarama duraklatılmış olarak kalacak.")
                    : L("Kaynak diske yazılmadı; tamamlanan dosyalar hedef klasörde korunur.");
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
            RequestCancel();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        LocalizationService.Current.LanguageChanged -= LocalizationService_LanguageChanged;
        _closed.TrySetResult(true);
        base.OnClosed(e);
    }

    private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning || _cancelRequested)
            return;

        if (!_isRecoveryPaused)
        {
            _isRecoveryPaused = true;
            _stopwatch.Stop();
            ProgressTitleText.Text = L("Kurtarma duraklatıldı");
            DetailText.Text = L("Aktif dosya güvenli blok sınırında bekliyor. Devam edildiğinde aynı noktadan sürecek.");
            SpeedText.Text = L("Duraklatıldı");
            EtaText.Text = L("Duraklatıldı");
            PauseResumeGlyph.Text = "▶";
            PauseResumeButtonText.Text = L("Kurtarmaya Devam Et");
            _refreshDynamicText = () =>
            {
                RefreshCommonText();
                ProgressTitleText.Text = L("Kurtarma duraklatıldı");
                DetailText.Text = L("Aktif dosya güvenli blok sınırında bekliyor. Devam edildiğinde aynı noktadan sürecek.");
                SpeedText.Text = L("Duraklatıldı");
                EtaText.Text = L("Duraklatıldı");
            };
            PauseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _isRecoveryPaused = false;
        _stopwatch.Start();
        _lastElapsed = _stopwatch.Elapsed;
        ProgressTitleText.Text = L(_lastRunningTitleSource);
        DetailText.Text = L(_lastRunningDetailSource);
        string resumedSpeedSource = _smoothedBytesPerSecond > 0
            ? $"{RecoveryFileItem.FormatBytes((long)_smoothedBytesPerSecond)}/sn"
            : "Hesaplanıyor";
        SpeedText.Text = L(resumedSpeedSource);
        EtaText.Text = L("Hesaplanıyor");
        PauseResumeGlyph.Text = "Ⅱ";
        PauseResumeButtonText.Text = L("Kurtarmayı Duraklat");
        _refreshDynamicText = () =>
        {
            RefreshCommonText();
            ProgressTitleText.Text = L(_lastRunningTitleSource);
            DetailText.Text = L(_lastRunningDetailSource);
            SpeedText.Text = L(resumedSpeedSource);
            EtaText.Text = L("Hesaplanıyor");
        };
        ResumeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            RequestCancel();
            return;
        }

        Close();
    }

    private void RequestCancel()
    {
        if (_cancelRequested)
            return;

        _cancelRequested = true;
        if (_isRecoveryPaused)
        {
            _isRecoveryPaused = false;
            ResumeRequested?.Invoke(this, EventArgs.Empty);
        }
        PauseResumeButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        CancelButtonText.Text = L("İptal ediliyor...");
        ProgressTitleText.Text = L("Kurtarma güvenli biçimde durduruluyor");
        DetailText.Text = L("Aktif çıktı akışı güvenli biçimde kapatılıyor...");
        _refreshDynamicText = () =>
        {
            RefreshCommonText();
            CancelButtonText.Text = L("İptal ediliyor...");
            ProgressTitleText.Text = L("Kurtarma güvenli biçimde durduruluyor");
            DetailText.Text = L("Aktif çıktı akışı güvenli biçimde kapatılıyor...");
        };
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCommonText()
    {
        Title = L("Kurtarma");
        FileCountText.Text = L($"{_fileCount:N0} dosya");
        DestinationText.Text = L($"Hedef: {_destination}");
        OpenFolderButtonText.Text = L("Kurtarılanları Görüntüle");
        CancelButtonText.Text = L(_isRunning ? "Kurtarmayı Durdur" : "Kapat");
        PauseResumeButtonText.Text = L(_isRecoveryPaused ? "Kurtarmaya Devam Et" : "Kurtarmayı Duraklat");
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(() =>
            {
                LocalizationService.Current.ApplyWindow(this);
                _refreshDynamicText?.Invoke();
            });
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_destination))
                throw new DirectoryNotFoundException(L("Kurtarma hedef klasörü artık bulunamıyor."));

            Process.Start(new ProcessStartInfo
            {
                FileName = _destination,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L(ex.Message), L("Kurtarma"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "—";

        TimeSpan value = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(99).TotalSeconds));
        if (value.TotalHours >= 1)
            return L($"{(int)value.TotalHours} sa {value.Minutes:00} dk");
        if (value.TotalMinutes >= 1)
            return L($"{value.Minutes} dk {value.Seconds:00} sn");
        return L($"{Math.Max(1, value.Seconds)} sn");
    }
}
