using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using LibVLCSharp.Shared;
using VlcLibVLC = LibVLCSharp.Shared.LibVLC;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using NSXVeriKurtarmaPro.Infrastructure;
using NSXVeriKurtarmaPro.Models;
using NSXVeriKurtarmaPro.Services;
using NSXVeriKurtarmaPro.ViewModels;

namespace NSXVeriKurtarmaPro;

public partial class RecoveryPreviewWindow : Window
{
    private const double MaximumPreviewSeconds = 10d;
    private const long MaximumVideoPreviewReadBytes = 64L * 1024 * 1024;
    private const int StripWindowSize = 16;
    private const double StripCardWidth = 84d;
    private const double StripCardHeight = 86d;
    private const double StripCardStride = 90d;
    private const double PrimaryPreviewCornerRadius = 24d;
    private const double PhotoMinimumZoom = 0.005d;
    private const double PhotoMaximumZoom = 8.00d;
    private const double PhotoWheelZoomFactor = 1.18d;
    private const int StripWheelWindowShift = 4;

    private readonly StorageDeviceInfo _device;
    private readonly IReadOnlyList<RecoveryFileItem> _items;
    private readonly RecoveryService _recoveryService = new();
    private readonly MediaRepairService _mediaRepairService = new();
    private readonly MainViewModel? _mainViewModel;
    private readonly DispatcherTimer _positionTimer;

    private VlcLibVLC? _libVlc;
    private VlcMediaPlayer? _videoPlayer;
    private VlcMedia? _activeVideoMedia;
    private StreamMediaInput? _activeVideoInput;
    private Stream? _activeVideoStream;
    private bool _videoViewReady;
    private bool _videoPlaybackNeedsInitialization;
    private string? _pendingVideoPath;
    private RecoveryFileItem? _pendingVideoItem;
    private int _videoSnapshotGeneration;

    private CancellationTokenSource _previewCts = new();
    private string? _previewFilePath;
    private bool _ownsPreviewFile;
    private bool _isPlaying;
    private bool _updatingSlider;
    private double _previewLimitSeconds = MaximumPreviewSeconds;
    private int _currentIndex;
    private bool _isRepairing;
    private bool _isAnalyzingReference;
    private ReferenceVideoProfile? _referenceVideoProfile;
    private string? _referenceVideoPath;
    private bool _workAreaMaximized;
    private Rect _restoreBounds;
    private double _photoZoom = 1d;
    private double _photoFitZoom = 1d;
    private double _photoPanX;
    private double _photoPanY;
    private bool _photoIsFit = true;
    private bool _photoIsDragging;
    private bool _photoDragMoved;
    private Point _photoDragStart;
    private double _photoPanStartX;
    private double _photoPanStartY;
    private readonly DispatcherTimer _photoSingleClickTimer;
    private int _pendingPhotoRepairIndex = -1;
    private int _stripStartIndex = -1;

    public bool RecoverRequested { get; private set; }
    public RecoveryFileItem? RequestedRecoveryItem { get; private set; }

    private RecoveryFileItem CurrentItem => _items[_currentIndex];

    private static string L(string source) => LocalizationService.Current.Translate(source);

    private int GetActiveStripWindowSize()
    {
        double viewport = PreviewStripScrollViewer?.ViewportWidth ?? 0d;
        if (viewport <= 0d)
            viewport = PreviewStripScrollViewer?.ActualWidth ?? 0d;

        if (viewport <= 0d)
            return StripWindowSize;

        // Kart + boşluk stride değerine göre pencere büyüdükçe görünür thumb sayısını artır.
        int calculated = (int)Math.Floor((viewport + 6d) / StripCardStride);
        return Math.Max(StripWindowSize, calculated);
    }

