using Microsoft.Win32;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using NSXVeriKurtarmaPro.Infrastructure;
using NSXVeriKurtarmaPro.Models;
using NSXVeriKurtarmaPro.Services;
using NSXVeriKurtarmaPro.ViewModels;

namespace NSXVeriKurtarmaPro;

public partial class MainWindow : Window
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDeviceTypeVolume = 0x00000002;

    // Windows 11 DWM pencere kenarligi / yuvarlatma ayarlari.
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDefault = 0;
    private const int DwmwcpDoNotRound = 1;
    private const uint DwmColorDefault = 0xFFFFFFFF;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private readonly WindowChrome _normalChrome;
    private bool _workAreaMaximized;
    private bool _restoringFromMinimize;
    private ScanOptimizationWindow? _optimizationWindow;
    private bool _shutdownClosePending;
    private bool _allowFinalClose;
    private Rect _restoreBounds;
    private readonly DispatcherTimer _spinnerTimer;
    private readonly DispatcherTimer _thumbnailViewportTimer;
    private readonly DispatcherTimer _mediaRemovalRefreshTimer;
    private readonly DispatcherTimer _mediaArrivalRefreshTimer;
    private readonly DispatcherTimer _storageTopologyPollTimer;
    private ScrollViewer? _recoveryFilesScrollViewer;
    private ScrollViewer? _recoveryThumbnailScrollViewer;
    private HwndSource? _hwndSource;
    private bool _mediaArrivalRefreshPending;
    private bool _storageTopologyPollBusy;
    private HashSet<string> _knownReadyVolumeRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRemovedVolumeRoots = new(StringComparer.OrdinalIgnoreCase);
    private const double CollapsedNavigationPaneWidth = 58d;
    private const double DefaultNavigationPaneWidth = 250d;
    private const double MinExpandedNavigationPaneWidth = 250d;
    private const double MaxExpandedNavigationPaneWidth = 560d;

    private double _spinnerAngle;
    private bool _isNavigationPaneCollapsed;
    private double _expandedNavigationPaneWidth = DefaultNavigationPaneWidth;
    private string _resultViewMode = "Thumbnail";
    private bool _resultViewInitializedForCurrentSession;
    private RecoveryPreviewWindow? _activePreviewWindow;
    private bool _scanCompletionDialogOpen;
    private bool _returnHomePending;
    private bool _resultDateRangeFilterInstalled;
    private bool _suppressResultDateRangeRefresh;
    private DatePicker? _resultDateFromPicker;
    private DatePicker? _resultDateToPicker;
    private ToggleButton? _resultPhotoTypeToggle;
    private ToggleButton? _resultVideoTypeToggle;
    private ToggleButton? _resultFileTypeToggle;
    private TextBlock? _resultFileTypeTitleText;
    private Predicate<object>? _resultFilterBeforeDateRange;

    public MainWindow()
    {
        Debug.WriteLine("[NSX STARTUP] MW-1 constructor entered");
        InitializeComponent();
        Debug.WriteLine("[NSX STARTUP] MW-2 InitializeComponent completed");
        var viewModel = new MainViewModel();
        Debug.WriteLine("[NSX STARTUP] MW-3 MainViewModel constructed");
        DataContext = viewModel;
        Debug.WriteLine("[NSX STARTUP] MW-4 DataContext assigned");
        viewModel.PropertyChanged += MainViewModel_PropertyChanged;
        viewModel.ScanCompleted += MainViewModel_ScanCompleted;
        LocalizationService.Current.LanguageChanged += LocalizationService_LanguageChanged;
        ProUiSettings.SettingsChanged += ProUiSettings_SettingsChanged;
        _expandedNavigationPaneWidth = ClampNavigationPaneWidth(ProUiSettings.Current.RecoveryNavigationPaneWidth);
        SyncWorkspaceVisibility(viewModel);
        Debug.WriteLine("[NSX STARTUP] MW-5 workspace visibility synced");

        _normalChrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(8),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        };

        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(24)
        };
        _spinnerTimer.Tick += SpinnerTimer_Tick;

        _thumbnailViewportTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _thumbnailViewportTimer.Tick += ThumbnailViewportTimer_Tick;

        _mediaRemovalRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _mediaRemovalRefreshTimer.Tick += MediaRemovalRefreshTimer_Tick;

        _mediaArrivalRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _mediaArrivalRefreshTimer.Tick += MediaArrivalRefreshTimer_Tick;

        _storageTopologyPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _storageTopologyPollTimer.Tick += StorageTopologyPollTimer_Tick;

        viewModel.RecoveryFiles.CollectionChanged += RecoveryFiles_CollectionChanged;
        Debug.WriteLine("[NSX STARTUP] MW-6 timers and handlers ready");

        // SAFE WINDOW MODE: WindowChrome is intentionally not attached during startup.
        // Native frame changes could recursively re-enter WPF layout on some Windows 11/.NET 8 combinations.
        Debug.WriteLine("[NSX STARTUP] MW-7 managed window mode ready");

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closed += MainWindow_Closed;
        Debug.WriteLine("[NSX STARTUP] MW-8 constructor completed");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        Debug.WriteLine("[NSX STARTUP] MW-9 SourceInitialized entered");
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WindowMessageHook);
        Debug.WriteLine("[NSX STARTUP] MW-10 SourceInitialized completed");
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmDeviceChange)
            return IntPtr.Zero;

        if (!ProUiSettings.Current.AutoRefreshDevices)
            return IntPtr.Zero;

        long change = wParam.ToInt64();

        // SD kart mevcut bir USB/kart okuyucuya takıldığında Windows bazen DBT_DEVICEARRIVAL
        // yerine yalnız DBT_DEVNODES_CHANGED yayınlar. lParam bu mesajda boş olabilir; bu yüzden
        // volume header kontrolünden önce yakalayıp debounce refresh kuyruğuna alıyoruz.
        if (change == DbtDevNodesChanged)
        {
            QueueMediaArrivalRefresh();
            return IntPtr.Zero;
        }

        if (!IsStorageVolumeChange(lParam))
            return IntPtr.Zero;

        if (change == DbtDeviceRemoveComplete)
        {
            foreach (string root in GetChangedVolumeRoots(lParam))
                _pendingRemovedVolumeRoots.Add(root);

            _mediaArrivalRefreshPending = false;
            _mediaArrivalRefreshTimer.Stop();
            _mediaRemovalRefreshTimer.Stop();
            _mediaRemovalRefreshTimer.Start();
        }
        else if (change == DbtDeviceArrival)
        {
            QueueMediaArrivalRefresh();
        }

        return IntPtr.Zero;
    }

    private static bool IsStorageVolumeChange(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
            return false;

        try
        {
            DevBroadcastHeader header = Marshal.PtrToStructure<DevBroadcastHeader>(lParam);
            return header.DeviceType == DbtDeviceTypeVolume;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetChangedVolumeRoots(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
            return Array.Empty<string>();

        try
        {
            DevBroadcastVolume volume = Marshal.PtrToStructure<DevBroadcastVolume>(lParam);
            if (volume.DeviceType != DbtDeviceTypeVolume || volume.UnitMask == 0)
                return Array.Empty<string>();

            var roots = new List<string>();
            for (int index = 0; index < 26; index++)
            {
                if ((volume.UnitMask & (1u << index)) == 0)
                    continue;

                roots.Add($"{(char)('A' + index)}:\\");
            }

            return roots;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void QueueMediaArrivalRefresh()
    {
        if (!ProUiSettings.Current.AutoRefreshDevices)
            return;

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        if (vm.IsRefreshingDevices || (vm.IsBusy && !vm.IsScanRunning))
        {
            _mediaArrivalRefreshPending = true;
            return;
        }

        _mediaArrivalRefreshPending = false;
        _mediaArrivalRefreshTimer.Stop();
        _mediaArrivalRefreshTimer.Start();
    }

    private async void MediaArrivalRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _mediaArrivalRefreshTimer.Stop();

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (DataContext is not MainViewModel vm)
            return;

        if (vm.IsRefreshingDevices || (vm.IsBusy && !vm.IsScanRunning))
        {
            _mediaArrivalRefreshPending = true;
            return;
        }

        _mediaArrivalRefreshPending = false;
        await vm.RefreshAfterMediaArrivalAsync();
        _knownReadyVolumeRoots = GetReadyStorageVolumeRoots();
    }

    private async void MediaRemovalRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _mediaRemovalRefreshTimer.Stop();

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (DataContext is MainViewModel vm)
        {
            if (vm.IsRefreshingDevices)
            {
                _mediaRemovalRefreshTimer.Start();
                return;
            }

            string[] removedRoots = _pendingRemovedVolumeRoots.ToArray();
            _pendingRemovedVolumeRoots.Clear();
            await vm.RefreshAfterMediaRemovalAsync(removedRoots);
            _knownReadyVolumeRoots = GetReadyStorageVolumeRoots();
            if (!vm.IsScanRunning)
                DeviceScrollViewer.ScrollToTop();
        }
    }

    private async void StorageTopologyPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_storageTopologyPollBusy || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _storageTopologyPollBusy = true;
        try
        {
            HashSet<string> currentRoots = GetReadyStorageVolumeRoots();
            if (_knownReadyVolumeRoots.Count == 0)
            {
                _knownReadyVolumeRoots = currentRoots;
                return;
            }

            string[] removedRoots = _knownReadyVolumeRoots
                .Except(currentRoots, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] addedRoots = currentRoots
                .Except(_knownReadyVolumeRoots, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (removedRoots.Length == 0 && addedRoots.Length == 0)
                return;

            _knownReadyVolumeRoots = currentRoots;

            if (DataContext is not MainViewModel vm)
                return;

            // Removal path kaynak aygıtın çıkıp çıkmadığını da bilir ve aktif taramanın resume
            // noktasını korur. Başka bir aygıt çıktıysa yalnız cihaz listesi merge edilir.
            if (removedRoots.Length > 0)
            {
                await vm.RefreshAfterMediaRemovalAsync(removedRoots);
                return;
            }

            // Kart okuyucu zaten bağlıyken SD kart takılması genellikle burada yakalanır.
            // Tarama aktifse ViewModel SelectedDevice/scan task'ı koruyarak listeyi merge eder.
            if (addedRoots.Length > 0)
            {
                if (vm.IsRefreshingDevices || (vm.IsBusy && !vm.IsScanRunning))
                {
                    _mediaArrivalRefreshPending = true;
                    return;
                }

                await vm.RefreshAfterMediaArrivalAsync();
            }
        }
        catch (Exception ex)
        {
            // Fallback watcher hiçbir koşulda tarama/kurtarma işini bozmamalı.
            AppLog.Error("Depolama topolojisi arka plan kontrolü başarısız.", ex);
        }
        finally
        {
            _storageTopologyPollBusy = false;
        }
    }

    private static HashSet<string> GetReadyStorageVolumeRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed) || !drive.IsReady)
                        continue;

                    string root = drive.RootDirectory.FullName;
                    if (!string.IsNullOrWhiteSpace(root))
                        roots.Add(root.TrimEnd('\\') + "\\");
                }
                catch
                {
                    // Bir kart okuyucunun kısa süreli ready/not-ready geçişi diğer aygıtları etkilemez.
                }
            }
        }
        catch
        {
        }

        return roots;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NSX STARTUP] MW-11 Loaded entered");
        SyncSpinnerAnimation();
        _knownReadyVolumeRoots = GetReadyStorageVolumeRoots();
        if (ProUiSettings.Current.AutoRefreshDevices)
            _storageTopologyPollTimer.Start();

        RestoreSavedMainWindowLayout();
        RefreshLanguageMenuVisuals(reloadLanguages: true);
        ApplyNavigationPaneState();
        InstallResultDateRangeFilter();

        // Varsayilan Pro davranis: gorev cubugunu koruyarak calisma alanini doldur.
        // Kullanici Ayarlar'dan kapatirsa kaydedilen normal pencere yerlesimi kullanilir.
        if (ProUiSettings.Current.StartInWorkArea)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Debug.WriteLine("[NSX STARTUP] MW-13 maximize entered");
                EnterWorkAreaMaximized();
                Debug.WriteLine("[NSX STARTUP] MW-14 maximize completed");
            }, DispatcherPriority.Loaded);
        }

        if (DataContext is MainViewModel vm && !string.IsNullOrWhiteSpace(App.StartupProjectPath))
            _ = vm.LoadProjectAsync(App.StartupProjectPath);
        Debug.WriteLine("[NSX STARTUP] MW-12 Loaded completed");
    }


    private void InstallResultDateRangeFilter()
    {
        if (_resultDateRangeFilterInstalled || DataContext is not MainViewModel vm)
            return;

        if (ResultFilterPopup.Child is not Border popupBorder || popupBorder.Child is not StackPanel popupPanel)
            return;

        _resultFilterBeforeDateRange = vm.RecoveryFilesView.Filter;

        // Minimum dosya boyutu filtresi artık kullanılmıyor. Aynı konumu tek satırlık
        // Resim / Video / Dosya sonuç filtresi kaplar. Eski kaydedilmiş MB değeri de
        // görünmeyen bir filtre olarak sonuçları etkilemesin diye sıfırlanır.
        int typeFilterIndex = Math.Max(0, popupPanel.Children.Count - 1);
        if (popupPanel.Children.Count > 0 && popupPanel.Children[typeFilterIndex] is Border)
            popupPanel.Children.RemoveAt(typeFilterIndex);

        if (vm.MinimumFileSizeMb > 0d)
            vm.MinimumFileSizeMb = 0d;

        Border typeFilterCard = CreateResultFileTypeCard();
        popupPanel.Children.Insert(Math.Min(typeFilterIndex, popupPanel.Children.Count), typeFilterCard);

        Border dateRangeCard = CreateResultDateRangeCard();
        popupPanel.Children.Add(dateRangeCard);
        _resultDateRangeFilterInstalled = true;
    }

    private Border CreateResultFileTypeCard()
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE9, 0xF1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var content = new StackPanel();
        card.Child = content;

        _resultFileTypeTitleText = new TextBlock
        {
            Text = L("TÜR"),
            FontSize = 10.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0x40, 0x54)),
            Margin = new Thickness(1, 0, 0, 7)
        };
        content.Children.Add(_resultFileTypeTitleText);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _resultPhotoTypeToggle = CreateResultFileTypeToggle(L("Resim"), "Photo");
        _resultVideoTypeToggle = CreateResultFileTypeToggle(L("Video"), "Video");
        _resultFileTypeToggle = CreateResultFileTypeToggle(L("Dosya"), "File");

        Grid.SetColumn(_resultPhotoTypeToggle, 0);
        Grid.SetColumn(_resultVideoTypeToggle, 2);
        Grid.SetColumn(_resultFileTypeToggle, 4);
        row.Children.Add(_resultPhotoTypeToggle);
        row.Children.Add(_resultVideoTypeToggle);
        row.Children.Add(_resultFileTypeToggle);
        content.Children.Add(row);

        return card;
    }

    private ToggleButton CreateResultFileTypeToggle(string text, string tag)
    {
        var toggle = new ToggleButton
        {
            Content = text,
            Tag = tag,
            Height = 36,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = text
        };

        if (TryFindResource("ResultFilterSwitchStyle") is Style style)
            toggle.Style = style;

        toggle.Checked += ResultFileTypeToggle_Changed;
        toggle.Unchecked += ResultFileTypeToggle_Changed;
        return toggle;
    }

    private bool HasResultFileTypeFilter() =>
        _resultPhotoTypeToggle?.IsChecked == true ||
        _resultVideoTypeToggle?.IsChecked == true ||
        _resultFileTypeToggle?.IsChecked == true;

    private bool MatchesResultFileType(RecoveryFileItem file)
    {
        bool photoSelected = _resultPhotoTypeToggle?.IsChecked == true;
        bool videoSelected = _resultVideoTypeToggle?.IsChecked == true;
        bool fileSelected = _resultFileTypeToggle?.IsChecked == true;

        if (!photoSelected && !videoSelected && !fileSelected)
            return true;

        bool isPhoto = FileTypeHelper.IsPhoto(file.Extension);
        bool isVideo = FileTypeHelper.IsVideo(file.Extension);
        bool isFile = !isPhoto && !isVideo;

        return (photoSelected && isPhoto) ||
               (videoSelected && isVideo) ||
               (fileSelected && isFile);
    }

    private void ResultFileTypeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsProResultFilterEnabled && HasResultFileTypeFilter())
            vm.IsProResultFilterEnabled = true;

        UpdateResultDateRangeFilterHook();
        RefreshResultDateRangeFilter();
    }

    private Border CreateResultDateRangeCard()
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE9, 0xF1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var content = new StackPanel();
        card.Child = content;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        titlePanel.Children.Add(new TextBlock
        {
            Text = "\uE787",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x73, 0xD9)),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = L("Tarih Aralığı"),
            FontSize = 11.2,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0x40, 0x54)),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(titlePanel);

        var clearButton = new Button
        {
            Content = L("Temizle"),
            Padding = new Thickness(9, 3, 9, 3),
            MinWidth = 58,
            FontSize = 9.4,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x73, 0xD9)),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xE3, 0xF3)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        clearButton.Click += ResultDateRangeClear_Click;
        Grid.SetColumn(clearButton, 1);
        header.Children.Add(clearButton);
        content.Children.Add(header);

        content.Children.Add(new TextBlock
        {
            Text = L("Dosyanın çekim, değiştirilme veya oluşturulma tarihine göre iki tarih arasında filtreler."),
            FontSize = 9.1,
            Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3)),
            Margin = new Thickness(0, 4, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });

        var dateGrid = new Grid();
        dateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        dateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel fromPanel = CreateDatePickerPanel(L("Başlangıç"), out _resultDateFromPicker);
        StackPanel toPanel = CreateDatePickerPanel(L("Bitiş"), out _resultDateToPicker);
        Grid.SetColumn(fromPanel, 0);
        Grid.SetColumn(toPanel, 2);
        dateGrid.Children.Add(fromPanel);
        dateGrid.Children.Add(toPanel);
        content.Children.Add(dateGrid);

        content.Children.Add(new TextBlock
        {
            Text = L("Başlangıç ve bitiş günleri dahildir. Tarihi bilinmeyen kayıtlar aralık seçiliyken gizlenir."),
            FontSize = 8.7,
            Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3)),
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return card;
    }

    private StackPanel CreateDatePickerPanel(string label, out DatePicker picker)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9.3,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85)),
            Margin = new Thickness(1, 0, 0, 4)
        });

        picker = new DatePicker
        {
            Height = 32,
            FontSize = 10.4,
            SelectedDateFormat = DatePickerFormat.Short,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xE2, 0xEC)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0x40, 0x54)),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = L("Tarih seçin")
        };
        picker.SelectedDateChanged += ResultDateRangePicker_SelectedDateChanged;
        picker.CalendarOpened += (_, _) => ResultFilterPopup.StaysOpen = true;
        picker.CalendarClosed += (_, _) => ResultFilterPopup.StaysOpen = false;
        panel.Children.Add(picker);
        return panel;
    }

    private bool MatchesResultDateRange(RecoveryFileItem file)
    {
        DateTime? from = _resultDateFromPicker?.SelectedDate?.Date;
        DateTime? to = _resultDateToPicker?.SelectedDate?.Date;

        if (!from.HasValue && !to.HasValue)
            return true;

        DateTime? resultDate = file.ResultDate?.ToLocalTime().Date;
        if (!resultDate.HasValue)
            return false;

        DateTime lower = from ?? DateTime.MinValue.Date;
        DateTime upper = to ?? DateTime.MaxValue.Date;
        if (lower > upper)
            (lower, upper) = (upper, lower);

        return resultDate.Value >= lower && resultDate.Value <= upper;
    }

    private void ResultDateRangePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressResultDateRangeRefresh)
            return;

        if (DataContext is MainViewModel vm &&
            !vm.IsProResultFilterEnabled &&
            (_resultDateFromPicker?.SelectedDate is not null || _resultDateToPicker?.SelectedDate is not null))
        {
            vm.IsProResultFilterEnabled = true;
        }

        UpdateResultDateRangeFilterHook();
        RefreshResultDateRangeFilter();
    }

    private void ResultDateRangeClear_Click(object sender, RoutedEventArgs e)
    {
        _suppressResultDateRangeRefresh = true;
        try
        {
            if (_resultDateFromPicker is not null)
                _resultDateFromPicker.SelectedDate = null;
            if (_resultDateToPicker is not null)
                _resultDateToPicker.SelectedDate = null;
        }
        finally
        {
            _suppressResultDateRangeRefresh = false;
        }

        UpdateResultDateRangeFilterHook();
        RefreshResultDateRangeFilter();
    }

    private void UpdateResultDateRangeFilterHook()
    {
        if (DataContext is not MainViewModel vm || _resultFilterBeforeDateRange is null)
            return;

        bool hasDateRange = _resultDateFromPicker?.SelectedDate is not null ||
                            _resultDateToPicker?.SelectedDate is not null;
        bool hasFileTypeFilter = HasResultFileTypeFilter();

        if (!hasDateRange && !hasFileTypeFilter)
        {
            if (!ReferenceEquals(vm.RecoveryFilesView.Filter, _resultFilterBeforeDateRange))
                vm.RecoveryFilesView.Filter = _resultFilterBeforeDateRange;
            return;
        }

        if (ReferenceEquals(vm.RecoveryFilesView.Filter, _resultFilterBeforeDateRange))
        {
            Predicate<object> baseFilter = _resultFilterBeforeDateRange;
            vm.RecoveryFilesView.Filter = item =>
            {
                if (!baseFilter(item))
                    return false;

                if (!vm.IsProResultFilterEnabled || item is not RecoveryFileItem file)
                    return true;

                if (!MatchesResultFileType(file))
                    return false;

                return MatchesResultDateRange(file);
            };
        }
    }

    private void RefreshResultDateRangeFilter()
    {
        if (DataContext is not MainViewModel vm)
            return;

        MethodInfo? refreshMethod = typeof(MainViewModel).GetMethod(
            "RefreshProResultFilters",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (refreshMethod is not null)
        {
            refreshMethod.Invoke(vm, null);
            return;
        }

        vm.RecoveryFilesView.Refresh();
    }


    private void RestoreSavedMainWindowLayout()
    {
        ProUiSettingsState settings = ProUiSettings.Current;
        ProWindowLayoutState layout = settings.MainWindowLayout;

        if (settings.RememberMainWindowLayout && layout.IsValid)
        {
            Rect work = SystemParameters.WorkArea;
            double width = Math.Min(Math.Max(MinWidth, layout.Width), Math.Max(MinWidth, work.Width));
            double height = Math.Min(Math.Max(MinHeight, layout.Height), Math.Max(MinHeight, work.Height));
            double left = Math.Clamp(layout.Left, work.Left, Math.Max(work.Left, work.Right - width));
            double top = Math.Clamp(layout.Top, work.Top, Math.Max(work.Top, work.Bottom - height));

            _restoreBounds = new Rect(left, top, width, height);
            if (!settings.StartInWorkArea)
            {
                Width = width;
                Height = height;
                Left = left;
                Top = top;
            }
        }
        else
        {
            _restoreBounds = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        }
    }

    private void SaveCurrentMainWindowLayout()
    {
        if (!ProUiSettings.Current.RememberMainWindowLayout)
            return;

        Rect bounds = _workAreaMaximized && _restoreBounds.Width > 0 && _restoreBounds.Height > 0
            ? _restoreBounds
            : new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

        ProUiSettings.SaveMainWindowLayout(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private void ProUiSettings_SettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            ProUiSettingsState settings = ProUiSettings.Current;

            if (settings.AutoRefreshDevices)
            {
                if (!_storageTopologyPollTimer.IsEnabled)
                    _storageTopologyPollTimer.Start();
                QueueMediaArrivalRefresh();
            }
            else
            {
                _mediaArrivalRefreshPending = false;
                _mediaArrivalRefreshTimer.Stop();
                _mediaRemovalRefreshTimer.Stop();
                _storageTopologyPollTimer.Stop();
            }

            if (settings.StartInWorkArea)
            {
                EnterWorkAreaMaximized();
            }
            else if (_workAreaMaximized)
            {
                ExitWorkAreaMaximized();
            }

            if (settings.AutoGenerateThumbnails && IsRecoveryWorkspaceActive())
                ScheduleVisibleThumbnailLoad();

            if (!_isNavigationPaneCollapsed)
            {
                _expandedNavigationPaneWidth = ClampNavigationPaneWidth(settings.RecoveryNavigationPaneWidth);
                ApplyNavigationPaneState();
            }
        }, DispatcherPriority.Background);
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MainViewModel filterVm && !filterVm.IsProResultFilterEnabled)
        {
            bool activateMasterFilter = e.PropertyName switch
            {
                nameof(MainViewModel.HideZeroByteFiles) => filterVm.HideZeroByteFiles,
                nameof(MainViewModel.HideThumbnailFiles) => filterVm.HideThumbnailFiles,
                nameof(MainViewModel.HideDamagedFiles) => filterVm.HideDamagedFiles,
                nameof(MainViewModel.HideSystemTemporaryFiles) => filterVm.HideSystemTemporaryFiles,
                nameof(MainViewModel.HideExistingFiles) => filterVm.HideExistingFiles,
                nameof(MainViewModel.MinimumRecoveryConfidence) => filterVm.MinimumRecoveryConfidence > 0,
                nameof(MainViewModel.MinimumFileSizeMb) => filterVm.MinimumFileSizeMb > 0d,
                _ => false
            };

            if (activateMasterFilter)
                filterVm.IsProResultFilterEnabled = true;
        }

        if (e.PropertyName == nameof(MainViewModel.IsOrganizingResults) && sender is MainViewModel organizingVm)
        {
            if (organizingVm.IsOrganizingResults && IsVisible && !_shutdownClosePending)
            {
                if (_optimizationWindow is null)
                {
                    _optimizationWindow = new ScanOptimizationWindow(this, organizingVm);
                    _optimizationWindow.Show();
                }
            }
            else
            {
                _optimizationWindow?.CloseWhenComplete();
                _optimizationWindow = null;
            }
        }
        if (e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.IsPaused))
            Dispatcher.BeginInvoke(SyncSpinnerAnimation, DispatcherPriority.Render);

        if (e.PropertyName is nameof(MainViewModel.IsRecoveryWorkspace) or nameof(MainViewModel.IsLandingWorkspace))
            Dispatcher.BeginInvoke(() =>
            {
                if (sender is MainViewModel vm)
                    SyncWorkspaceVisibility(vm);
            }, DispatcherPriority.DataBind);

        if ((e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.IsRefreshingDevices))
            && _mediaArrivalRefreshPending
            && sender is MainViewModel vm
            && (!vm.IsBusy || vm.IsScanRunning)
            && !vm.IsRefreshingDevices)
        {
            Dispatcher.BeginInvoke(QueueMediaArrivalRefresh, DispatcherPriority.Background);
        }
    }

    private void SyncWorkspaceVisibility(MainViewModel viewModel)
    {
        bool showRecovery = viewModel.IsRecoveryWorkspace;

        if (showRecovery)
        {
            if (!ReferenceEquals(RecoveryWorkspace.DataContext, viewModel))
                RecoveryWorkspace.DataContext = viewModel;

            RecoveryWorkspace.Visibility = Visibility.Visible;
            LandingWorkspace.Visibility = Visibility.Collapsed;

            // Her yeni sonuç oturumu ilk kez resimli görünümle açılır. Kullanıcı daha sonra
            // Liste/Ayrıntılar seçerse aynı oturum boyunca seçimi korunur.
            if (!_resultViewInitializedForCurrentSession)
            {
                _resultViewInitializedForCurrentSession = true;
                ThumbnailResultViewButton.IsChecked = true;
                ApplyResultViewMode("Thumbnail");
            }

            ApplyNavigationPaneState();
        }
        else
        {
            _resultViewInitializedForCurrentSession = false;
            RecoveryWorkspace.Visibility = Visibility.Collapsed;
            RecoveryWorkspace.DataContext = null;
            LandingWorkspace.Visibility = Visibility.Visible;
        }
    }

    private void SpinnerTimer_Tick(object? sender, EventArgs e)
    {
        if (LoadingSpinnerRotor.RenderTransform is not RotateTransform rotate)
            return;

        _spinnerAngle = (_spinnerAngle + 10.0) % 360.0;
        rotate.Angle = _spinnerAngle;
    }

    private void SyncSpinnerAnimation()
    {
        if (DataContext is MainViewModel vm && vm.IsBusy && !vm.IsPaused)
        {
            if (!_spinnerTimer.IsEnabled)
                _spinnerTimer.Start();
        }
        else if (_spinnerTimer.IsEnabled)
        {
            _spinnerTimer.Stop();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _optimizationWindow?.CloseWhenComplete();
        _optimizationWindow = null;
        ProUiSettings.SettingsChanged -= ProUiSettings_SettingsChanged;
        SaveCurrentMainWindowLayout();

        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged -= MainViewModel_PropertyChanged;
            vm.ScanCompleted -= MainViewModel_ScanCompleted;
        }

        LocalizationService.Current.LanguageChanged -= LocalizationService_LanguageChanged;

        _spinnerTimer.Stop();
        _spinnerTimer.Tick -= SpinnerTimer_Tick;

        _thumbnailViewportTimer.Stop();
        _thumbnailViewportTimer.Tick -= ThumbnailViewportTimer_Tick;

        _mediaRemovalRefreshTimer.Stop();
        _mediaRemovalRefreshTimer.Tick -= MediaRemovalRefreshTimer_Tick;

        _mediaArrivalRefreshTimer.Stop();
        _mediaArrivalRefreshTimer.Tick -= MediaArrivalRefreshTimer_Tick;

        _storageTopologyPollTimer.Stop();
        _storageTopologyPollTimer.Tick -= StorageTopologyPollTimer_Tick;

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WindowMessageHook);
            _hwndSource = null;
        }

        if (_recoveryFilesScrollViewer is not null)
            _recoveryFilesScrollViewer.ScrollChanged -= RecoveryFilesScrollViewer_ScrollChanged;

        if (_recoveryThumbnailScrollViewer is not null)
            _recoveryThumbnailScrollViewer.ScrollChanged -= RecoveryThumbnailScrollViewer_ScrollChanged;

        if (DataContext is MainViewModel viewModel)
            viewModel.RecoveryFiles.CollectionChanged -= RecoveryFiles_CollectionChanged;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_workAreaMaximized && WindowState == WindowState.Minimized)
        {
            _restoringFromMinimize = true;
            return;
        }

        if (_workAreaMaximized && WindowState == WindowState.Normal && _restoringFromMinimize)
        {
            _restoringFromMinimize = false;
            Dispatcher.BeginInvoke(ApplyWorkAreaBounds, DispatcherPriority.Send);
        }
    }

    private void EnterWorkAreaMaximized()
    {
        Debug.WriteLine("[NSX STARTUP] MW-M1 managed maximize begin");

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        if (!_workAreaMaximized)
        {
            double restoreWidth = ActualWidth > 0 ? ActualWidth : Width;
            double restoreHeight = ActualHeight > 0 ? ActualHeight : Height;
            if (restoreWidth > 0 && restoreHeight > 0)
                _restoreBounds = new Rect(Left, Top, restoreWidth, restoreHeight);
        }

        _workAreaMaximized = true;
        ResizeMode = ResizeMode.CanMinimize;
        RootShell.Margin = new Thickness(0);
        RootShell.CornerRadius = new CornerRadius(0);
        RootShell.BorderThickness = new Thickness(0);
        RootShell.BorderBrush = Brushes.Transparent;

        ApplyWorkAreaBounds();
        Debug.WriteLine("[NSX STARTUP] MW-M2 managed maximize end");
    }

    private void ExitWorkAreaMaximized(Point? screenMouse = null)
    {
        if (!_workAreaMaximized)
            return;

        _workAreaMaximized = false;
        ResizeMode = ResizeMode.CanResize;
        RootShell.Margin = new Thickness(0);
        RootShell.CornerRadius = new CornerRadius(18);
        RootShell.BorderThickness = new Thickness(1);
        RootShell.BorderBrush = new SolidColorBrush(Color.FromRgb(217, 225, 234));

        double width = Math.Max(MinWidth, _restoreBounds.Width > 0 ? _restoreBounds.Width : 1500);
        double height = Math.Max(MinHeight, _restoreBounds.Height > 0 ? _restoreBounds.Height : 860);
        double left = _restoreBounds.Left;
        double top = _restoreBounds.Top;

        if (screenMouse is Point mouse)
        {
            double ratio = ActualWidth <= 0 ? 0.5 : Math.Clamp(mouse.X / Math.Max(1, SystemParameters.VirtualScreenWidth), 0.15, 0.85);
            left = mouse.X - width * ratio;
            top = Math.Max(SystemParameters.VirtualScreenTop, mouse.Y - 20);
        }

        WindowState = WindowState.Normal;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    private void ApplyWorkAreaBounds()
    {
        if (!_workAreaMaximized)
            return;

        // Managed-only work area sizing. No SetWindowPos / SWP_FRAMECHANGED / DWM frame mutation.
        // This keeps the Windows taskbar visible without re-entering the native frame/layout pipeline.
        Rect work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
            return;

        WindowState = WindowState.Normal;
        Left = work.Left;
        Top = work.Top;
        Width = Math.Max(MinWidth, work.Width);
        Height = Math.Max(MinHeight, work.Height);
    }

    private void ApplyDwmMaximizedVisuals()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int corner = DwmwcpDoNotRound;
        uint border = DwmColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
    }

    private void ApplyDwmNormalVisuals()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int corner = DwmwcpDefault;
        uint border = DwmColorDefault;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (e.ClickCount == 2)
        {
            if (ProUiSettings.Current.StartInWorkArea)
                EnterWorkAreaMaximized();
            else if (_workAreaMaximized)
                ExitWorkAreaMaximized();
            else
                EnterWorkAreaMaximized();

            e.Handled = true;
            return;
        }

        if (_workAreaMaximized || ProUiSettings.Current.StartInWorkArea)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        _restoringFromMinimize = _workAreaMaximized;
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (ProUiSettings.Current.StartInWorkArea)
        {
            EnterWorkAreaMaximized();
            return;
        }

        if (_workAreaMaximized)
            ExitWorkAreaMaximized();
        else
            EnterWorkAreaMaximized();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string L(string source) => LocalizationService.Current.Translate(source);

    private void LanguageMenuButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLanguageMenuVisuals(reloadLanguages: true);
        OpenButtonContextMenu(LanguageMenuButton);
    }

    private void SessionMenuButton_Click(object sender, RoutedEventArgs e) =>
        OpenButtonContextMenu(SessionMenuButton);

    private void MainMenuButton_Click(object sender, RoutedEventArgs e) =>
        OpenButtonContextMenu(MainMenuButton);

    private static void OpenButtonContextMenu(Button button)
    {
        ContextMenu? menu = button.ContextMenu;
        if (menu is null)
            return;

        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 5;
        menu.IsOpen = true;
    }

    private void RefreshLanguageMenuVisuals(bool reloadLanguages)
    {
        LocalizationService localization = LocalizationService.Current;
        if (reloadLanguages)
            localization.ReloadLanguages(raiseLanguageChanged: true);

        LanguagePack? current = localization.CurrentLanguage;
        ImageSource? currentFlag = localization.GetFlagImage(current, 96);
        CurrentLanguageFlag.Source = currentFlag;
        CurrentLanguageFlag.Visibility = currentFlag is null ? Visibility.Collapsed : Visibility.Visible;
        CurrentLanguageFallbackGlyph.Visibility = currentFlag is null ? Visibility.Visible : Visibility.Collapsed;
        LanguageButtonLabel.Text = current?.DisplayName ?? L("Dil Seçiniz");
        UpdateBrandLogoForLanguage(current);
        ApplyNonVisualLocalizedHeaders();

        ContextMenu? menu = LanguageMenuButton.ContextMenu;
        if (menu is null)
            return;

        menu.Items.Clear();
        foreach (LanguagePack language in localization.AvailableLanguages)
        {
            ImageSource? flag = localization.GetFlagImage(language, 96);
            FrameworkElement flagVisual;

            if (flag is not null)
            {
                flagVisual = new Border
                {
                    Width = 24,
                    Height = 16,
                    CornerRadius = new CornerRadius(3),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD7, 0xE0, 0xEA)),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    Child = new Image
                    {
                        Source = flag,
                        Stretch = Stretch.UniformToFill,
                        SnapsToDevicePixels = true
                    }
                };
            }
            else
            {
                flagVisual = new TextBlock
                {
                    Text = "\uE774",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x51, 0x6B, 0x90)),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = language.DisplayName,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = $"  {language.Code}",
                FontSize = 9.2,
                Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3)),
                VerticalAlignment = VerticalAlignment.Center
            });

            var item = new MenuItem
            {
                Header = header,
                Icon = flagVisual,
                Tag = language.Code,
                IsCheckable = true,
                IsChecked = string.Equals(current?.Code, language.Code, StringComparison.OrdinalIgnoreCase),
                StaysOpenOnClick = false,
                Style = (Style)FindResource("DeepScanMenuItemStyle")
            };
            item.Click += LanguageMenuItem_Click;
            menu.Items.Add(item);
        }
    }

    private void UpdateBrandLogoForLanguage(LanguagePack? language)
    {
        if (BrandLogoImage is null)
            return;

        bool isTurkish = language?.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase) == true;
        string assetName = isTurkish ? "logo_ui.png" : "logo_ui_en.png";

        try
        {
            var uri = new Uri($"pack://application:,,,/NSXVeriKurtarmaPro;component/Assets/{assetName}", UriKind.Absolute);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            BrandLogoImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Dil logosu yüklenemedi: {assetName}", ex);
        }
    }

    private void ApplyNonVisualLocalizedHeaders()
    {
        if (RecoveryFilesGrid is null || RecoveryFilesGrid.Columns.Count < 7)
            return;

        RecoveryFilesGrid.Columns[1].Header = L("Ad");
        RecoveryFilesGrid.Columns[2].Header = L("Tarih");
        RecoveryFilesGrid.Columns[3].Header = L("Tür");
        RecoveryFilesGrid.Columns[4].Header = L("Boyut");
        RecoveryFilesGrid.Columns[5].Header = L("Durum");
        RecoveryFilesGrid.Columns[6].Header = L("Güven");

        // DataGridColumn is not a FrameworkElement. Refresh cells explicitly so
        // IValueConverter-backed runtime status/confidence values switch language immediately.
        RecoveryFilesGrid.Items.Refresh();
    }

    private void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string code)
            return;

        if (LocalizationService.Current.SetLanguage(code))
            RefreshLanguageMenuVisuals(reloadLanguages: false);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is MainViewModel viewModel)
                viewModel.RefreshLocalizedText();

            if (_resultFileTypeTitleText is not null)
                _resultFileTypeTitleText.Text = L("TÜR");
            if (_resultPhotoTypeToggle is not null)
            {
                _resultPhotoTypeToggle.Content = L("Resim");
                _resultPhotoTypeToggle.ToolTip = L("Resim");
            }
            if (_resultVideoTypeToggle is not null)
            {
                _resultVideoTypeToggle.Content = L("Video");
                _resultVideoTypeToggle.ToolTip = L("Video");
            }
            if (_resultFileTypeToggle is not null)
            {
                _resultFileTypeToggle.Content = L("Dosya");
                _resultFileTypeToggle.ToolTip = L("Dosya");
            }

            RefreshLanguageMenuVisuals(reloadLanguages: false);
        }, DispatcherPriority.DataBind);
    }

    private void SaveSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (!vm.HasSavableProject)
        {
            MessageBox.Show(
                this,
                L("Kaydedilecek bir tarama oturumu bulunamadı."),
                L("Oturum Kaydet"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _ = SaveProjectWithDialog(vm);
    }

    private async void LoadSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (vm.IsBusy)
        {
            MessageBox.Show(
                this,
                L("Tarama veya kurtarma işlemi sürerken başka bir oturum yüklenemez."),
                L("Oturum Yükle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (vm.HasSavableProject)
        {
            MessageBoxResult choice = MessageBox.Show(
                this,
                L("Mevcut tarama sonuçları yeni oturum yüklenince değiştirilecek. Önce mevcut oturumu kaydetmek ister misiniz?"),
                L("Oturum Yükle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (choice == MessageBoxResult.Cancel)
                return;
            if (choice == MessageBoxResult.Yes && !SaveProjectWithDialog(vm))
                return;
        }

        var dialog = new OpenFileDialog
        {
            Title = L("Tarama Oturumunu Yükle - NSX Veri Kurtarma Projesi"),
            Filter = L("NSX Veri Kurtarma Projesi (*.nsx)|*.nsx"),
            DefaultExt = RecoveryProjectService.ProjectExtension,
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await vm.LoadProjectAsync(dialog.FileName);
    }

    private void LicenseMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenSystemCenter(SystemCenterSection.License);

    private void UpdateMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenSystemCenter(SystemCenterSection.Update);

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenSystemCenter(SystemCenterSection.Settings);

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenSystemCenter(SystemCenterSection.About);

    private void OpenSystemCenter(SystemCenterSection section)
    {
        var window = new SystemCenterWindow(section)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleNavigationPane_Click(object sender, RoutedEventArgs e)
    {
        _isNavigationPaneCollapsed = !_isNavigationPaneCollapsed;
        ApplyNavigationPaneState();
    }

    private void NavigationResizeSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_isNavigationPaneCollapsed)
            return;

        double clampedWidth = ClampNavigationPaneWidth(RecoveryNavigationColumn.ActualWidth);
        _expandedNavigationPaneWidth = clampedWidth;
        RecoveryNavigationColumn.Width = new GridLength(clampedWidth);
        SaveNavigationPaneWidth(clampedWidth);
    }

    private void ApplyNavigationPaneState()
    {
        if (RecoveryNavigationColumn is null)
            return;

        if (_isNavigationPaneCollapsed)
        {
            if (RecoveryNavigationColumn.ActualWidth > CollapsedNavigationPaneWidth + 0.5d)
                _expandedNavigationPaneWidth = ClampNavigationPaneWidth(RecoveryNavigationColumn.ActualWidth);

            RecoveryNavigationColumn.MinWidth = CollapsedNavigationPaneWidth;
            RecoveryNavigationColumn.MaxWidth = CollapsedNavigationPaneWidth;
            RecoveryNavigationColumn.Width = new GridLength(CollapsedNavigationPaneWidth);
            NavigationExpandedContent.Visibility = Visibility.Collapsed;
            NavigationCollapsedRail.Visibility = Visibility.Visible;
            NavigationResizeSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        double width = ClampNavigationPaneWidth(_expandedNavigationPaneWidth);
        _expandedNavigationPaneWidth = width;
        RecoveryNavigationColumn.MinWidth = MinExpandedNavigationPaneWidth;
        RecoveryNavigationColumn.MaxWidth = MaxExpandedNavigationPaneWidth;
        RecoveryNavigationColumn.Width = new GridLength(width);
        NavigationExpandedContent.Visibility = Visibility.Visible;
        NavigationCollapsedRail.Visibility = Visibility.Collapsed;
        NavigationResizeSplitter.Visibility = Visibility.Visible;
        SaveNavigationPaneWidth(width);
    }

    private static double ClampNavigationPaneWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0d)
            return DefaultNavigationPaneWidth;

        return Math.Min(MaxExpandedNavigationPaneWidth, Math.Max(MinExpandedNavigationPaneWidth, width));
    }

    private static void SaveNavigationPaneWidth(double width)
    {
        double clampedWidth = ClampNavigationPaneWidth(width);
        if (Math.Abs(ProUiSettings.Current.RecoveryNavigationPaneWidth - clampedWidth) < 0.5d)
            return;

        ProUiSettings.Update(settings => settings.RecoveryNavigationPaneWidth = clampedWidth);
    }

    private void ResultViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton button && button.Tag is string mode)
            ApplyResultViewMode(mode);
    }

    private void ApplyResultViewMode(string mode)
    {
        if (RecoveryFilesGrid is null || RecoveryThumbnailList is null || RecoveryFilesGrid.Columns.Count < 6)
            return;

        _resultViewMode = mode switch
        {
            "List" => "List",
            "Thumbnail" => "Thumbnail",
            _ => "Details"
        };

        bool thumbnailMode = string.Equals(_resultViewMode, "Thumbnail", StringComparison.Ordinal);
        RecoveryFilesGrid.Visibility = thumbnailMode ? Visibility.Collapsed : Visibility.Visible;
        RecoveryThumbnailList.Visibility = thumbnailMode ? Visibility.Visible : Visibility.Collapsed;
        RecoveryFilesGrid.Tag = _resultViewMode;
        RecoveryFilesGrid.Columns[1].Width = new DataGridLength(1d, DataGridLengthUnitType.Star);

        if (_resultViewMode == "Details")
        {
            RecoveryFilesGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
            RecoveryFilesGrid.RowHeight = 62d;
            for (int index = 2; index < RecoveryFilesGrid.Columns.Count; index++)
                RecoveryFilesGrid.Columns[index].Visibility = Visibility.Visible;
            RecoveryFilesGrid.Columns[1].Width = new DataGridLength(2.3d, DataGridLengthUnitType.Star);
        }
        else if (_resultViewMode == "List")
        {
            RecoveryFilesGrid.HeadersVisibility = DataGridHeadersVisibility.None;
            RecoveryFilesGrid.RowHeight = 48d;
            for (int index = 2; index < RecoveryFilesGrid.Columns.Count; index++)
                RecoveryFilesGrid.Columns[index].Visibility = Visibility.Collapsed;
        }
        else
        {
            RecoveryFilesGrid.HeadersVisibility = DataGridHeadersVisibility.None;
            for (int index = 2; index < RecoveryFilesGrid.Columns.Count; index++)
                RecoveryFilesGrid.Columns[index].Visibility = Visibility.Collapsed;
        }

        if (thumbnailMode)
        {
            RecoveryThumbnailList.UpdateLayout();
            HookRecoveryThumbnailScrollViewer();
        }
        else
        {
            RecoveryFilesGrid.UpdateLayout();
            HookRecoveryFilesScrollViewer();
        }

        ScheduleVisibleThumbnailLoad();
    }

    private bool IsRecoveryWorkspaceActive()
        => RecoveryWorkspace.Visibility == Visibility.Visible
           && RecoveryWorkspace.DataContext is MainViewModel;

    private void RecoveryFilesGrid_Loaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NSX STARTUP] MW-R1 RecoveryFilesGrid Loaded");
        if (!IsRecoveryWorkspaceActive())
        {
            Debug.WriteLine("[NSX STARTUP] MW-R2 RecoveryFilesGrid skipped while workspace hidden");
            return;
        }

        HookRecoveryFilesScrollViewer();
        ScheduleVisibleThumbnailLoad();
    }

    private void RecoveryFilesGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (IsRecoveryWorkspaceActive())
            ScheduleVisibleThumbnailLoad();
    }

    private void RecoveryFilesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsRecoveryWorkspaceActive())
            ScheduleVisibleThumbnailLoad();
    }

    private void RecoveryFiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsRecoveryWorkspaceActive())
            ScheduleVisibleThumbnailLoad();
    }

    private void RecoveryFilesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) > 0.001
            || Math.Abs(e.ViewportHeightChange) > 0.001
            || Math.Abs(e.ExtentHeightChange) > 0.001)
            ScheduleVisibleThumbnailLoad();
    }

    private void HookRecoveryFilesScrollViewer()
    {
        ScrollViewer? scrollViewer = FindVisualChild<ScrollViewer>(RecoveryFilesGrid);
        if (ReferenceEquals(scrollViewer, _recoveryFilesScrollViewer))
            return;

        if (_recoveryFilesScrollViewer is not null)
            _recoveryFilesScrollViewer.ScrollChanged -= RecoveryFilesScrollViewer_ScrollChanged;

        _recoveryFilesScrollViewer = scrollViewer;
        if (_recoveryFilesScrollViewer is not null)
            _recoveryFilesScrollViewer.ScrollChanged += RecoveryFilesScrollViewer_ScrollChanged;
    }

    private void ScheduleVisibleThumbnailLoad()
    {
        if (!IsLoaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _thumbnailViewportTimer.Stop();
        _thumbnailViewportTimer.Start();
    }

    private void ThumbnailViewportTimer_Tick(object? sender, EventArgs e)
    {
        _thumbnailViewportTimer.Stop();
        RequestVisibleThumbnails();
    }

    private void RequestVisibleThumbnails()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var visibleItems = new List<RecoveryFileItem>();

        if (RecoveryThumbnailList.Visibility == Visibility.Visible && RecoveryThumbnailList.ActualHeight > 0)
        {
            double viewportBottom = RecoveryThumbnailList.ActualHeight;
            foreach (ListBoxItem container in FindVisualChildren<ListBoxItem>(RecoveryThumbnailList))
            {
                if (container.DataContext is not RecoveryFileItem item
                    || item.PreviewImage is not null
                    || (item.Category != "Fotoğraf" && item.Category != "Video")
                    || container.ActualHeight <= 0)
                    continue;

                Point topLeft;
                try
                {
                    topLeft = container.TranslatePoint(new Point(0, 0), RecoveryThumbnailList);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                double bottom = topLeft.Y + container.ActualHeight;
                if (bottom >= 0 && topLeft.Y <= viewportBottom)
                    visibleItems.Add(item);
            }
        }
        else if (RecoveryFilesGrid.ActualHeight > 0)
        {
            double viewportBottom = RecoveryFilesGrid.ActualHeight;
            foreach (DataGridRow row in FindVisualChildren<DataGridRow>(RecoveryFilesGrid))
            {
                if (row.Item is not RecoveryFileItem item
                    || item.PreviewImage is not null
                    || (item.Category != "Fotoğraf" && item.Category != "Video")
                    || row.ActualHeight <= 0)
                    continue;

                Point topLeft;
                try
                {
                    topLeft = row.TranslatePoint(new Point(0, 0), RecoveryFilesGrid);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                double bottom = topLeft.Y + row.ActualHeight;
                if (bottom >= 0 && topLeft.Y <= viewportBottom)
                    visibleItems.Add(item);
            }
        }

        if (visibleItems.Count > 0)
            vm.RequestVisiblePreviews(visibleItems.Distinct().ToList());
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(parent);

        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = count - 1; i >= 0; i--)
            {
                DependencyObject child = VisualTreeHelper.GetChild(current, i);
                if (child is T match)
                    return match;

                pending.Push(child);
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(parent);

        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = count - 1; i >= 0; i--)
            {
                DependencyObject child = VisualTreeHelper.GetChild(current, i);
                if (child is T match)
                    yield return match;

                pending.Push(child);
            }
        }
    }

    private void RecoveryFilesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(RecoveryFilesGrid, e.OriginalSource as DependencyObject) is not DataGridRow row)
        {
            RecoveryFilesGrid.SelectedItem = null;
            return;
        }

        RecoveryFilesGrid.SelectedItem = row.Item;
        row.Focus();
    }

    private void RecoveryFilesGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Menü boş alanda da açılabilir; toplu seçim komutu seçim durumuna göre dinamik değişir.
        if (sender is FrameworkElement owner)
            PrepareRecoveryBulkSelectionMenu(owner.ContextMenu);
        e.Handled = false;
    }

    private void RecoveryFilesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2)
            return;

        if (ItemsControl.ContainerFromElement(RecoveryFilesGrid, e.OriginalSource as DependencyObject) is not DataGridRow row ||
            row.Item is not RecoveryFileItem item)
            return;

        RecoveryFilesGrid.SelectedItem = item;
        if (DataContext is MainViewModel vm)
            vm.SelectedRecoveryFile = item;

        row.Focus();
        OpenSelectedPreview();
        e.Handled = true;
    }

    private void RecoveryThumbnail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement element ||
            element.DataContext is not RecoveryFileItem item)
            return;

        RecoveryFilesGrid.SelectedItem = item;
        if (DataContext is MainViewModel vm)
            vm.SelectedRecoveryFile = item;

        e.Handled = true;
        OpenSelectedPreview();
    }

    private void RecoveryThumbnailList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(RecoveryThumbnailList, e.OriginalSource as DependencyObject) is ListBoxItem item
            && item.DataContext is RecoveryFileItem file)
        {
            RecoveryThumbnailList.SelectedItem = file;
            if (DataContext is MainViewModel vm)
                vm.SelectedRecoveryFile = file;
            item.Focus();
        }
    }

    private void RecoveryThumbnailList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement owner)
            PrepareRecoveryBulkSelectionMenu(owner.ContextMenu);
        e.Handled = false;
    }

    private void RecoveryThumbnailList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecoveryThumbnailList.SelectedItem is not RecoveryFileItem item)
            return;

        if (DataContext is MainViewModel vm)
            vm.SelectedRecoveryFile = item;

        OpenSelectedPreview();
        e.Handled = true;
    }

    private void RecoveryThumbnailList_Loaded(object sender, RoutedEventArgs e)
    {
        HookRecoveryThumbnailScrollViewer();
        ScheduleVisibleThumbnailLoad();
    }

    private void RecoveryThumbnailList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        HookRecoveryThumbnailScrollViewer();
        ScheduleVisibleThumbnailLoad();
    }

    private void HookRecoveryThumbnailScrollViewer()
    {
        ScrollViewer? scrollViewer = FindVisualChild<ScrollViewer>(RecoveryThumbnailList);
        if (ReferenceEquals(scrollViewer, _recoveryThumbnailScrollViewer))
            return;

        if (_recoveryThumbnailScrollViewer is not null)
            _recoveryThumbnailScrollViewer.ScrollChanged -= RecoveryThumbnailScrollViewer_ScrollChanged;

        _recoveryThumbnailScrollViewer = scrollViewer;
        if (_recoveryThumbnailScrollViewer is not null)
            _recoveryThumbnailScrollViewer.ScrollChanged += RecoveryThumbnailScrollViewer_ScrollChanged;
    }

    private void RecoveryThumbnailScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
            ScheduleVisibleThumbnailLoad();
    }

    private void QuickScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        // Tek parça split-button davranışı: sağdaki 30 px yalnız tür menüsünü açar;
        // ana gövde ise hatırlanan türle seçili aygıtı anında tarar.
        Point point = Mouse.GetPosition(button);
        bool optionsArea = point.X >= Math.Max(0d, button.ActualWidth - 34d);
        if (optionsArea && button.ContextMenu is not null)
        {
            button.ContextMenu.DataContext = DataContext;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (DataContext is MainViewModel vm && vm.StartQuickScanCommand.CanExecute(null))
            vm.StartQuickScanCommand.Execute(null);

        e.Handled = true;
    }

    private void MainViewModel_ScanCompleted(object? sender, EventArgs e)
    {
        if (sender is not MainViewModel vm || _scanCompletionDialogOpen || _shutdownClosePending || !IsVisible)
            return;

        Dispatcher.BeginInvoke(() => ShowScanCompletedDialog(vm), DispatcherPriority.ContextIdle);
    }

    private void ShowScanCompletedDialog(MainViewModel vm)
    {
        if (_scanCompletionDialogOpen || _shutdownClosePending || !IsVisible || vm.IsBusy)
            return;

        _scanCompletionDialogOpen = true;
        try
        {
            var dialog = new ScanCompletedWindow(
                this,
                vm.FoundCount,
                vm.FoundBytesText,
                vm.ProgressElapsedText);

            _ = dialog.ShowDialog();
            if (dialog.RecoveryRequested && vm.RecoveryFiles.Count > 0)
            {
                if (!vm.RecoveryFiles.Any(item => item.IsChecked))
                    vm.RecoveryFiles[0].IsChecked = true;

                RecoveryFilesGrid.Focus();
                RecoveryFilesGrid.ScrollIntoView(vm.RecoveryFiles[0]);
            }
        }
        finally
        {
            _scanCompletionDialogOpen = false;
        }
    }

    private async void ReturnHomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_returnHomePending || DataContext is not MainViewModel vm)
            return;

        if (vm.IsScanRunning)
        {
            var prompt = new ScanExitPromptWindow(
                this,
                ScanExitPromptMode.StopScanAndReturnHome,
                vm.FoundCount,
                vm.FoundBytesText,
                vm.ProgressElapsedText);

            _ = prompt.ShowDialog();
            if (!prompt.Confirmed)
                return;

            _returnHomePending = true;
            try
            {
                if (vm.StopCommand.CanExecute(null))
                    vm.StopCommand.Execute(null);

                await vm.WaitForCurrentOperationAsync();

                if (vm.ReturnToDeviceSelectionCommand.CanExecute(null))
                    vm.ReturnToDeviceSelectionCommand.Execute(null);
            }
            finally
            {
                _returnHomePending = false;
            }

            return;
        }

        if (vm.RecoveryFiles.Count > 0)
        {
            var prompt = new ScanExitPromptWindow(
                this,
                ScanExitPromptMode.ResetResultsAndReturnHome,
                vm.FoundCount,
                vm.FoundBytesText,
                vm.ProgressElapsedText);

            _ = prompt.ShowDialog();
            if (!prompt.Confirmed)
                return;
        }

        if (vm.ReturnToDeviceSelectionCommand.CanExecute(null))
            vm.ReturnToDeviceSelectionCommand.Execute(null);
    }

    private void StopScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsScanRunning)
            return;

        var prompt = new ScanExitPromptWindow(
            this,
            ScanExitPromptMode.StopScan,
            vm.FoundCount,
            vm.FoundBytesText,
            vm.ProgressElapsedText);

        _ = prompt.ShowDialog();
        if (prompt.Confirmed && vm.StopCommand.CanExecute(null))
            vm.StopCommand.Execute(null);
    }

    private void DeepScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void FolderScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        if (DataContext is MainViewModel vm)
            vm.ActivateFolderScanNavigation();

        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void PreviewFile_Click(object sender, RoutedEventArgs e) => OpenSelectedPreview();

    private void RecoverSingleFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.RecoverSelectedCommand.CanExecute(null))
            vm.RecoverSelectedCommand.Execute(null);
    }

    private void MarkFileForRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedRecoveryFile is not null)
            vm.SelectedRecoveryFile.IsChecked = true;
    }

    private void SelectAllVisibleFiles_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (vm.AreAllVisibleSelected)
        {
            if (vm.ClearRecoverySelectionCommand.CanExecute(null))
                vm.ClearRecoverySelectionCommand.Execute(null);
            return;
        }

        vm.AreAllVisibleSelected = true;
    }

    private void PrepareRecoveryBulkSelectionMenu(ContextMenu? menu)
    {
        if (menu is null || DataContext is not MainViewModel vm)
            return;

        MenuItem? bulkItem = menu.Items.OfType<MenuItem>().FirstOrDefault();
        if (bulkItem is null)
            return;

        bool clearAll = vm.AreAllVisibleSelected;
        bulkItem.Header = LocalizationService.Current.Translate(clearAll
            ? "Tüm Seçileni İptal Et"
            : "Tümünü seç");

        if (bulkItem.Icon is TextBlock icon)
        {
            icon.Text = clearAll ? "\uE711" : "\uE762";
            icon.Foreground = clearAll
                ? new SolidColorBrush(Color.FromRgb(176, 63, 75))
                : new SolidColorBrush(Color.FromRgb(11, 87, 201));
        }
    }

    private void ResultNavigationTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not RadioButton tab)
            return;

        string mode = string.Equals(tab.Tag?.ToString(), "Path", StringComparison.OrdinalIgnoreCase)
            ? "Path"
            : "Type";

        if (vm.ShowResultNavigationCommand.CanExecute(mode))
            vm.ShowResultNavigationCommand.Execute(mode);
    }

    private void ClearRecoverySelection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsBusy)
            return;

        foreach (RecoveryFileItem item in vm.RecoveryFiles)
            item.IsChecked = false;
    }

    private void OpenSelectedPreview()
    {
        if (_activePreviewWindow is { IsVisible: true })
        {
            if (_activePreviewWindow.WindowState == WindowState.Minimized)
                _activePreviewWindow.WindowState = WindowState.Normal;

            _activePreviewWindow.Activate();
            _activePreviewWindow.Focus();
            return;
        }

        if (DataContext is not MainViewModel vm || vm.SelectedDevice is null || vm.SelectedRecoveryFile is null)
            return;

        StorageDeviceInfo previewDevice = vm.SelectedDevice;
        RecoveryFileItem selectedItem = vm.SelectedRecoveryFile;

        try
        {
            // View canlı olarak değişebildiği için önizleme penceresine sabit bir anlık görüntü verilir.
            List<RecoveryFileItem> previewItems = vm.RecoveryFilesView.Cast<RecoveryFileItem>().ToList();
            if (previewItems.Count == 0)
                previewItems = [selectedItem];

            int initialIndex = previewItems.FindIndex(item => ReferenceEquals(item, selectedItem));
            if (initialIndex < 0)
            {
                previewItems.Insert(0, selectedItem);
                initialIndex = 0;
            }

            var previewWindow = new RecoveryPreviewWindow(previewDevice, previewItems, initialIndex, vm)
            {
                Owner = this
            };
            _activePreviewWindow = previewWindow;

            previewWindow.Closed += async (_, _) =>
            {
                if (ReferenceEquals(_activePreviewWindow, previewWindow))
                    _activePreviewWindow = null;

                if (!previewWindow.RecoverRequested || previewWindow.RequestedRecoveryItem is null)
                    return;

                await vm.RecoverPreviewItemAsync(previewDevice, previewWindow.RequestedRecoveryItem);
            };

            previewWindow.Show();
        }
        catch (Exception ex)
        {
            AppLog.Error("Önizleme penceresi açılamadı.", ex);
            MessageBox.Show(
                this,
                string.Format(L("Önizleme penceresi açılamadı. Ana program çalışmaya devam edecek.\n\n{0}"), L(ex.Message)),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ToggleMaximize()
    {
        if (_workAreaMaximized)
            ExitWorkAreaMaximized();
        else
        {
            _restoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            EnterWorkAreaMaximized();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowFinalClose)
        {
            base.OnClosing(e);
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            if (!_shutdownClosePending && vm.HasSavableProject)
            {
                string message = L(vm.IsScanRunning
                    ? "Tarama devam ediyor.\n\nMevcut taramayı NSX projesi olarak kaydetmek ister misiniz?\nKaydederseniz bu .nsx dosyasını daha sonra açıp kaldığınız yerden devam edebilirsiniz."
                    : "Mevcut tarama sonuçlarını NSX projesi olarak kaydetmek ister misiniz?\nKaydederseniz bu .nsx dosyasını daha sonra yeniden açabilirsiniz.");

                var savePrompt = new SessionSavePromptWindow(
                    L("Oturum Kaydet"),
                    message,
                    L("Evet"),
                    L("Hayır"),
                    L("İptal"),
                    L(vm.IsScanRunning ? "Tarama devam ediyor" : "Tarama tamamlandı"),
                    L("Tarama Oturumunu Kaydet - NSX Veri Kurtarma Projesi"),
                    vm.IsScanRunning)
                {
                    Owner = this
                };

                _ = savePrompt.ShowDialog();
                SessionSavePromptChoice saveChoice = savePrompt.Choice;

                if (saveChoice == SessionSavePromptChoice.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (saveChoice == SessionSavePromptChoice.Save && !SaveProjectWithDialog(vm))
                {
                    e.Cancel = true;
                    return;
                }
            }

            vm.RequestShutdown();

            if (vm.IsBusy)
            {
                e.Cancel = true;
                if (!_shutdownClosePending)
                {
                    _shutdownClosePending = true;
                    _ = CloseAfterOperationStopsAsync(vm);
                }
                return;
            }
        }

        base.OnClosing(e);
    }

    private bool SaveProjectWithDialog(MainViewModel vm)
    {
        string defaultFileName = !string.IsNullOrWhiteSpace(vm.CurrentProjectPath)
            ? Path.GetFileName(vm.CurrentProjectPath)
            : $"NSX_Tarama_{DateTime.Now:yyyyMMdd_HHmm}.nsx";

        var dialog = new SaveFileDialog
        {
            Title = L("Tarama Oturumunu Kaydet - NSX Veri Kurtarma Projesi"),
            Filter = L("NSX Veri Kurtarma Projesi (*.nsx)|*.nsx"),
            DefaultExt = ".nsx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = defaultFileName
        };

        if (!string.IsNullOrWhiteSpace(vm.CurrentProjectPath))
        {
            string? currentDirectory = Path.GetDirectoryName(vm.CurrentProjectPath);
            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
                dialog.InitialDirectory = currentDirectory;
        }

        if (dialog.ShowDialog(this) != true)
            return false;

        if (vm.SaveProject(dialog.FileName))
            return true;

        MessageBox.Show(
            this,
            L("NSX kurtarma projesi kaydedilemedi. Program kapatılmadı; farklı bir konum seçerek tekrar deneyebilirsiniz."),
            L("Oturum Kaydet"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private async Task CloseAfterOperationStopsAsync(MainViewModel vm)
    {
        try
        {
            await vm.WaitForCurrentOperationAsync();
        }
        catch
        {
        }

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _allowFinalClose = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastHeader
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastVolume
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public uint UnitMask;
        public ushort Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectInt rcMonitor;
        public RectInt rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref uint pvAttribute,
        int cbAttribute);
}
