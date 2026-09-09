using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace NSXVeriKurtarmaPro.Services;

public static class ElevationService
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Release/Publish manifest ile zaten yonetici olarak acilir. Debug/F5 normal yetkiyle
    /// acildiginda ham disk erisimi gercekten baslatilacagi anda Windows UAC dogrudan devreye girer.
    /// Uygulama icinde ek bir Evet/Hayir onay penceresi gosterilmez.
    /// </summary>
    public static bool EnsureAdministratorForRawAccess()
    {
        if (IsAdministrator())
            return true;

        try
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("Uygulama yolu belirlenemedi.");

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });

            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Windows UAC kullanici tarafindan iptal edildi. Uygulama acik kalir.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                string.Format(
                    LocalizationService.Current.Translate("Yonetici olarak yeniden baslatilamadi.\n\n{0}"),
                    LocalizationService.Current.Translate(ex.Message)),
                LocalizationService.Current.Translate("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }
}
