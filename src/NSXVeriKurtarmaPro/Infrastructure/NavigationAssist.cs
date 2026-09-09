using System.Windows;

namespace NSXVeriKurtarmaPro.Infrastructure;

public static class NavigationAssist
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(NavigationAssist),
        new FrameworkPropertyMetadata(false));

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
}
