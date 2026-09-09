using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro;

public partial class StartupLoadingWindow : Window, INotifyPropertyChanged
{
    private double _progressPercent;
    private string _stepTitle = L("Başlangıç hazırlanıyor...");
    private string _stepDetail = L("Uygulama açılış modülleri hazırlanıyor.");

    public StartupLoadingWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (Math.Abs(_progressPercent - value) < 0.01)
                return;

            _progressPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ProgressFillWidth));
        }
    }

    public string ProgressText => $"%{Math.Round(ProgressPercent):0}";

    public double ProgressFillWidth => 225d * Math.Clamp(ProgressPercent, 0d, 100d) / 100d;

    public Visibility NonTurkishOverlayVisibility
    {
        get
        {
            string code = LocalizationService.Current.CurrentLanguage?.Code ?? "tr-TR";
            return code.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    public string StepTitle
    {
        get => _stepTitle;
        private set
        {
            if (string.Equals(_stepTitle, value, StringComparison.Ordinal))
                return;

            _stepTitle = value;
            OnPropertyChanged();
        }
    }

    public string StepDetail
    {
        get => _stepDetail;
        private set
        {
            if (string.Equals(_stepDetail, value, StringComparison.Ordinal))
                return;

            _stepDetail = value;
            OnPropertyChanged();
        }
    }

    public void UpdateProgress(double percent, string title, string detail)
    {
        ProgressPercent = Math.Clamp(percent, 0, 100);
        StepTitle = L(title);
        StepDetail = L(detail);
    }

    private static string L(string source) => LocalizationService.Current.Translate(source);

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