    public RecoveryPreviewWindow(
        StorageDeviceInfo device,
        IReadOnlyList<RecoveryFileItem> items,
        int initialIndex,
        MainViewModel? mainViewModel = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _items = items is { Count: > 0 }
            ? items
            : throw new ArgumentException(L("Önizlenecek dosya bulunamadı."), nameof(items));
        _currentIndex = Math.Clamp(initialIndex, 0, _items.Count - 1);
        _mainViewModel = mainViewModel;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _positionTimer.Tick += PositionTimer_Tick;
        _photoSingleClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _photoSingleClickTimer.Tick += PhotoSingleClickTimer_Tick;

        // WPF XAML yüklenirken ValueChanged/SelectionChanged gibi eventler tetiklenebilir.
        // Bu nedenle pencerenin kullandığı tüm zorunlu alanlar InitializeComponent'ten önce hazırlanır.
        InitializeComponent();
        InitializeVideoEngine();

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(8),
            GlassFrameThickness = new Thickness(0),
            // Sistem Merkezi ile aynı dış köşe geometrisi. VLC/VideoView uyumluluğunu
            // korumak için AllowsTransparency açmadan WindowChrome üzerinden uygulanır.
            CornerRadius = new CornerRadius(15),
            UseAeroCaptionButtons = false
        });

        if (_mainViewModel is not null)
            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

        Loaded += RecoveryPreviewWindow_Loaded;
        Closed += RecoveryPreviewWindow_Closed;
    }

    public RecoveryPreviewWindow(
        StorageDeviceInfo device,
        RecoveryFileItem item,
        MainViewModel? mainViewModel = null)
        : this(device, [item], 0, mainViewModel)
    {
    }

    private async void RecoveryPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreSavedPreviewWindowLayout();
        ConstrainToWorkingArea();
        UpdateMediaHostClip();

        try
        {
            RefreshStrip(centerCurrent: true);
            await SafeLoadCurrentItemAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Önizleme penceresi hazırlanamadı.", ex);
            ShowUnsupported("Önizleme penceresi hazırlanamadı. Ana program çalışmaya devam ediyor.");
        }
    }

    private async Task SafeLoadCurrentItemAsync()
    {
        try
        {
            await LoadCurrentItemAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"Dosya önizlemesi açılamadı: {CurrentItem.FileName}", ex);
            ShowUnsupported("Dosya önizleme için hazırlanamadı. Dosyayı yine de kurtarmayı deneyebilirsiniz.");
        }
    }

    private static bool CanRepairItem(RecoveryFileItem item)
        => item.Category == "Fotoğraf" ||
           (item.Category == "Video" && MediaRepairService.CanRepairVideo(item.Extension));

    private static bool IsRepairNeededState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return false;

        return state.Contains("Zayıf", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("Kısmi", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("Parçalı", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("Yeniden", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("Video Akışı", StringComparison.OrdinalIgnoreCase) ||
               state.Contains("Video Parçası", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldUseRepairLayout(RecoveryFileItem item)
        => CanRepairItem(item) && !item.IsRepaired && IsRepairNeededState(item.RecoveryState);

    private void UpdatePreviewLayout(RecoveryFileItem item)
    {
        bool useRepairLayout = ShouldUseRepairLayout(item);

        if (useRepairLayout)
        {
            PreviewSideColumn.Width = new GridLength(286);
            PreviewSidePanel.Visibility = Visibility.Visible;
            FileInfoCard.Visibility = Visibility.Collapsed;
            RepairCard.Margin = new Thickness(0);
            Grid.SetColumnSpan(PrimaryPreviewBorder, 1);
            PrimaryPreviewBorder.Margin = new Thickness(0, 0, 16, 0);
        }
        else
        {
            PreviewSideColumn.Width = new GridLength(0);
            PreviewSidePanel.Visibility = Visibility.Collapsed;
            FileInfoCard.Visibility = Visibility.Visible;
            RepairCard.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetColumnSpan(PrimaryPreviewBorder, 2);
            PrimaryPreviewBorder.Margin = new Thickness(0);
        }
    }

    private async Task LoadCurrentItemAsync()
    {
        RecoveryFileItem item = CurrentItem;
        DataContext = item;
        PreviewCounterText.Text = $"{_currentIndex + 1} / {_items.Count}";
        StripSelectionText.Text = L($"Seçim {_currentIndex + 1} / {_items.Count}");
        InitializeTechnicalInfo(item);
        UpdatePreviewLayout(item);
        PreviewLimitBadge.Visibility = item.Category == "Video" ? Visibility.Visible : Visibility.Collapsed;
        bool canRepair = CanRepairItem(item);
        RepairButton.Visibility = canRepair ? Visibility.Visible : Visibility.Collapsed;
        RepairButton.IsEnabled = canRepair && !_isRepairing && !item.IsRepaired;

        RepairHintText.Text = L(item.Category switch
        {
            "Fotoğraf" => "Büyük resmin üzerine tek tıklayın. Görüntü yeniden çözülür, temiz dosya yapısına kodlanır ve tekrar doğrulanır.",
            "Video" when MediaRepairService.CanRepairVideo(item.Extension) =>
                "Video yapısı analiz edilir. Desteklenen formatlarda kapsayıcı ve akış bilgileri güvenli biçimde yeniden düzenlenir.",
            "Video" => "Video önizlenebilir; ancak bu format için güvenilir yapısal onarım doğrulanamadığından dosya değiştirilmez.",
            _ => "Bu dosya türü için onarma desteği bulunmuyor."
        });
        UpdateReferenceVideoPanel(item);

        if (item.IsRepaired)
        {
            SetRepairStatus(
                item.RepairMessage ?? "Dosya daha önce onarıldı. Bundan sonraki kurtarma otomatik olarak onarılmış sürümü kullanacak.",
                false);
        }
        else if (canRepair)
        {
            SetRepairStatus(
                "Onarmak için büyük resim/video alanına tek tıklayın. Başarılı onarım kalıcı olarak bu oturumdaki dosyaya bağlanır.",
                false,
                neutral: true);
        }
        else
        {
            SetRepairStatus(
                "Video önizlenebilir; ancak bu format için güvenilir onarım yöntemi doğrulanamadığından onarım uygulanmaz.",
                false,
                neutral: true);
        }

        UpdateRecoverAvailability();
        UpdateNavigationState();

        CancelPreviewLoad();
        DeletePreviewCacheFile();
        ResetPreviewPanels();

        if (item.Category == "Belge")
        {
            ShowUnsupported("Bu dosya türü için görsel önizleme bulunmuyor.");
            return;
        }

        if (item.IsRepaired && !string.IsNullOrWhiteSpace(item.RepairedFilePath) && File.Exists(item.RepairedFilePath))
        {
            _previewFilePath = item.RepairedFilePath;
            _ownsPreviewFile = false;
            ShowCurrentMedia(item, item.RepairedFilePath);
            return;
        }

        // Normal video dosyalarını önizleme için geçici diske bütünüyle kopyalama.
        // LibVLC yalnız ihtiyaç duyduğu aralıkları salt-okunur stream üzerinden ister;
        // stream toplam okuma bütçesiyle sınırlandığı için GB boyutlu dosyalar hızlı açılır.
        if (item.Category == "Video" && item.TransformKind == RecoveryTransformKind.None)
        {
            ShowVideo(item, path: null);
            return;
        }

        try
        {
            string previewDirectory = Path.Combine(Path.GetTempPath(), "NSXVeriKurtarmaPro", "PreviewCache");
            CancellationToken token = _previewCts.Token;
            string? path = await Task.Run(
                () => item.Category == "Video"
                    ? _recoveryService.CreateTemporaryVideoPreviewFile(
                        _device,
                        item,
                        previewDirectory,
                        MaximumVideoPreviewReadBytes,
                        token)
                    : _recoveryService.CreateTemporaryPreviewFile(_device, item, previewDirectory, token),
                token);

            if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(path))
                return;

            _previewFilePath = path;
            _ownsPreviewFile = true;
            ShowCurrentMedia(item, path);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            ShowUnsupported("Dosya verisi önizleme için açılamadı.");
        }
    }

    private void ShowCurrentMedia(RecoveryFileItem item, string path)
    {
        if (item.Category == "Fotoğraf")
            ShowPhoto(path);
        else if (item.Category == "Video")
            ShowVideo(item, path);
        else
            ShowUnsupported("Bu dosya türü için görsel önizleme bulunmuyor.");
    }

    private void ResetPreviewPanels()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        PhotoPanel.Visibility = Visibility.Collapsed;
        VideoPanel.Visibility = Visibility.Collapsed;
        UnsupportedPanel.Visibility = Visibility.Collapsed;
        RepairLoadingOverlay.Visibility = Visibility.Collapsed;
        PreviewFinishedOverlay.Visibility = Visibility.Collapsed;
        PositionSlider.Maximum = MaximumPreviewSeconds;
        PositionSlider.Value = 0;
        CurrentTimeText.Text = "00:00";
        LimitTimeText.Text = "00:10";
        _previewLimitSeconds = MaximumPreviewSeconds;

        try
        {
            PhotoPreview.Source = null;
            ResetPhotoTransformState();
            _videoPlayer?.Stop();
            DisposeActiveVideoSource();
            _videoPlaybackNeedsInitialization = false;
            _pendingVideoPath = null;
            _pendingVideoItem = null;
            _videoSnapshotGeneration++;
        }
        catch
        {
        }

        _positionTimer.Stop();
        _isPlaying = false;
        SyncPlayPauseGlyph();
    }

    private void ShowPhoto(string path)
    {
        try
        {
            (uint width, uint height) = GlobalImageCodec.GetDimensions(path);
            ImageSource previewSource = GlobalImageCodec.LoadPreview(path, 1800);
            PhotoPreview.Source = previewSource;
            // WPF Stretch=Uniform resmi viewport'a kendisi sığdırır. Burada piksel Width/Height
            // vermiyoruz; aksi halde hasarlı metadata/DPI bilgisi fotoğrafı küçük veya kırpılmış gösterebilir.

            RecoveryFileItem item = CurrentItem;
            SetResolutionInfo(item, width, height);
            if (item.PreviewImage is null)
            {
                try
                {
                    item.PreviewImage = GlobalImageCodec.LoadPreview(path, 240);
                    RefreshStrip(centerCurrent: false);
                }
                catch
                {
                }
            }

            LoadingPanel.Visibility = Visibility.Collapsed;
            PhotoPanel.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitPhotoToViewport));
        }
        catch
        {
            if (CurrentItem.Category == "Fotoğraf")
                TechnicalInfoValueText.Text = "—";
            ShowUnsupported("Resim verisi önizleme için çözümlenemedi.");
        }
    }


    private void ResetPhotoTransformState()
    {
        _photoSingleClickTimer.Stop();
        _pendingPhotoRepairIndex = -1;
        _photoZoom = 1d;
        _photoFitZoom = 1d;
        _photoPanX = 0d;
        _photoPanY = 0d;
        _photoIsFit = true;
        _photoIsDragging = false;
        _photoDragMoved = false;

        if (PhotoViewport is not null && PhotoViewport.IsMouseCaptured)
            PhotoViewport.ReleaseMouseCapture();

        if (PhotoScaleTransform is not null)
        {
            PhotoScaleTransform.ScaleX = 1d;
            PhotoScaleTransform.ScaleY = 1d;
        }

        if (PhotoTranslateTransform is not null)
        {
            PhotoTranslateTransform.X = 0d;
            PhotoTranslateTransform.Y = 0d;
        }

        if (PhotoViewport is not null)
            PhotoViewport.Cursor = Cursors.Arrow;
    }


    private void UpdatePhotoFitZoom()
    {
        // PhotoPreview tüm viewport'u kaplar ve Stretch=Uniform kullanır.
        // Bu yüzden doğal "sığdır" ölçeği daima 1.0'dır; yatay/dikey resim tamamen görünür.
        _photoFitZoom = 1d;
    }

    private void FitPhotoToViewport()
    {
        if (PhotoPreview.Source is null)
            return;

        UpdatePhotoFitZoom();
        _photoZoom = _photoFitZoom;
        _photoPanX = 0d;
        _photoPanY = 0d;
        _photoIsFit = true;
        ApplyPhotoTransform();
    }


    private void SetPhotoZoom(double requestedZoom, Point anchor)
    {
        if (PhotoPreview.Source is null || PhotoViewport.ActualWidth <= 0d || PhotoViewport.ActualHeight <= 0d)
            return;

        UpdatePhotoFitZoom();

        double oldZoom = Math.Max(PhotoMinimumZoom, _photoZoom);
        double newZoom = Math.Clamp(requestedZoom, PhotoMinimumZoom, PhotoMaximumZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.0001d)
            return;

        var viewportCenter = new Point(PhotoViewport.ActualWidth / 2d, PhotoViewport.ActualHeight / 2d);
        double ratio = newZoom / oldZoom;

        _photoPanX = anchor.X - viewportCenter.X - ((anchor.X - viewportCenter.X - _photoPanX) * ratio);
        _photoPanY = anchor.Y - viewportCenter.Y - ((anchor.Y - viewportCenter.Y - _photoPanY) * ratio);
        _photoZoom = newZoom;
        _photoIsFit = Math.Abs(_photoZoom - _photoFitZoom) <= 0.015d;

        ClampPhotoPan();
        ApplyPhotoTransform();
    }

    private void ClampPhotoPan()
    {
        if (PhotoPreview.Source is null || PhotoViewport.ActualWidth <= 0d || PhotoViewport.ActualHeight <= 0d)
        {
            _photoPanX = 0d;
            _photoPanY = 0d;
            return;
        }

        // Image kontrolü viewport'a Stretch edilir; 1.0 ölçek tam sığdırılmış haldir.
        // Yalnızca 1.0 üstündeki zoom'da pan alanı açılır.
        double scaledWidth = PhotoViewport.ActualWidth * _photoZoom;
        double scaledHeight = PhotoViewport.ActualHeight * _photoZoom;
        double maxPanX = Math.Max(0d, (scaledWidth - PhotoViewport.ActualWidth) / 2d);
        double maxPanY = Math.Max(0d, (scaledHeight - PhotoViewport.ActualHeight) / 2d);

        _photoPanX = Math.Clamp(_photoPanX, -maxPanX, maxPanX);
        _photoPanY = Math.Clamp(_photoPanY, -maxPanY, maxPanY);
    }

    private void ApplyPhotoTransform()
    {
        PhotoScaleTransform.ScaleX = _photoZoom;
        PhotoScaleTransform.ScaleY = _photoZoom;
        PhotoTranslateTransform.X = _photoPanX;
        PhotoTranslateTransform.Y = _photoPanY;
        PhotoViewport.Cursor = _photoIsDragging ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void PhotoViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isRepairing || CurrentItem.Category != "Fotoğraf" || PhotoPreview.Source is null)
            return;

        _photoSingleClickTimer.Stop();
        _pendingPhotoRepairIndex = -1;

        double factor = e.Delta > 0 ? PhotoWheelZoomFactor : 1d / PhotoWheelZoomFactor;
        SetPhotoZoom(_photoZoom * factor, e.GetPosition(PhotoViewport));
        e.Handled = true;
    }

    private void PhotoViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isRepairing || CurrentItem.Category != "Fotoğraf" || PhotoPreview.Source is null)
            return;

        _photoSingleClickTimer.Stop();
        _pendingPhotoRepairIndex = -1;


        _photoIsDragging = true;
        _photoDragMoved = false;
        _photoDragStart = e.GetPosition(PhotoViewport);
        _photoPanStartX = _photoPanX;
        _photoPanStartY = _photoPanY;
        PhotoViewport.CaptureMouse();
        ApplyPhotoTransform();
        e.Handled = true;
    }

    private void PhotoViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_photoIsDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(PhotoViewport);
        Vector delta = current - _photoDragStart;

        if (!_photoDragMoved &&
            (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance))
        {
            _photoDragMoved = true;
        }

        if (!_photoDragMoved)
            return;

        _photoPanX = _photoPanStartX + delta.X;
        _photoPanY = _photoPanStartY + delta.Y;
        _photoIsFit = false;
        ClampPhotoPan();
        ApplyPhotoTransform();
        e.Handled = true;
    }

    private void PhotoViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_photoIsDragging)
            return;

        bool wasDrag = _photoDragMoved;
        _photoIsDragging = false;
        _photoDragMoved = false;

        if (PhotoViewport.IsMouseCaptured)
            PhotoViewport.ReleaseMouseCapture();

        ApplyPhotoTransform();
        e.Handled = true;

        if (!wasDrag)
        {
            _pendingPhotoRepairIndex = _currentIndex;
            _photoSingleClickTimer.Stop();
            _photoSingleClickTimer.Start();
        }
    }

    private void PhotoViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_photoIsDragging && e.LeftButton == MouseButtonState.Released)
        {
            _photoIsDragging = false;
            _photoDragMoved = false;
            if (PhotoViewport.IsMouseCaptured)
                PhotoViewport.ReleaseMouseCapture();
        }

        ApplyPhotoTransform();
    }

    private void MediaHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMediaHostClip();
    }

    private void UpdateMediaHostClip()
    {
        // Border CornerRadius tek başına içeriği gerçek anlamda kırpmaz. Özellikle
        // yükleniyor / önizleme yok / video gibi tam yüzeyi dolduran panellerde
        // köşeler kare görünüyordu. Ana medya yüzeyine aynı 24 px yuvarlak geometriyi uygula.
        double clipWidth = Math.Max(0d, MediaHost.ActualWidth);
        double clipHeight = Math.Max(0d, MediaHost.ActualHeight);
        MediaHost.Clip = clipWidth > 0d && clipHeight > 0d
            ? new RectangleGeometry(new Rect(0d, 0d, clipWidth, clipHeight), PrimaryPreviewCornerRadius, PrimaryPreviewCornerRadius)
            : null;
    }

    private void PhotoViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ClipToBounds dikdörtgen kırpma yapar; zoom sırasında köşeler bu yüzden kareleşiyordu.
        // Viewport'u gerçek yuvarlak geometri ile kırp. RenderTransform ne kadar büyürse büyüsün
        // görüntü 24 px yuvarlak köşelerin dışına çıkamaz.
        double clipWidth = Math.Max(0d, PhotoViewport.ActualWidth);
        double clipHeight = Math.Max(0d, PhotoViewport.ActualHeight);
        PhotoViewport.Clip = clipWidth > 0d && clipHeight > 0d
            ? new RectangleGeometry(new Rect(0d, 0d, clipWidth, clipHeight), PrimaryPreviewCornerRadius, PrimaryPreviewCornerRadius)
            : null;

        if (PhotoPreview.Source is null)
            return;

        UpdatePhotoFitZoom();
        if (_photoIsFit)
        {
            _photoZoom = _photoFitZoom;
            _photoPanX = 0d;
            _photoPanY = 0d;
        }
        else
        {
            ClampPhotoPan();
        }

        ApplyPhotoTransform();
    }

    private async void PhotoSingleClickTimer_Tick(object? sender, EventArgs e)
    {
        _photoSingleClickTimer.Stop();
        int requestedIndex = _pendingPhotoRepairIndex;
        _pendingPhotoRepairIndex = -1;

        if (_isRepairing || requestedIndex != _currentIndex || CurrentItem.Category != "Fotoğraf")
            return;

        await RepairCurrentItemAsync();
    }


    private void InitializeTechnicalInfo(RecoveryFileItem item)
    {
        if (item.Category is "Fotoğraf" or "Video")
        {
            TechnicalInfoLabelText.Text = L("Çözünürlük");
            TechnicalInfoValueText.Text = item.HasResolution ? item.ResolutionText : L("Analiz ediliyor…");
            return;
        }

        TechnicalInfoLabelText.Text = L("Kaynak");
        TechnicalInfoValueText.Text = string.IsNullOrWhiteSpace(item.LocationText) ? "—" : L(item.LocationText);
    }

    private void SetResolutionInfo(RecoveryFileItem item, uint width, uint height)
    {
        if (width == 0 || height == 0)
            return;

        item.SetResolution(width, height);
        if (!ReferenceEquals(item, CurrentItem))
            return;

        TechnicalInfoLabelText.Text = L("Çözünürlük");
        TechnicalInfoValueText.Text = item.ResolutionText;
    }

    private async Task UpdateVideoResolutionAsync(RecoveryFileItem item, string path)
    {
        try
        {
            ReferenceVideoProfile profile = await Task.Run(() => ReferenceVideoProfileService.Analyze(path));
            if (!ReferenceEquals(item, CurrentItem))
                return;

            if (profile.Width > 0 && profile.Height > 0)
                SetResolutionInfo(item, (uint)profile.Width, (uint)profile.Height);
        }
        catch
        {
            if (ReferenceEquals(item, CurrentItem) && TechnicalInfoValueText.Text == L("Analiz ediliyor…"))
                TechnicalInfoValueText.Text = "—";
            // Decoder playback may still expose geometry for damaged/unsupported containers.
        }
    }

    private void TryUpdateResolutionFromDecoder(RecoveryFileItem item)
    {
        if (_videoPlayer is null || !ReferenceEquals(item, CurrentItem))
            return;

        try
        {
            foreach (System.Reflection.MethodInfo method in _videoPlayer.GetType().GetMethods())
            {
                if (!string.Equals(method.Name, "Size", StringComparison.Ordinal) || method.GetParameters().Length != 3)
                    continue;

                System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                Type firstType = parameters[0].ParameterType;
                object index = firstType == typeof(uint) ? 0u : 0;
                object[] args = [index, 0u, 0u];
                method.Invoke(_videoPlayer, args);

                uint width = Convert.ToUInt32(args[1], System.Globalization.CultureInfo.InvariantCulture);
                uint height = Convert.ToUInt32(args[2], System.Globalization.CultureInfo.InvariantCulture);
                if (width > 0 && height > 0)
                {
                    SetResolutionInfo(item, width, height);
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void InitializeVideoEngine()
    {
        try
        {
            Core.Initialize();
            _libVlc = new VlcLibVLC(
                "--no-video-title-show",
                "--no-osd",
                "--quiet",
                "--file-caching=250",
                "--avcodec-hw=any");
            _videoPlayer = new VlcMediaPlayer(_libVlc);
            _videoPlayer.Playing += VideoEngine_Playing;
            _videoPlayer.EndReached += VideoEngine_EndReached;
            _videoPlayer.EncounteredError += VideoEngine_EncounteredError;
            VideoView.Loaded += VideoView_Loaded;
        }
        catch (Exception ex)
        {
            AppLog.Error("Global video önizleme motoru başlatılamadı.", ex);
            DisposeVideoEngine();
        }
    }

    private void VideoView_Loaded(object sender, RoutedEventArgs e)
    {
        _videoViewReady = true;
        if (_videoPlayer is not null)
            VideoView.MediaPlayer = _videoPlayer;

        if (_pendingVideoItem is not null)
            StartVideoPlayback(_pendingVideoItem, _pendingVideoPath);
    }

    private void ShowVideo(RecoveryFileItem item, string? path)
    {
        if (_libVlc is null || _videoPlayer is null)
        {
            ShowUnsupported("Global video önizleme motoru kullanılamıyor. Dosyayı yine de kurtarabilirsiniz.");
            return;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        UnsupportedPanel.Visibility = Visibility.Collapsed;
        VideoPanel.Visibility = Visibility.Visible;
        _pendingVideoPath = path;
        _pendingVideoItem = item;
        if (!string.IsNullOrWhiteSpace(path))
            _ = UpdateVideoResolutionAsync(item, path);

        if (_videoViewReady)
            StartVideoPlayback(item, path);
    }

    private void StartVideoPlayback(RecoveryFileItem item, string? path)
    {
        if (_libVlc is null || _videoPlayer is null || !_videoViewReady)
            return;

        try
        {
            _positionTimer.Stop();
            _videoPlayer.Stop();
            DisposeActiveVideoSource();

            if (!string.IsNullOrWhiteSpace(path))
            {
                _activeVideoMedia = new VlcMedia(_libVlc, path, FromType.FromPath);
            }
            else
            {
                _activeVideoStream = new RecoveryItemReadStream(
                    _device,
                    item,
                    MaximumVideoPreviewReadBytes);
                _activeVideoInput = new StreamMediaInput(_activeVideoStream);
                _activeVideoMedia = new VlcMedia(_libVlc, _activeVideoInput);
            }

            _activeVideoMedia.AddOption(":no-sub-autodetect-file");
            _activeVideoMedia.AddOption(":file-caching=180");
            _activeVideoMedia.AddOption($":stop-time={MaximumPreviewSeconds:0}");
            _videoPlaybackNeedsInitialization = true;
            _videoPlayer.Play(_activeVideoMedia);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Video önizlemesi açılamadı: {CurrentItem.FileName}", ex);
            DisposeActiveVideoSource();
            VideoPanel.Visibility = Visibility.Collapsed;
            ShowUnsupported("Video verisi global medya motoru tarafından çözümlenemedi.");
        }
    }

    private void VideoEngine_Playing(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_videoPlayer is null || CurrentItem.Category != "Video")
                return;

            TryUpdateResolutionFromDecoder(CurrentItem);

            double durationSeconds = _videoPlayer.Length > 0
                ? Math.Max(0.1, _videoPlayer.Length / 1000d)
                : MaximumPreviewSeconds;

            _previewLimitSeconds = Math.Min(MaximumPreviewSeconds, durationSeconds);
            PositionSlider.Maximum = _previewLimitSeconds;
            LimitTimeText.Text = FormatTime(_previewLimitSeconds);
            PreviewFinishedOverlay.Visibility = Visibility.Collapsed;

            bool firstPlayback = _videoPlaybackNeedsInitialization;
            _videoPlaybackNeedsInitialization = false;
            if (firstPlayback)
                _videoPlayer.Time = 0;

            _isPlaying = true;
            SyncPlayPauseGlyph();
            _positionTimer.Start();

            if (firstPlayback)
            {
                int generation = ++_videoSnapshotGeneration;
                _ = CaptureCurrentVideoThumbnailAsync(generation);
            }
        }));
    }

    private void VideoEngine_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(PauseAtCurrentPosition));
    }

    private void VideoEngine_EncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _positionTimer.Stop();
            _isPlaying = false;
            SyncPlayPauseGlyph();
            VideoPanel.Visibility = Visibility.Collapsed;
            ShowUnsupported("Video verisi global medya motoru tarafından oynatılamadı.");
        }));
    }

    private async Task CaptureCurrentVideoThumbnailAsync(int generation)
    {
        if (_videoPlayer is null || CurrentItem.Category != "Video" || CurrentItem.PreviewImage is not null)
            return;

        RecoveryFileItem item = CurrentItem;
        string snapshotDirectory = Path.Combine(Path.GetTempPath(), "NSXVeriKurtarmaPro", "VideoSnapshots");
        string snapshotPath = Path.Combine(snapshotDirectory, $"snapshot_{Environment.ProcessId}_{Guid.NewGuid():N}.png");

        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            await Task.Delay(900);

            if (generation != _videoSnapshotGeneration || _videoPlayer is null || !ReferenceEquals(item, CurrentItem))
                return;

            _videoPlayer.TakeSnapshot(0, snapshotPath, 320, 180);

            for (int attempt = 0; attempt < 8 && !File.Exists(snapshotPath); attempt++)
                await Task.Delay(100);

            if (generation != _videoSnapshotGeneration || !File.Exists(snapshotPath) || !ReferenceEquals(item, CurrentItem))
                return;

            item.PreviewImage = GlobalImageCodec.LoadPreview(snapshotPath, 240);
            RefreshStrip(centerCurrent: false);
        }
        catch
        {
        }
        finally
        {
            TryDelete(snapshotPath);
        }
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPlaying || _videoPlayer is null)
            return;

        if (_videoPlayer.Length > 0)
        {
            double durationSeconds = Math.Max(0.1, _videoPlayer.Length / 1000d);
            double limit = Math.Min(MaximumPreviewSeconds, durationSeconds);
            if (Math.Abs(limit - _previewLimitSeconds) > 0.05)
            {
                _previewLimitSeconds = limit;
                PositionSlider.Maximum = _previewLimitSeconds;
                LimitTimeText.Text = FormatTime(_previewLimitSeconds);
            }
        }

        double seconds = Math.Max(0d, _videoPlayer.Time / 1000d);
        if (seconds >= _previewLimitSeconds)
        {
            _videoPlayer.Time = (long)(_previewLimitSeconds * 1000d);
            _videoPlayer.Pause();
            _isPlaying = false;
            SyncPlayPauseGlyph();
            _positionTimer.Stop();
            PreviewFinishedOverlay.Visibility = Visibility.Visible;
            UpdateSlider(_previewLimitSeconds);
            return;
        }

        UpdateSlider(seconds);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPlayer is null || _activeVideoMedia is null || _isRepairing)
            return;

        if (_isPlaying)
        {
            PauseAtCurrentPosition();
            return;
        }

        double currentSeconds = Math.Max(0d, _videoPlayer.Time / 1000d);
        if (currentSeconds >= _previewLimitSeconds - 0.05)
        {
            _videoPlayer.Time = 0;
            UpdateSlider(0);
        }

        PreviewFinishedOverlay.Visibility = Visibility.Collapsed;
        _videoPlayer.Play();
        _isPlaying = true;
        SyncPlayPauseGlyph();
        _positionTimer.Start();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _videoPlayer is null || _activeVideoMedia is null || _isRepairing)
            return;

        double seconds = Math.Clamp(e.NewValue, 0d, _previewLimitSeconds);
        _videoPlayer.Time = (long)(seconds * 1000d);
        CurrentTimeText.Text = FormatTime(seconds);
        if (seconds < _previewLimitSeconds)
            PreviewFinishedOverlay.Visibility = Visibility.Collapsed;
    }

    private void PauseAtCurrentPosition()
    {
        try
        {
            _videoPlayer?.Pause();
        }
        catch
        {
        }

        _isPlaying = false;
        SyncPlayPauseGlyph();
        _positionTimer.Stop();
        double seconds = _videoPlayer is null ? 0d : Math.Max(0d, _videoPlayer.Time / 1000d);
        UpdateSlider(Math.Min(seconds, _previewLimitSeconds));
    }

    private void UpdateSlider(double seconds)
    {
        _updatingSlider = true;
        PositionSlider.Value = Math.Clamp(seconds, 0d, _previewLimitSeconds);
        CurrentTimeText.Text = FormatTime(seconds);
        _updatingSlider = false;
    }

    private void SyncPlayPauseGlyph()
    {
        VideoPlayGlyph.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
        VideoPauseGlyph.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowUnsupported(string text)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        PhotoPanel.Visibility = Visibility.Collapsed;
        VideoPanel.Visibility = Visibility.Collapsed;
        UnsupportedText.Text = L(text);
        UnsupportedPanel.Visibility = Visibility.Visible;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private void RefreshStrip(bool centerCurrent)
    {
        PreviewStripPanel.Children.Clear();

        int visibleCount = GetActiveStripWindowSize();
        int maxStart = Math.Max(0, _items.Count - visibleCount);
        if (_stripStartIndex < 0 || _stripStartIndex > maxStart)
            _stripStartIndex = Math.Clamp(_currentIndex - (visibleCount / 2), 0, maxStart);

        if (centerCurrent)
        {
            _stripStartIndex = Math.Clamp(_currentIndex - (visibleCount / 2), 0, maxStart);
        }
        else
        {
            if (_currentIndex < _stripStartIndex)
                _stripStartIndex = _currentIndex;
            else if (_currentIndex >= _stripStartIndex + visibleCount)
                _stripStartIndex = Math.Clamp(_currentIndex - visibleCount + 1, 0, maxStart);
        }

        int start = _stripStartIndex;
        int count = Math.Min(visibleCount, _items.Count - start);

        for (int i = start; i < start + count; i++)
        {
            RecoveryFileItem item = _items[i];
            PreviewStripPanel.Children.Add(CreateStripCard(item, i == _currentIndex));
        }

        StripInfoText.Text = L($" • {_items.Count:N0} öğe");
        UpdateNavigationState();
    }

    private void PreviewStripScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || _items.Count == 0)
            return;

        if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 1)
            return;

        RefreshStrip(centerCurrent: false);
    }

    private void PreviewStripScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_items.Count <= 1)
            return;

        e.Handled = true;

        if (e.Delta > 0)
            NavigateByOffset(-1);
        else if (e.Delta < 0)
            NavigateByOffset(1);
    }

    private void ShiftStripWindow(int direction, double previousOffset)
    {
        if (direction == 0)
            return;

        int visibleCount = GetActiveStripWindowSize();
        int maxStart = Math.Max(0, _items.Count - visibleCount);
        if (_stripStartIndex < 0)
            _stripStartIndex = Math.Clamp(_currentIndex - (visibleCount / 2), 0, maxStart);

        int oldStart = _stripStartIndex;
        if (direction > 0)
            _stripStartIndex = Math.Min(maxStart, _stripStartIndex + StripWheelWindowShift);
        else
            _stripStartIndex = Math.Max(0, _stripStartIndex - StripWheelWindowShift);

        int shiftedItems = Math.Abs(_stripStartIndex - oldStart);
        if (shiftedItems == 0)
            return;

        RefreshStrip(centerCurrent: false);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            double continuityOffset = direction > 0
                ? Math.Max(0d, previousOffset - (shiftedItems * StripCardStride))
                : Math.Min(PreviewStripScrollViewer.ScrollableWidth, previousOffset + (shiftedItems * StripCardStride));

            PreviewStripScrollViewer.ScrollToHorizontalOffset(continuityOffset);
        }));
    }

    private Button CreateStripCard(RecoveryFileItem item, bool isCurrent)
    {
        var button = new Button
        {
            Width = StripCardWidth,
            Height = StripCardHeight,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(5),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isCurrent ? "#EEF4FF" : "#FFFFFF")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isCurrent ? "#0B57C9" : "#D6DEE9")),
            BorderThickness = new Thickness(isCurrent ? 2 : 1),
            Cursor = Cursors.Hand,
            Tag = item
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewBorder = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F6FA")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isCurrent ? "#B8D1FF" : "#E5ECF3")),
            BorderThickness = new Thickness(1),
            ClipToBounds = true
        };

        var previewGrid = new Grid();
        if (item.PreviewImage is not null)
        {
            previewGrid.Children.Add(new Image
            {
                Source = item.PreviewImage,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            });
        }
        else
        {
            previewGrid.Children.Add(new TextBlock
            {
                Text = item.TypeGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Category == "Video" ? "#7C3AED" : "#0B57C9")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (item.IsRepaired)
        {
            previewGrid.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A")),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Child = new TextBlock
                {
                    Text = "✓",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        previewBorder.Child = previewGrid;
        grid.Children.Add(previewBorder);

        var name = new TextBlock
        {
            Text = item.FileName,
            FontSize = 9.3,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#344054")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 5, 0, 0),
            MaxWidth = StripCardWidth - 10
        };
        Grid.SetRow(name, 1);
        grid.Children.Add(name);

        button.Content = grid;
        button.Click += StripCard_Click;
        return button;
    }

    private void StripCard_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepairing || sender is not Button { Tag: RecoveryFileItem item })
            return;

        NavigateToStripItemWithoutScrolling(item);
    }

    private void UpdateNavigationState()
    {
        PreviewStripPanel.IsEnabled = !_isRepairing;
        StripSelectionText.Text = L($"Seçim {_currentIndex + 1} / {_items.Count}");
    }

    private void NavigateToStripItemWithoutScrolling(RecoveryFileItem item)
    {
        int index = IndexOfItem(item);
        if (index < 0 || index == _currentIndex)
            return;

        _currentIndex = index;
        RefreshStrip(centerCurrent: false);
        _ = SafeLoadCurrentItemAsync();
    }

    private void NavigateToItem(RecoveryFileItem item)
    {
        int index = IndexOfItem(item);
        if (index < 0 || index == _currentIndex)
            return;

        _currentIndex = index;
        _stripStartIndex = -1;
        RefreshStrip(centerCurrent: false);
        _ = SafeLoadCurrentItemAsync();
    }

    private int IndexOfItem(RecoveryFileItem item)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
                return i;
        }

        return -1;
    }

    private void RecoveryPreviewWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (_isRepairing)
            return;


        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (e.Key == Key.Left)
        {
            NavigateByOffset(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            NavigateByOffset(1);
            e.Handled = true;
        }
    }

    private void NavigateByOffset(int offset)
    {
        if (_isRepairing || offset == 0)
            return;

        int targetIndex = Math.Clamp(_currentIndex + offset, 0, _items.Count - 1);
        if (targetIndex == _currentIndex)
            return;

        _currentIndex = targetIndex;
        RefreshStrip(centerCurrent: false);
        _ = SafeLoadCurrentItemAsync();
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy) ||
            e.PropertyName == nameof(MainViewModel.IsScanRunning))
            UpdateRecoverAvailability();
    }

    private void UpdateRecoverAvailability()
    {
        RecoverButton.IsEnabled = !_isRepairing && (_mainViewModel is null ||
                                  !_mainViewModel.IsBusy ||
                                  _mainViewModel.IsScanRunning);
    }

    private void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (!RecoverButton.IsEnabled)
            return;

        RecoverRequested = true;
        RequestedRecoveryItem = CurrentItem;
        Close();
    }

    private async void PreviewMedia_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _isRepairing)
            return;

        e.Handled = true;
        await RepairCurrentItemAsync();
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepairing)
            return;

        await RepairCurrentItemAsync();
    }

    private async Task RepairCurrentItemAsync()
    {
        RecoveryFileItem item = CurrentItem;
        if (item.Category is not ("Fotoğraf" or "Video"))
            return;
        if (item.Category == "Video" && !MediaRepairService.CanRepairVideo(item.Extension))
            return;

        if (item.IsRepaired && !string.IsNullOrWhiteSpace(item.RepairedFilePath) && File.Exists(item.RepairedFilePath))
        {
            SetRepairStatus(
                item.RepairMessage ?? "Bu dosya zaten onarıldı. Kurtarma doğrudan onarılmış sürümü kullanacak.",
                false);
            return;
        }

        string repairSourceDirectory = Path.Combine(
            Path.GetTempPath(),
            "NSXVeriKurtarmaPro",
            "RepairSource",
            Guid.NewGuid().ToString("N"));
        string repairedPath = BuildRepairCachePath(item);

        try
        {
            _isRepairing = true;
            PauseAtCurrentPosition();
            RepairButton.IsEnabled = false;
            UpdateReferenceVideoPanel(item);
            UpdateRecoverAvailability();
            UpdateNavigationState();
            ShowRepairLoading(item, "Kaynak veri onarma motoruna hazırlanıyor...");
            SetRepairStatus("Dosya onarım için hazırlanıyor; kaynak veri güvenli biçimde okunuyor...", true);

            string sourcePath = await Task.Run(() =>
                _recoveryService.CreateTemporaryPreviewFile(
                    _device,
                    item,
                    repairSourceDirectory,
                    CancellationToken.None));

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new IOException("Kaynak medya onarma için çıkarılamadı.");

            UpdateRepairStage("Source materialize edildi; container/codec parse ve structural validation çalışıyor...");

            Action<string> progress = message =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                Dispatcher.BeginInvoke(new Action(() => UpdateRepairStage(message)));
            };

            ReferenceVideoProfile? referenceProfile = item.Category == "Video" &&
                                                     _referenceVideoProfile is not null &&
                                                     ReferenceVideoProfileService.IsCompatible(_referenceVideoProfile, item.Extension)
                ? _referenceVideoProfile
                : null;
            if (referenceProfile is not null)
                UpdateRepairStage($"Aynı cihaz referansı aktif • {referenceProfile.Summary}");

            MediaRepairResult result = await Task.Run(() =>
                _mediaRepairService.Repair(item, sourcePath, repairedPath, progress, referenceProfile));

            if (!result.Success || !File.Exists(repairedPath) || result.OutputBytes <= 0)
                throw new InvalidDataException(result.Message);

            item.RepairedFilePath = repairedPath;
            item.RepairMessage = result.Message + " Bundan sonraki kurtarma bu onarılmış sürümü kullanacak.";
            if (item.Category == "Video" && result.VideoHealthScore.HasValue)
            {
                item.VideoHealthScore = result.VideoHealthScore.Value;
                item.VideoHealthGrade = result.VideoHealthGrade;
                item.VideoHealthSummary = result.VideoHealthSummary;
            }
            if (item.Category == "Fotoğraf")
                TryRefreshRepairedThumbnail(item, repairedPath);

            // Başarılı onarım sonrası bilgi/yan panel gösterme; doğrudan sade önizlemeye dön.
            UpdatePreviewLayout(item);
            RefreshStrip(centerCurrent: false);

            // Geçici ham önizlemeyi bırak; aynı pencerede onarılmış sonucu göster.
            DeletePreviewCacheFile();
            ResetPreviewPanels();
            _previewFilePath = repairedPath;
            _ownsPreviewFile = false;
            ShowCurrentMedia(item, repairedPath);
        }
        catch (Exception ex)
        {
            TryDelete(repairedPath);
            item.RepairedFilePath = null;
            item.RepairMessage = null;
            item.VideoHealthScore = null;
            item.VideoHealthGrade = null;
            item.VideoHealthSummary = null;
            SetRepairStatus(L($"Onarma başarısız: {ex.Message}"), false, true);
            MessageBox.Show(
                this,
                string.Format(L("Dosya onarılamadı.\n\n{0}"), L(ex.Message)),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            RepairLoadingOverlay.Visibility = Visibility.Collapsed;
            _isRepairing = false;
            UpdatePreviewLayout(CurrentItem);
            RepairButton.IsEnabled = !CurrentItem.IsRepaired && CanRepairItem(CurrentItem);
            UpdateReferenceVideoPanel(CurrentItem);
            UpdateRecoverAvailability();
            UpdateNavigationState();
            TryDeleteDirectory(repairSourceDirectory);
        }
    }


    private void UpdateReferenceVideoPanel(RecoveryFileItem item)
    {
        bool supported = item.Category == "Video" && ReferenceVideoProfileService.SupportsReference(item.Extension);
        ReferenceVideoPanel.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported)
            return;

        SelectReferenceVideoButton.IsEnabled = !_isRepairing && !_isAnalyzingReference;
        ClearReferenceVideoButton.IsEnabled = !_isRepairing && !_isAnalyzingReference && _referenceVideoProfile is not null;

        if (_isAnalyzingReference)
        {
            ReferenceVideoStatusText.Foreground = new SolidColorBrush(Color.FromRgb(71, 84, 103));
            ReferenceVideoStatusText.Text = L("Referans video analiz ediliyor; codec, zamanlama ve container profili çıkarılıyor...");
            return;
        }

        if (_referenceVideoProfile is null || string.IsNullOrWhiteSpace(_referenceVideoPath))
        {
            ReferenceVideoStatusText.Foreground = new SolidColorBrush(Color.FromRgb(71, 84, 103));
            ReferenceVideoStatusText.Text = L("Referans seçilmedi. Normal onarım motoru çalışmaya devam eder.");
            return;
        }

        bool compatible = ReferenceVideoProfileService.IsCompatible(_referenceVideoProfile, item.Extension);
        ReferenceVideoStatusText.Foreground = new SolidColorBrush(compatible
            ? Color.FromRgb(2, 122, 72)
            : Color.FromRgb(180, 83, 9));
        string fileName = Path.GetFileName(_referenceVideoPath) ?? _referenceVideoPath;
        ReferenceVideoStatusText.Text = compatible
            ? L($"Aktif: {fileName} • {_referenceVideoProfile.Summary}")
            : L($"Seçili referans ({fileName}) bu dosyanın kapsayıcı ailesiyle uyumlu değil; bu dosyada kullanılmayacak.");
    }

    private async void SelectReferenceVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepairing || _isAnalyzingReference)
            return;

        var dialog = new OpenFileDialog
        {
            Title = L("Aynı cihazdan sağlam referans video seç"),
            Filter = L("Desteklenen referans videolar|*.mp4;*.mov;*.qt;*.m4v;*.3gp;*.3g2;*.f4v;*.mts;*.m2ts;*.m2t;*.ts;*.tp;*.trp|Tüm dosyalar|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _isAnalyzingReference = true;
            UpdateReferenceVideoPanel(CurrentItem);
            string selectedPath = dialog.FileName;
            ReferenceVideoProfile profile = await Task.Run(() =>
                ReferenceVideoProfileService.Analyze(selectedPath));

            if (!ReferenceVideoProfileService.IsCompatible(profile, CurrentItem.Extension))
                throw new InvalidDataException("Seçilen referans video mevcut dosyayla aynı kapsayıcı ailesinde değil. MP4/MOV için MP4/MOV; MTS/M2TS/TS için MTS/M2TS/TS referansı seçin.");
            if (profile.Confidence < 75)
                throw new InvalidDataException($"Referans profil güveni yetersiz (%{profile.Confidence}). Başka bir sağlam kamera/telefon videosu seçin.");

            _referenceVideoProfile = profile;
            _referenceVideoPath = selectedPath;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                string.Format(L("Referans video kullanılamadı.\n\n{0}"), L(ex.Message)),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isAnalyzingReference = false;
            UpdateReferenceVideoPanel(CurrentItem);
        }
    }

    private void ClearReferenceVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepairing || _isAnalyzingReference)
            return;
        _referenceVideoProfile = null;
        _referenceVideoPath = null;
        UpdateReferenceVideoPanel(CurrentItem);
    }

    private static void TryRefreshRepairedThumbnail(RecoveryFileItem item, string path)
    {
        try
        {
            item.PreviewImage = GlobalImageCodec.LoadPreview(path, 220);
            (uint width, uint height) = GlobalImageCodec.GetDimensions(path);
            item.SetResolution(width, height);
        }
        catch
        {
        }
    }

    private string BuildRepairCachePath(RecoveryFileItem item)
    {
        string extension = item.Category == "Fotoğraf"
            ? GlobalImageCodec.GetRepairExtension(item.Extension)
            : FileTypeHelper.Normalize(item.Extension).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
            extension = "bin";

        string root = Path.Combine(
            Path.GetTempPath(),
            "NSXVeriKurtarmaPro",
            "RepairedCache",
            Environment.ProcessId.ToString());
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"repaired_{Guid.NewGuid():N}.{extension}");
    }

    private void ShowRepairLoading(RecoveryFileItem item, string detail)
    {
        RepairLoadingTitle.Text = L(item.Category == "Video" ? "Video onarılıyor" : "Resim onarılıyor");
        RepairLoadingDetail.Text = L(detail);
        RepairLoadingOverlay.Visibility = Visibility.Visible;
        Panel.SetZIndex(RepairLoadingOverlay, 30);
    }

    private void UpdateRepairStage(string message)
    {
        RepairLoadingDetail.Text = L(message);
        SetRepairStatus(message, true);
    }

    private void SetRepairStatus(string message, bool busy, bool isError = false, bool neutral = false)
    {
        RepairStatusText.Text = L(message);

        string background = isError ? "#FEF3F2"
            : busy ? "#EFF6FF"
            : neutral ? "#F8FAFC"
            : "#ECFDF3";
        string foreground = isError ? "#B42318"
            : busy ? "#0B57C9"
            : neutral ? "#475467"
            : "#027A48";

        RepairStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        RepairStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        => ToggleWorkAreaMaximize();

    private void ToggleWorkAreaMaximize()
    {
        if (_workAreaMaximized)
        {
            _workAreaMaximized = false;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = Math.Max(MinWidth, _restoreBounds.Width);
            Height = Math.Max(MinHeight, _restoreBounds.Height);
            SyncMaximizeGlyph();
            return;
        }

        _restoreBounds = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        Rect work = SystemParameters.WorkArea;
        _workAreaMaximized = true;
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;
        SyncMaximizeGlyph();
    }

    private void SyncMaximizeGlyph()
    {
        if (MaximizeGlyph is null || RestoreGlyph is null)
            return;

        MaximizeGlyph.Visibility = _workAreaMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = _workAreaMaximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreSavedPreviewWindowLayout()
    {
        ProUiSettingsState settings = ProUiSettings.Current;
        ProWindowLayoutState layout = settings.PreviewWindowLayout;
        if (!settings.RememberPreviewWindowLayout || !layout.IsValid)
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
            return;
        }

        Width = Math.Max(MinWidth, layout.Width);
        Height = Math.Max(MinHeight, layout.Height);
        Left = layout.Left;
        Top = layout.Top;
        _restoreBounds = new Rect(Left, Top, Width, Height);
    }

    private void SaveCurrentPreviewWindowLayout()
    {
        if (!ProUiSettings.Current.RememberPreviewWindowLayout)
            return;

        Rect bounds = _workAreaMaximized && _restoreBounds.Width > 0 && _restoreBounds.Height > 0
            ? _restoreBounds
            : new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

        ProUiSettings.SavePreviewWindowLayout(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private void ConstrainToWorkingArea()
    {
        Rect work = SystemParameters.WorkArea;
        double targetWidth = Math.Min(Width, Math.Max(MinWidth, work.Width - 24));
        double targetHeight = Math.Min(Height, Math.Max(MinHeight, work.Height - 24));

        Width = targetWidth;
        Height = targetHeight;
        Left = Math.Max(work.Left + 12, Math.Min(Left, work.Right - targetWidth - 12));
        Top = Math.Max(work.Top + 12, Math.Min(Top, work.Bottom - targetHeight - 12));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleWorkAreaMaximize();
            e.Handled = true;
            return;
        }

        if (_workAreaMaximized)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RecoveryPreviewWindow_Closed(object? sender, EventArgs e)
    {
        SaveCurrentPreviewWindowLayout();
        Window? ownerWindow = Owner;

        if (_mainViewModel is not null)
            _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;

        CancelPreviewLoad();
        _positionTimer.Stop();
        _positionTimer.Tick -= PositionTimer_Tick;
        _photoSingleClickTimer.Stop();
        _photoSingleClickTimer.Tick -= PhotoSingleClickTimer_Tick;

        DisposeVideoEngine();

        DeletePreviewCacheFile();
        _previewCts.Dispose();

        if (ownerWindow is not null && ownerWindow.IsVisible)
        {
            ownerWindow.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    if (!ownerWindow.IsVisible)
                        return;

                    ownerWindow.Activate();
                    ownerWindow.Focus();
                }));
        }
    }

    private void DisposeActiveVideoSource()
    {
        _activeVideoMedia?.Dispose();
        _activeVideoMedia = null;
        _activeVideoInput?.Dispose();
        _activeVideoInput = null;
        _activeVideoStream?.Dispose();
        _activeVideoStream = null;
    }

    private void DisposeVideoEngine()
    {
        _videoViewReady = false;
        _videoPlaybackNeedsInitialization = false;
        _pendingVideoPath = null;
        _pendingVideoItem = null;
        _videoSnapshotGeneration++;

        try
        {
            VideoView.Loaded -= VideoView_Loaded;
            VideoView.MediaPlayer = null;
        }
        catch
        {
        }

        if (_videoPlayer is not null)
        {
            try
            {
                _videoPlayer.Playing -= VideoEngine_Playing;
                _videoPlayer.EndReached -= VideoEngine_EndReached;
                _videoPlayer.EncounteredError -= VideoEngine_EncounteredError;
                _videoPlayer.Stop();
            }
            catch
            {
            }

            _videoPlayer.Dispose();
            _videoPlayer = null;
        }

        DisposeActiveVideoSource();
        _libVlc?.Dispose();
        _libVlc = null;

        try
        {
            VideoView.Dispose();
        }
        catch
        {
        }
    }

    private void CancelPreviewLoad()
    {
        try
        {
            _previewCts.Cancel();
        }
        catch
        {
        }

        _previewCts.Dispose();
        _previewCts = new CancellationTokenSource();
    }

    private void DeletePreviewCacheFile()
    {
        if (string.IsNullOrWhiteSpace(_previewFilePath))
            return;

        if (_ownsPreviewFile)
            TryDelete(_previewFilePath);

        _previewFilePath = null;
        _ownsPreviewFile = false;
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
