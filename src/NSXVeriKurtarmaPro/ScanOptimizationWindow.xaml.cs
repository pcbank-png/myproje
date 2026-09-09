using System.ComponentModel;
using System.Windows;
using NSXVeriKurtarmaPro.Services;
using NSXVeriKurtarmaPro.ViewModels;

namespace NSXVeriKurtarmaPro;

public partial class ScanOptimizationWindow : Window
{
    private bool _canClose;

    public ScanOptimizationWindow(Window owner, MainViewModel viewModel)
    {
        InitializeComponent();
        Owner = owner;
        DataContext = viewModel;
        Title = Heading.Text = LocalizationService.Current.Translate("Dosyalar optimize ediliyor");
        Explanation.Text = LocalizationService.Current.Translate(
            "Tarama sonuçları hazırlanıyor. Dosya listesi ve klasör ağacı hazır olduğunda bu pencere kapanacak.");
    }

    public void CloseWhenComplete()
    {
        _canClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_canClose && DataContext is MainViewModel { IsOrganizingResults: true })
            e.Cancel = true;
        base.OnClosing(e);
    }
}
