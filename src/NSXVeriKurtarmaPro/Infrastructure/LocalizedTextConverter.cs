using System.Globalization;
using System.Windows.Data;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro.Infrastructure;

/// <summary>
/// ViewModel'den gelen kategori/durum gibi metinleri mevcut dil paketine cevirir.
/// Ikinci binding LocalizationService.Revision'dir; dil degisince converter yeniden calisir.
/// </summary>
public sealed class LocalizedTextConverter : IMultiValueConverter, IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string source = value?.ToString() ?? string.Empty;
        return LocalizationService.Current.Translate(source);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string source = values.Length > 0 ? values[0]?.ToString() ?? string.Empty : string.Empty;
        string translated = LocalizationService.Current.Translate(source);

        if (parameter is string suffix && suffix.Length > 0)
            translated += LocalizationService.Current.Translate(suffix);

        return translated;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
