using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NSXVeriKurtarmaPro;

public enum SessionSavePromptChoice
{
    Cancel = 0,
    Save = 1,
    Discard = 2
}

public partial class SessionSavePromptWindow : Window
{
    public SessionSavePromptChoice Choice { get; private set; } = SessionSavePromptChoice.Cancel;

    public SessionSavePromptWindow(
        string title,
        string message,
        string saveText,
        string discardText,
        string cancelText,
        string scanStateText,
        string projectNoteText,
        bool scanRunning)
    {
        InitializeComponent();

        TitleText.Text = title;
        string bodyMessage = message.Replace("\r\n", "\n");
        if (scanRunning)
        {
            int separator = bodyMessage.IndexOf("\n\n", StringComparison.Ordinal);
            if (separator >= 0 && separator + 2 < bodyMessage.Length)
                bodyMessage = bodyMessage[(separator + 2)..];
        }

        MessageText.Text = bodyMessage;
        SaveButton.Content = saveText;
        DiscardButton.Content = discardText;
        CancelButton.Content = cancelText;
        ScanStateText.Text = scanStateText;
        ProjectNoteText.Text = projectNoteText;

        if (scanRunning)
        {
            ScanStateBadge.Background = BrushFrom("#EEF4FF");
            ScanStateBadge.BorderBrush = BrushFrom("#D7E5FF");
            ScanStateDot.Fill = BrushFrom("#0B57C9");
            ScanStateText.Foreground = BrushFrom("#0B57C9");
        }
        else
        {
            ScanStateBadge.Background = BrushFrom("#ECFDF3");
            ScanStateBadge.BorderBrush = BrushFrom("#C6F0D5");
            ScanStateDot.Fill = BrushFrom("#12B76A");
            ScanStateText.Foreground = BrushFrom("#027A48");
        }
    }

    private static SolidColorBrush BrushFrom(string value)
        => new((Color)ColorConverter.ConvertFromString(value));

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Choice = SessionSavePromptChoice.Save;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Choice = SessionSavePromptChoice.Discard;
        DialogResult = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = SessionSavePromptChoice.Cancel;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        Choice = SessionSavePromptChoice.Cancel;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            DragMove();
        }
        catch
        {
        }
    }
}
