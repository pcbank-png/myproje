using System.Text;

namespace NSXVeriKurtarmaPro.Services;

public static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NSX Yazılım",
        "NSX Veri Kurtarma Pro",
        "Logs");

    public static string CurrentLogPath =>
        Path.Combine(LogDirectory, $"NSXRecovery_{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}\r\n{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}\r\n";
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Log yazma arızası ana kurtarma işlemini etkilemez.
        }
    }
}
