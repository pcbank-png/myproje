using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using NSXVeriKurtarmaPro.Infrastructure;
using NSXVeriKurtarmaPro.Models;
using NSXVeriKurtarmaPro.Services;

namespace NSXVeriKurtarmaPro.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static string L(string source) => LocalizationService.Current.Translate(source);

    private const int PreviewBatchSize = 96;
    private const int CaptureDateBatchSize = 64;
    private const int AutomaticPhotoPreviewBudget = 48;
    private const int AutomaticVideoPreviewBudget = 4;
    private const int LiveNavigationRefreshIntervalMs = 4000;

    private readonly StorageDeviceService _storageDeviceService = new();
    private readonly QuickScanService _quickScanService = new();
    private readonly DeepScanService _deepScanService = new();
    private readonly NtfsFolderScanService _ntfsFolderScanService = new();
    private readonly RecoveryService _recoveryService = new();
    private readonly DiskImageService _diskImageService = new();
    private readonly RecoveryProjectService _projectService = new();
    private readonly PreviewService _previewService = new();
    private readonly SemaphoreSlim _previewBuildGate = new(2, 2);
    private readonly SemaphoreSlim _previewRecoveryGate = new(1, 1);
    private readonly HashSet<RecoveryFileItem> _previewRequested = [];
    private readonly HashSet<RecoveryFileItem> _visiblePreviewRequested = [];
    private readonly Queue<RecoveryFileItem> _automaticPreviewQueue = [];
    private readonly HashSet<RecoveryFileItem> _captureDateRequested = [];
    private readonly Queue<RecoveryFileItem> _captureDateQueue = [];

    private StorageDeviceInfo? _selectedDevice;
    private RecoveryFileItem? _selectedRecoveryFile;
    private ScanMode _scanMode = ScanMode.Deep;
    private DeepScanTarget _quickScanScope = LoadQuickScanScopePreference();
    private DeepScanTarget _deepScanScope = DeepScanTarget.All;
    private DeepScanTarget _folderScanScope = DeepScanTarget.None;
    private string _folderScanPath = string.Empty;
    private bool _isFolderScanSession;
    private bool _isUnifiedLostDataScan;
    private string _activeNavigation = "Overview";
    private string _statusTitle = "Kayıp Veri Taraması • Hazır";
    private string _statusDetail = "Kaynak aygıtı seçin; özgün dosya yolu metadata’sı ve ham veri tek akışta analiz edilir.";
    private string _progressTitle = "Beklemede";
    private string _progressDetail = "Kayıp verileri taramak için bir kaynak aygıt seçin.";
    private double _progressPercent;
    private string _progressProcessedText = "0 B";
    private string _progressFoundText = "0 dosya";
    private string _progressSpeedText = "—";
    private string _progressElapsedText = "0 sn";
    private string _progressEtaText = "—";
    private bool _isBusy;
    private bool _isScanRunning;
    private bool _isRefreshingDevices;
    private bool _isPaused;
    private string _resultSearchText = string.Empty;
    private string _resultCategory = "Tümü";
    private string _resultTypeGroup = "all";
    private string _resultExtension = string.Empty;
    private string _resultPathFilter = string.Empty;
    private bool _isProResultFilterEnabled;
    private bool _hideZeroByteFiles;
    private bool _hideThumbnailFiles;
    private bool _hideDamagedFiles;
    private bool _hideExistingFiles;
    private bool _hideSystemTemporaryFiles;
    private double _minimumFileSizeMb;
    private int _minimumRecoveryConfidence;
    private bool _isResultTypeNavigationActive;
    private bool _recoveryNavigationInitialized;
    private bool _pathRootExpansionInitialized;
    private readonly HashSet<string> _expandedNavigationGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historicalFolderPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _previewCts;
    private OperationPauseGate? _pauseGate;
    private Stopwatch? _operationStopwatch;
    private long _lastProgressBytes;
    private TimeSpan _lastProgressElapsed;
    private double _smoothedProgressBytesPerSecond;
    private bool _acceptLiveScanResults;
    private bool _deferScanResultOrganization;
    private bool _isReplacingResults;
    private bool _liveNavigationRefreshPending;
    private long _lastLiveNavigationRefreshTick;
    private int _navigationRefreshDispatchPending;
    private bool _isOrganizingResults;
    private int _automaticPhotoPreviewQueued;
    private int _automaticVideoPreviewQueued;
    private bool _automaticPreviewPumpRunning;
    private bool _captureDatePumpRunning;
    private bool _isRecoveryInProgress;
    private bool _isBulkSelectionUpdate;
    private readonly Dictionary<RecoveryFileItem, bool> _selectionStateCache = [];
    private TaskCompletionSource<bool>? _operationCompletion;
    private long _scanResumePosition;
    private long _scanResumeTotal;
    private bool _scanResumeAvailable;
    private RecoveryScanCheckpoint? _scanCheckpoint;
    private string _projectId = Guid.NewGuid().ToString("N");
    private RecoveryProjectState? _pendingProjectState;
    private string? _currentProjectPath;
    private bool _resumeReplayInProgress;
    private long _resumePositionFloor;
    private double _resumePercentFloor;
    private bool _scanWaitingForReconnect;
    private bool _scanDisconnectInterruptRequested;
    private string _activeScanRootPath = string.Empty;

    public ObservableCollection<StorageDeviceInfo> Devices { get; } = [];
    public RangeObservableCollection<RecoveryFileItem> RecoveryFiles { get; } = [];
    public ObservableCollection<RecoveryNavigationNode> TypeNavigation { get; } = [];
    public ObservableCollection<RecoveryNavigationNode> PathNavigation { get; } = [];
    public ObservableCollection<RecoveryNavigationNode> VisiblePathNavigation { get; } = [];
    public ICollectionView RecoveryFilesView { get; }
    public IReadOnlyList<string> ResultCategories { get; } = ["Tümü", "Fotoğraf", "Video", "Belge"];

    public event EventHandler? ScanCompleted;

    public ICommand RefreshDevicesCommand { get; }
    public ICommand SelectDeviceCommand { get; }
    public ICommand ShowOverviewCommand { get; }
    public ICommand SetScanModeCommand { get; }
    public ICommand SetQuickScanScopeCommand { get; }
    public ICommand StartQuickScanCommand { get; }
    public ICommand SetDeepScanScopeCommand { get; }
    public ICommand SetFolderScanScopeCommand { get; }
    public ICommand BrowseFolderScanCommand { get; }
    public ICommand StartScanCommand { get; }
    public ICommand CreateImageCommand { get; }
    public ICommand RecoverSelectedCommand { get; }
    public ICommand RecoverSingleCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }
    public ICommand ClearRecoverySelectionCommand { get; }
    public ICommand ClearResultsCommand { get; }
    public ICommand ShowAllResultsCommand { get; }
    public ICommand SetResultCategoryCommand { get; }
    public ICommand ShowResultNavigationCommand { get; }
    public ICommand SetResultNavigationFilterCommand { get; }
    public ICommand ToggleNavigationGroupCommand { get; }
    public ICommand TogglePathNodeSelectionCommand { get; }
    public ICommand ReturnToDeviceSelectionCommand { get; }
    public ICommand ScanDesktopCommand { get; }
    public ICommand ScanRecycleBinCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CancelCommand { get; }
    public MainViewModel()
    {
        LoadProResultFilterPreferences();

        RecoveryFilesView = CollectionViewSource.GetDefaultView(RecoveryFiles);
        RecoveryFilesView.Filter = FilterRecoveryFile;

        RefreshDevicesCommand = new AsyncRelayCommand(_ => RefreshDevicesAsync(), _ => !IsBusy && !IsRefreshingDevices);
        SelectDeviceCommand = new RelayCommand(SelectDevice, _ => !IsBusy);
        ShowOverviewCommand = new RelayCommand(_ => SetActiveNavigation("Overview"), _ => !IsBusy);
        SetScanModeCommand = new RelayCommand(SetScanMode, _ => !IsBusy);
        SetQuickScanScopeCommand = new RelayCommand(SetQuickScanScope, _ => !IsBusy);
        StartQuickScanCommand = new AsyncRelayCommand(_ => StartQuickScanAsync(), _ => SelectedDevice?.IsReady == true && !IsBusy);
        SetDeepScanScopeCommand = new RelayCommand(SetDeepScanScope, _ => !IsBusy);
        SetFolderScanScopeCommand = new RelayCommand(SetFolderScanScope, _ => !IsBusy);
        BrowseFolderScanCommand = new AsyncRelayCommand(_ => BrowseAndStartFolderScanAsync(), _ => !IsBusy);
        StartScanCommand = new AsyncRelayCommand(_ => StartUnifiedLostDataScanAsync(), _ => SelectedDevice?.IsReady == true && !IsBusy);
        CreateImageCommand = new AsyncRelayCommand(_ => CreateImageAsync(), _ => SelectedDevice?.IsReady == true && !IsBusy);
        RecoverSelectedCommand = new AsyncRelayCommand(_ => RecoverSelectedAsync(), _ => SelectedDevice?.IsReady == true && (SelectedCount > 0 || SelectedRecoveryFile is not null) && !_isRecoveryInProgress && (!IsBusy || IsScanRunning));
        RecoverSingleCommand = new AsyncRelayCommand(_ => RecoverSingleAsync(), _ => SelectedDevice?.IsReady == true && SelectedRecoveryFile is not null && !_isRecoveryInProgress && (!IsBusy || IsScanRunning));
        ToggleSelectAllCommand = new RelayCommand(_ => ToggleSelectAll(), _ => RecoveryFiles.Count > 0 && !IsBusy);
        ClearRecoverySelectionCommand = new RelayCommand(_ => ClearRecoverySelection(), _ => SelectedCount > 0 && !_isRecoveryInProgress);
        ClearResultsCommand = new RelayCommand(_ => ClearResults(), _ => RecoveryFiles.Count > 0 && !IsBusy);
        ShowAllResultsCommand = new RelayCommand(_ => ShowAllResults(), _ => RecoveryFiles.Count > 0);
        SetResultCategoryCommand = new RelayCommand(p => ResultCategory = p?.ToString() ?? "Tümü");
        ShowResultNavigationCommand = new RelayCommand(SetResultNavigationMode);
        SetResultNavigationFilterCommand = new RelayCommand(SetResultNavigationFilter);
        ToggleNavigationGroupCommand = new RelayCommand(ToggleNavigationGroup);
        TogglePathNodeSelectionCommand = new RelayCommand(TogglePathNodeSelection);
        ReturnToDeviceSelectionCommand = new RelayCommand(_ => ReturnToDeviceSelection(), _ => !IsBusy);
        ScanDesktopCommand = new AsyncRelayCommand(_ => StartKnownFolderScanAsync(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), _ => !IsBusy);
        ScanRecycleBinCommand = new AsyncRelayCommand(_ => StartRecycleBinScanAsync(), _ => !IsBusy);
        PauseCommand = new RelayCommand(_ => PauseOperation(), _ => IsBusy && !IsPaused && !_isRecoveryInProgress);
        ResumeCommand = new RelayCommand(_ => ResumeOperation(), _ => IsBusy && IsPaused && !_isRecoveryInProgress);
        StopCommand = new RelayCommand(_ => StopOperation(), _ => IsBusy && !_isRecoveryInProgress);
        CancelCommand = StopCommand;
        Debug.WriteLine("[NSX STARTUP] VM-1 commands ready");
        RecoveryFiles.CollectionChanged += (_, _) =>
        {
            // Final snapshots are committed by ReplaceResults; never rebuild navigation
            // from this callback and then rebuild the same tree again during finalization.
            if (_isReplacingResults)
                return;
            // Tarama sırasında bulunan her batch'te milyonluk listeyi yeniden filtreleyip
            // klasör/tür ağacını baştan kurmak I/O motorundan daha pahalı olabiliyor.
            // Live scan yalnız sayısal durumları günceller; ağır organizasyon final fazına bırakılır.
            if (_deferScanResultOrganization)
            {
                RaiseLiveScanResultState();
                RequestLiveNavigationRefresh();
                RaiseCommandStates();
                return;
            }

            RecoveryFilesView.Refresh();
            if (IsRecoveryWorkspace)
                RefreshRecoveryNavigation();
            RaiseResultState();
            RaiseCommandStates();
        };

        Debug.WriteLine("[NSX STARTUP] VM-2 refreshing devices");
        RefreshDevices();
        Debug.WriteLine("[NSX STARTUP] VM-3 constructor completed");
    }

    public void RefreshLocalizedText()
    {
        string[] localizedProperties =
        [
            nameof(QuickScanScopeTitle),
            nameof(DeepScanScopeTitle),
            nameof(FolderScanScopeTitle),
            nameof(ScanModeTitle),
            nameof(ScanModeDescription),
            nameof(SelectedDeviceText),
            nameof(SelectedDeviceDetail),
            nameof(StatusTitle),
            nameof(StatusDetail),
            nameof(ProgressTitle),
            nameof(ProgressDetail),
            nameof(ProgressFoundText),
            nameof(ProgressSpeedText),
            nameof(ProgressElapsedText),
            nameof(ProgressEtaText),
            nameof(ActiveResultFilterTitle),
            nameof(SelectedRepairedText),
            nameof(SelectionCoverageText),
            nameof(SelectionTypeText),
            nameof(SelectionRequiredSpaceText),
            nameof(SelectionSourceText),
            nameof(SelectionSourceDetail),
            nameof(SelectionStatusText),
            nameof(ResultSummaryText),
            nameof(MinimumRecoveryConfidenceText)
        ];

        foreach (string propertyName in localizedProperties)
            OnPropertyChanged(propertyName);

        RecoveryFilesView.Refresh();
    }

    public StorageDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(SelectedDeviceText));
                OnPropertyChanged(nameof(SelectedDeviceDetail));
                OnPropertyChanged(nameof(SelectionSourceText));
                OnPropertyChanged(nameof(SelectionSourceDetail));
                if (_recoveryNavigationInitialized && IsRecoveryWorkspace)
                    RebuildPathNavigation();
                RaiseCommandStates();
            }
        }
    }

    public RecoveryFileItem? SelectedRecoveryFile
    {
        get => _selectedRecoveryFile;
        set
        {
            if (SetProperty(ref _selectedRecoveryFile, value))
            {
                OnPropertyChanged(nameof(HasSelectedRecoveryFile));
                OnPropertyChanged(nameof(IsPreviewEmpty));
                OnPropertyChanged(nameof(HasSingleRecoveryFileInfo));
                OnPropertyChanged(nameof(IsRecoveryInfoEmpty));
                RaiseCommandStates();
            }
        }
    }

    public bool HasSelectedRecoveryFile => SelectedRecoveryFile is not null;
    public bool IsPreviewEmpty => SelectedRecoveryFile is null;

    public bool IsOverviewNavigationActive => _activeNavigation == "Overview";
    public bool IsQuickNavigationActive => _activeNavigation == "Quick";
    public bool IsDeepNavigationActive => _activeNavigation == "Deep";
    public bool IsFolderNavigationActive => _activeNavigation == "Folder";
    public bool IsImageNavigationActive => _activeNavigation == "Image";
    public bool IsRecoveryNavigationActive => _activeNavigation == "Recovery";

    private void SetActiveNavigation(string value)
    {
        if (string.Equals(_activeNavigation, value, StringComparison.Ordinal))
            return;

        _activeNavigation = value;
        OnPropertyChanged(nameof(IsOverviewNavigationActive));
        OnPropertyChanged(nameof(IsQuickNavigationActive));
        OnPropertyChanged(nameof(IsDeepNavigationActive));
        OnPropertyChanged(nameof(IsFolderNavigationActive));
        OnPropertyChanged(nameof(IsImageNavigationActive));
        OnPropertyChanged(nameof(IsRecoveryNavigationActive));
    }

    public ScanMode ScanMode
    {
        get => _scanMode;
        private set
        {
            if (SetProperty(ref _scanMode, value))
            {
                OnPropertyChanged(nameof(ScanModeTitle));
                OnPropertyChanged(nameof(ScanModeDescription));
                StatusTitle = value == ScanMode.Quick ? "Metadata Scan profili seçildi" : "Deep Scan pipeline seçildi";
                StatusDetail = ScanModeDescription;
            }
        }
    }

    public DeepScanTarget QuickScanScope
    {
        get => _quickScanScope;
        private set
        {
            DeepScanTarget normalized = NormalizeQuickScanScope(value);
            if (SetProperty(ref _quickScanScope, normalized))
            {
                OnPropertyChanged(nameof(IsQuickScanScopeAll));
                OnPropertyChanged(nameof(IsQuickScanScopePhoto));
                OnPropertyChanged(nameof(IsQuickScanScopeVideo));
                OnPropertyChanged(nameof(IsQuickScanScopeDocument));
                OnPropertyChanged(nameof(QuickScanScopeTitle));
                OnPropertyChanged(nameof(ScanModeDescription));
                SaveQuickScanScopePreference(normalized);
            }
        }
    }

    public bool IsQuickScanScopeAll => QuickScanScope == DeepScanTarget.All;
    public bool IsQuickScanScopePhoto => QuickScanScope == DeepScanTarget.Photo;
    public bool IsQuickScanScopeVideo => QuickScanScope == DeepScanTarget.Video;
    public bool IsQuickScanScopeDocument => QuickScanScope == DeepScanTarget.Document;

    public string QuickScanScopeTitle => L(QuickScanScope switch
    {
        DeepScanTarget.Photo => "Resim",
        DeepScanTarget.Video => "Video",
        DeepScanTarget.Document => "Dosya",
        _ => "Tümü"
    });

    public DeepScanTarget DeepScanScope
    {
        get => _deepScanScope;
        private set
        {
            if (SetProperty(ref _deepScanScope, value))
            {
                OnPropertyChanged(nameof(IsDeepScanScopeAll));
                OnPropertyChanged(nameof(IsDeepScanScopePhoto));
                OnPropertyChanged(nameof(IsDeepScanScopeVideo));
                OnPropertyChanged(nameof(IsDeepScanScopeDocument));
                OnPropertyChanged(nameof(DeepScanScopeTitle));
                OnPropertyChanged(nameof(ScanModeDescription));
            }
        }
    }

    public bool IsDeepScanScopeAll => DeepScanScope == DeepScanTarget.All;
    public bool IsDeepScanScopePhoto => DeepScanScope == DeepScanTarget.Photo;
    public bool IsDeepScanScopeVideo => DeepScanScope == DeepScanTarget.Video;
    public bool IsDeepScanScopeDocument => DeepScanScope == DeepScanTarget.Document;

    public string DeepScanScopeTitle => L(DeepScanScope switch
    {
        DeepScanTarget.None => "Tür Seçin",
        DeepScanTarget.Photo => "Resim Tara",
        DeepScanTarget.Video => "Video Tara",
        DeepScanTarget.Document => "Dosya Tara",
        DeepScanTarget.PhotoVideo => "Resim + Video",
        DeepScanTarget.PhotoDocument => "Resim + Dosya",
        DeepScanTarget.VideoDocument => "Video + Dosya",
        DeepScanTarget.All => "Tümünü Tara",
        _ => "Tür Seçin"
    });

    public DeepScanTarget FolderScanScope
    {
        get => _folderScanScope;
        private set
        {
            if (SetProperty(ref _folderScanScope, value))
            {
                OnPropertyChanged(nameof(IsFolderScanScopeAll));
                OnPropertyChanged(nameof(IsFolderScanScopePhoto));
                OnPropertyChanged(nameof(IsFolderScanScopeVideo));
                OnPropertyChanged(nameof(IsFolderScanScopeDocument));
                OnPropertyChanged(nameof(FolderScanScopeTitle));
            }
        }
    }

    public bool IsFolderScanScopeAll => FolderScanScope == DeepScanTarget.All;
    public bool IsFolderScanScopePhoto => FolderScanScope == DeepScanTarget.Photo;
    public bool IsFolderScanScopeVideo => FolderScanScope == DeepScanTarget.Video;
    public bool IsFolderScanScopeDocument => FolderScanScope == DeepScanTarget.Document;

    public string FolderScanScopeTitle => L(FolderScanScope switch
    {
        DeepScanTarget.None => "Tür Seçin",
        DeepScanTarget.Photo => "Resim",
        DeepScanTarget.Video => "Video",
        DeepScanTarget.Document => "Dosya",
        DeepScanTarget.PhotoVideo => "Resim + Video",
        DeepScanTarget.PhotoDocument => "Resim + Dosya",
        DeepScanTarget.VideoDocument => "Video + Dosya",
        DeepScanTarget.All => "Tümü",
        _ => "Tür Seçin"
    });

    public string FolderScanPath => _folderScanPath;

    public string ScanModeTitle => L("Kayıp Veri Taraması");

    public string ScanModeDescription => L(
        "Gerçek dosya sistemi metadata’sı, silinmiş klasör zinciri, ham veri imzaları ve gerekli reconstruction adımları tek dengeli tarama akışında otomatik yürütülür.");

    public string SelectedDeviceText => L(
        SelectedDevice is null ? "Aygıt seçilmedi" : $"Seçili aygıt: {SelectedDevice.DisplayName}");

    public string SelectedDeviceDetail =>
        SelectedDevice is null
            ? "—"
            : $"{SelectedDevice.FileSystem} • {RecoveryFileItem.FormatBytes(SelectedDevice.TotalBytes)} • {SelectedDevice.PhysicalDeviceText} • {RecoveryMediaProfileService.Create(SelectedDevice).DiagnosticText}";

    public string StatusTitle
    {
        get => L(_statusTitle);
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => L(_statusDetail);
        private set => SetProperty(ref _statusDetail, value);
    }

    public string ProgressTitle
    {
        get => L(_progressTitle);
        private set => SetProperty(ref _progressTitle, value);
    }

    public string ProgressDetail
    {
        get => L(_progressDetail);
        private set => SetProperty(ref _progressDetail, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (SetProperty(ref _progressPercent, value))
                OnPropertyChanged(nameof(ProgressPercentText));
        }
    }

    public string ProgressPercentText => $"{ProgressPercent:0}%";

    public string ProgressProcessedText
    {
        get => _progressProcessedText;
        private set => SetProperty(ref _progressProcessedText, value);
    }

    public string ProgressFoundText
    {
        get => L(_progressFoundText);
        private set => SetProperty(ref _progressFoundText, value);
    }

    public string ProgressSpeedText
    {
        get => L(_progressSpeedText);
        private set => SetProperty(ref _progressSpeedText, value);
    }

    public string ProgressElapsedText
    {
        get => L(_progressElapsedText);
        private set => SetProperty(ref _progressElapsedText, value);
    }

    public string ProgressEtaText
    {
        get => L(_progressEtaText);
        private set => SetProperty(ref _progressEtaText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RaiseCommandStates();
            }
        }
    }

    public bool IsScanRunning
    {
        get => _isScanRunning;
        private set
        {
            if (SetProperty(ref _isScanRunning, value))
            {
                OnPropertyChanged(nameof(IsRecoveryWorkspace));
                OnPropertyChanged(nameof(IsLandingWorkspace));
                OnPropertyChanged(nameof(SelectionStatusText));
                if (!value && RecoveryFiles.Count == 0 && _recoveryNavigationInitialized)
                    RefreshRecoveryNavigation();

                RaiseCommandStates();
            }
        }
    }

    public bool IsRefreshingDevices
    {
        get => _isRefreshingDevices;
        private set
        {
            if (SetProperty(ref _isRefreshingDevices, value))
                RaiseCommandStates();
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
                RaiseCommandStates();
        }
    }

    public bool IsOrganizingResults
    {
        get => _isOrganizingResults;
        private set => SetProperty(ref _isOrganizingResults, value);
    }

    public bool IsIdle => !IsBusy;
    public bool IsRecoveryWorkspace => _isFolderScanSession || IsScanRunning || RecoveryFiles.Count > 0;
    public bool IsLandingWorkspace => !IsRecoveryWorkspace;
    public bool IsResultTypeNavigationActive => _isResultTypeNavigationActive;
    public bool IsResultPathNavigationActive => !_isResultTypeNavigationActive;
    public string ActiveResultFilterTitle
    {
        get
        {
            if (IsResultPathNavigationActive)
            {
                if (!string.IsNullOrWhiteSpace(_resultPathFilter))
                    return _resultPathFilter.StartsWith("folder:", StringComparison.OrdinalIgnoreCase)
                        ? BuildPathBreadcrumb(_resultPathFilter[7..])
                        : _resultPathFilter.Equals("unresolved", StringComparison.OrdinalIgnoreCase)
                            ? L("Yolu Bulunamayan Dosyalar")
                            : _resultPathFilter.StartsWith("source:", StringComparison.OrdinalIgnoreCase)
                                ? GetSourceBucketTitle(_resultPathFilter[7..])
                                : L("Taranan Konum");

                return SelectedDevice?.DisplayName ?? L("Taranan Konum");
            }

            if (!string.IsNullOrWhiteSpace(_resultExtension))
                return _resultExtension.ToUpperInvariant();

            if (!string.Equals(_resultTypeGroup, "all", StringComparison.OrdinalIgnoreCase))
                return L(RecoveryTypeCatalog.AllGroups.FirstOrDefault(g => string.Equals(g.Key, _resultTypeGroup, StringComparison.OrdinalIgnoreCase))?.Title ?? "Dosyalar");

            return L("Tüm Dosyalar");
        }
    }
    public int FoundCount => RecoveryFiles.Count;
    public long FoundBytes => RecoveryFiles.Sum(f => Math.Max(0L, f.SizeBytes));
    public string FoundBytesText => RecoveryFileItem.FormatBytes(FoundBytes);
    public int PhotoCount => RecoveryFiles.Count(f => f.Category == "Fotoğraf");
    public int VideoCount => RecoveryFiles.Count(f => f.Category == "Video");
    public int DocumentCount => RecoveryFiles.Count(f => f.Category == "Belge");
    public int SelectedCount => RecoveryFiles.Count(f => f.IsChecked);
    public long SelectedBytes => RecoveryFiles.Where(f => f.IsChecked).Sum(f => Math.Max(0, f.SizeBytes));
    public string SelectedBytesText => RecoveryFileItem.FormatBytes(SelectedBytes);
    public bool HasRecoverySelection => SelectedCount > 0;
    public bool HasSingleRecoveryFileInfo => !HasRecoverySelection && SelectedRecoveryFile is not null;
    public bool IsRecoveryInfoEmpty => !HasRecoverySelection && SelectedRecoveryFile is null;

    public int SelectedPhotoCount => RecoveryFiles.Count(f => f.IsChecked && f.Category == "Fotoğraf");
    public int SelectedVideoCount => RecoveryFiles.Count(f => f.IsChecked && f.Category == "Video");
    public int SelectedDocumentCount => RecoveryFiles.Count(f => f.IsChecked && f.Category == "Belge");
    public long SelectedPhotoBytes => RecoveryFiles.Where(f => f.IsChecked && f.Category == "Fotoğraf").Sum(f => Math.Max(0, f.SizeBytes));
    public long SelectedVideoBytes => RecoveryFiles.Where(f => f.IsChecked && f.Category == "Video").Sum(f => Math.Max(0, f.SizeBytes));
    public long SelectedDocumentBytes => RecoveryFiles.Where(f => f.IsChecked && f.Category == "Belge").Sum(f => Math.Max(0, f.SizeBytes));
    public string SelectedPhotoBytesText => RecoveryFileItem.FormatBytes(SelectedPhotoBytes);
    public string SelectedVideoBytesText => RecoveryFileItem.FormatBytes(SelectedVideoBytes);
    public string SelectedDocumentBytesText => RecoveryFileItem.FormatBytes(SelectedDocumentBytes);
    public int SelectedExtensionCount => RecoveryFiles
        .Where(f => f.IsChecked)
        .Select(f => FileTypeHelper.Normalize(f.Extension))
        .Where(ext => !string.IsNullOrWhiteSpace(ext))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
    public int SelectedRepairedCount => RecoveryFiles.Count(f => f.IsChecked && f.IsRepaired);
    public string SelectedRepairedText => L($"{SelectedRepairedCount:N0} dosya");
    public int SelectedVisibleCount => RecoveryFilesView.Cast<RecoveryFileItem>().Count(f => f.IsChecked);
    public string SelectionCoverageText => L(DisplayedCount == FoundCount
        ? $"{SelectedCount:N0} / {FoundCount:N0} sonuç"
        : $"{SelectedVisibleCount:N0} görünür • {SelectedCount:N0} toplam");
    public string SelectionTypeText => L($"{SelectedExtensionCount:N0} dosya türü");
    public string SelectionRequiredSpaceText => L($"En az {SelectedBytesText}");
    public string SelectionSourceText => SelectedDevice?.DisplayName ?? L("Kaynak aygıt");
    public string SelectionSourceDetail => SelectedDevice is null
        ? "—"
        : $"{SelectedDevice.FileSystem} • {SelectedDevice.PhysicalDeviceText}";
    public string SelectionStatusText => L(_isRecoveryInProgress
        ? "Kurtarma işlemi sürüyor"
        : IsScanRunning
            ? "Tarama sürüyor • seçim anlık güncelleniyor"
            : "Kurtarmaya hazır");

    public int DisplayedCount => RecoveryFilesView.Cast<object>().Count();
    public bool IsResultsEmpty => DisplayedCount == 0;
    public string ResultSummaryText => L($"{DisplayedCount:N0} dosya gösteriliyor • Seçili: {SelectedCount:N0} • {SelectedBytesText}");

    public string ResultSearchText
    {
        get => _resultSearchText;
        set
        {
            if (SetProperty(ref _resultSearchText, value ?? string.Empty))
            {
                RecoveryFilesView.Refresh();
                if (_recoveryNavigationInitialized && !_deferScanResultOrganization)
                {
                    if (IsResultPathNavigationActive)
                        RebuildPathNavigation();
                    else
                        RebuildTypeNavigationCounts();
                }
                RaiseResultState();
            }
        }
    }

    public bool IsProResultFilterEnabled
    {
        get => _isProResultFilterEnabled;
        set
        {
            if (SetProperty(ref _isProResultFilterEnabled, value))
            {
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public bool HideZeroByteFiles
    {
        get => _hideZeroByteFiles;
        set
        {
            if (SetProperty(ref _hideZeroByteFiles, value))
            {
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public bool HideThumbnailFiles
    {
        get => _hideThumbnailFiles;
        set
        {
            if (SetProperty(ref _hideThumbnailFiles, value))
            {
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public bool HideDamagedFiles
    {
        get => _hideDamagedFiles;
        set
        {
            if (SetProperty(ref _hideDamagedFiles, value))
            {
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public bool HideExistingFiles
    {
        get => _hideExistingFiles;
        set
        {
            if (SetProperty(ref _hideExistingFiles, value))
            {
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public bool HideSystemTemporaryFiles
    {
        get => _hideSystemTemporaryFiles;
        set
        {
            if (SetProperty(ref _hideSystemTemporaryFiles, value))
            {
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public double MinimumFileSizeMb
    {
        get => _minimumFileSizeMb;
        set
        {
            double normalized = double.IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0d;
            normalized = Math.Round(normalized, 1, MidpointRounding.AwayFromZero);
            if (SetProperty(ref _minimumFileSizeMb, normalized))
            {
                OnPropertyChanged(nameof(MinimumFileSizeText));
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public string MinimumFileSizeText => $"{MinimumFileSizeMb:0.#} MB";

    public int MinimumRecoveryConfidence
    {
        get => _minimumRecoveryConfidence;
        set
        {
            int normalized = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _minimumRecoveryConfidence, normalized))
            {
                OnPropertyChanged(nameof(MinimumRecoveryConfidenceText));
                SaveProResultFilterPreferences();
                RefreshProResultFilters();
            }
        }
    }

    public string MinimumRecoveryConfidenceText => MinimumRecoveryConfidence <= 0
        ? "0%"
        : $"%{MinimumRecoveryConfidence}";

    private void LoadProResultFilterPreferences()
    {
        try
        {
            string path = GetProResultFilterSettingsPath();
            if (!File.Exists(path))
            {
                // İlk kullanımda en güvenli ve pratik temizlik seçeneklerini hazır getir.
                _hideZeroByteFiles = true;
                _hideThumbnailFiles = true;
                _hideDamagedFiles = true;
                _hideSystemTemporaryFiles = true;
                return;
            }

            ProResultFilterPreferences? settings = JsonSerializer.Deserialize<ProResultFilterPreferences>(File.ReadAllText(path));
            if (settings is null)
                return;

            _isProResultFilterEnabled = settings.Enabled;
            _hideZeroByteFiles = settings.HideZeroByteFiles;
            _hideThumbnailFiles = settings.HideThumbnailFiles;
            _hideDamagedFiles = settings.HideDamagedFiles;
            _hideExistingFiles = settings.HideExistingFiles;
            _hideSystemTemporaryFiles = settings.HideSystemTemporaryFiles;
            _minimumFileSizeMb = Math.Clamp(settings.MinimumFileSizeMb, 0d, 100d);
            _minimumRecoveryConfidence = Math.Clamp(settings.MinimumRecoveryConfidence, 0, 100);
        }
        catch
        {
            // Filtre tercihleri bozuk/erişilemez olsa bile kurtarma akışı etkilenmez.
        }
    }

    private void SaveProResultFilterPreferences()
    {
        try
        {
            string path = GetProResultFilterSettingsPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var settings = new ProResultFilterPreferences
            {
                Enabled = _isProResultFilterEnabled,
                HideZeroByteFiles = _hideZeroByteFiles,
                HideThumbnailFiles = _hideThumbnailFiles,
                HideDamagedFiles = _hideDamagedFiles,
                HideExistingFiles = _hideExistingFiles,
                HideSystemTemporaryFiles = _hideSystemTemporaryFiles,
                MinimumFileSizeMb = _minimumFileSizeMb,
                MinimumRecoveryConfidence = _minimumRecoveryConfidence
            };

            File.WriteAllText(path, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Tercih kaydı başarısız olsa da çalışan filtre ve kurtarma süreci kesilmez.
        }
    }

    private static string GetProResultFilterSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NSX Yazılım",
        "NSX Veri Kurtarma Pro",
        "result-filter.json");

    private sealed class ProResultFilterPreferences
    {
        public bool Enabled { get; set; }
        public bool HideZeroByteFiles { get; set; }
        public bool HideThumbnailFiles { get; set; }
        public bool HideDamagedFiles { get; set; }
        public bool HideExistingFiles { get; set; }
        public bool HideSystemTemporaryFiles { get; set; }
        public double MinimumFileSizeMb { get; set; }
        public int MinimumRecoveryConfidence { get; set; }
    }

    private void RefreshProResultFilters()
    {
        RecoveryFilesView.Refresh();
        if (_recoveryNavigationInitialized && !_deferScanResultOrganization)
        {
            if (IsResultPathNavigationActive)
                RebuildPathNavigation();
            else
                RebuildTypeNavigationCounts();
        }

        RaiseResultState();
    }

    public string ResultCategory
    {
        get => _resultCategory;
        set
        {
            if (SetProperty(ref _resultCategory, string.IsNullOrWhiteSpace(value) ? "Tümü" : value))
            {
                RecoveryFilesView.Refresh();
                if (_recoveryNavigationInitialized && IsResultPathNavigationActive && !_deferScanResultOrganization)
                    RebuildPathNavigation();
                OnPropertyChanged(nameof(IsResultFilterAllActive));
                OnPropertyChanged(nameof(IsResultFilterPhotoActive));
                OnPropertyChanged(nameof(IsResultFilterVideoActive));
                OnPropertyChanged(nameof(IsResultFilterDocumentActive));
                RaiseResultState();
            }
        }
    }

    public bool IsResultFilterAllActive => string.Equals(ResultCategory, "Tümü", StringComparison.OrdinalIgnoreCase);
    public bool IsResultFilterPhotoActive => string.Equals(ResultCategory, "Fotoğraf", StringComparison.OrdinalIgnoreCase);
    public bool IsResultFilterVideoActive => string.Equals(ResultCategory, "Video", StringComparison.OrdinalIgnoreCase);
    public bool IsResultFilterDocumentActive => string.Equals(ResultCategory, "Belge", StringComparison.OrdinalIgnoreCase);

    public bool AreAllVisibleSelected
    {
        get
        {
            List<RecoveryFileItem> visible = RecoveryFilesView.Cast<RecoveryFileItem>().ToList();
            return visible.Count > 0 && visible.All(f => f.IsChecked);
        }
        set
        {
            List<RecoveryFileItem> visible = RecoveryFilesView.Cast<RecoveryFileItem>().ToList();
            if (visible.Count == 0)
                return;

            _isBulkSelectionUpdate = true;
            try
            {
                foreach (RecoveryFileItem file in visible)
                    file.IsChecked = value;
            }
            finally
            {
                _isBulkSelectionUpdate = false;
            }

            RefreshPathNavigationSelectionStates();
            RaiseResultState();
            RaiseCommandStates();
        }
    }

    private bool EnsureNavigationDispatcherAccess()
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return true;

        QueueRecoveryNavigationRefresh();
        return false;
    }

    private void QueueRecoveryNavigationRefresh()
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (Interlocked.Exchange(ref _navigationRefreshDispatchPending, 1) != 0)
            return;

        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _navigationRefreshDispatchPending, 0);
                RefreshRecoveryNavigation();
                RecoveryFilesView.Refresh();
                RaiseResultState();
                RaiseCommandStates();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _navigationRefreshDispatchPending, 0);
        }
    }

    private void BuildTypeNavigation()
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        TypeNavigation.Clear();
        TypeNavigation.Add(new RecoveryNavigationNode
        {
            Title = "Tüm Dosyalar",
            FilterKind = "all",
            FilterValue = "all",
            GroupKey = "all",
            Glyph = "\uE80A",
            IconForeground = "#0B57C9",
            IconBackground = "#EAF2FF",
            IsGroupHeader = true,
            IsSelected = true
        });

        foreach (RecoveryTypeCatalog.Group group in RecoveryTypeCatalog.AllGroups)
        {
            bool isExpanded = _expandedNavigationGroups.Contains(group.Key);
            TypeNavigation.Add(new RecoveryNavigationNode
            {
                Title = group.Title,
                FilterKind = "category",
                FilterValue = group.Key,
                GroupKey = group.Key,
                Glyph = group.Glyph,
                IconForeground = RecoveryTypeCatalog.GetIconForeground(group.Key),
                IconBackground = RecoveryTypeCatalog.GetIconBackground(group.Key),
                IsGroupHeader = true,
                IsExpandable = true,
                IsExpanded = isExpanded
            });

            foreach (string extension in group.Extensions)
            {
                TypeNavigation.Add(new RecoveryNavigationNode
                {
                    Title = extension,
                    FilterKind = "extension",
                    FilterValue = extension,
                    GroupKey = group.Key,
                    ParentGroupKey = group.Key,
                    Glyph = "\uE8B7",
                    IconForeground = RecoveryTypeCatalog.GetIconForeground(group.Key),
                    IconBackground = RecoveryTypeCatalog.GetIconBackground(group.Key),
                    IndentWidth = 18,
                    IsVisible = isExpanded
                });
            }
        }

        RebuildTypeNavigationCounts();
    }

    private void RefreshRecoveryNavigation()
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        if (!IsRecoveryWorkspace)
        {
            if (_recoveryNavigationInitialized)
            {
                TypeNavigation.Clear();
                PathNavigation.Clear();
                VisiblePathNavigation.Clear();
                _recoveryNavigationInitialized = false;
            }
            return;
        }

        if (!_recoveryNavigationInitialized)
        {
            BuildTypeNavigation();
            _recoveryNavigationInitialized = true;
        }

        RebuildTypeNavigationCounts();
        RebuildPathNavigation();
        UpdateNavigationSelection();
    }

    private void RebuildTypeNavigationCounts()
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        if (TypeNavigation.Count == 0)
            return;

        string search = ResultSearchText.Trim();
        List<RecoveryFileItem> navigationFiles = RecoveryFiles
            .Where(file => MatchesProResultFilters(file) &&
                           (search.Length == 0 ||
                            file.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                            file.Extension.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                            file.RecoveryState.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                            file.LocationText.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        var extensionCounts = navigationFiles
            .GroupBy(file => RecoveryTypeCatalog.NormalizeExtension(file.Extension), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        EnsureDynamicTypeNavigationExtensions(extensionCounts.Keys);

        foreach (RecoveryNavigationNode node in TypeNavigation)
        {
            node.Count = node.FilterKind switch
            {
                "all" => navigationFiles.Count,
                "category" => navigationFiles.Count(file => RecoveryTypeCatalog.ExtensionBelongsTo(file.Extension, node.FilterValue)),
                "extension" when string.Equals(node.GroupKey, "unsaved", StringComparison.OrdinalIgnoreCase) =>
                    string.Equals(node.FilterValue, "uzantısız", StringComparison.OrdinalIgnoreCase)
                        ? navigationFiles.Count(file => string.IsNullOrWhiteSpace(RecoveryTypeCatalog.NormalizeExtension(file.Extension)))
                        : 0,
                "extension" => extensionCounts.TryGetValue(RecoveryTypeCatalog.NormalizeExtension(node.FilterValue), out int count) ? count : 0,
                _ => 0
            };
        }
    }

    private void EnsureDynamicTypeNavigationExtensions(IEnumerable<string> extensions)
    {
        var existingExtensions = new HashSet<string>(
            TypeNavigation
                .Where(node => string.Equals(node.FilterKind, "extension", StringComparison.OrdinalIgnoreCase))
                .Select(node => RecoveryTypeCatalog.NormalizeExtension(node.FilterValue))
                .Where(extension => extension.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (string extension in extensions
                     .Select(RecoveryTypeCatalog.NormalizeExtension)
                     .Where(extension => extension.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase))
        {
            if (!existingExtensions.Add(extension))
                continue;

            string groupKey = RecoveryTypeCatalog.GetGroupKey(extension);
            int groupIndex = TypeNavigation
                .Select((node, index) => (node, index))
                .Where(entry => string.Equals(entry.node.FilterKind, "category", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(entry.node.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.index)
                .DefaultIfEmpty(-1)
                .First();
            if (groupIndex < 0)
                continue;

            RecoveryNavigationNode groupNode = TypeNavigation[groupIndex];
            int insertIndex = groupIndex + 1;
            while (insertIndex < TypeNavigation.Count &&
                   string.Equals(TypeNavigation[insertIndex].ParentGroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
            {
                insertIndex++;
            }

            TypeNavigation.Insert(insertIndex, new RecoveryNavigationNode
            {
                Title = extension,
                FilterKind = "extension",
                FilterValue = extension,
                GroupKey = groupKey,
                ParentGroupKey = groupKey,
                Glyph = "\uE8B7",
                IconForeground = RecoveryTypeCatalog.GetIconForeground(groupKey),
                IconBackground = RecoveryTypeCatalog.GetIconBackground(groupKey),
                IndentWidth = 18,
                IsVisible = groupNode.IsExpanded
            });
        }
    }

    private bool MergeHistoricalFolderPaths(IEnumerable<string>? paths)
    {
        if (paths is null)
            return false;

        bool changed = false;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string normalized = NormalizeFolderNavigationPath(path);
            if (normalized != "\\" && _historicalFolderPaths.Add(normalized))
                changed = true;
        }

        return changed;
    }

    private void RebuildPathNavigation()
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        const string rootGroupKey = "path:root";
        if (!_pathRootExpansionInitialized)
        {
            _expandedNavigationGroups.Add(rootGroupKey);
            _pathRootExpansionInitialized = true;
        }
        bool isRootExpanded = _expandedNavigationGroups.Contains(rootGroupKey);
        RecoveryPathTreeSnapshot snapshot = RecoveryPathTreeService.Build(
            RecoveryFiles.Where(MatchesNonPathResultFilters),
            _historicalFolderPaths);
        var desired = new List<RecoveryNavigationNode>(snapshot.Entries.Count + 2)
        {
            new()
            {
                Title = SelectedDevice?.DisplayName ?? "Taranan Konum",
                FilterKind = "all",
                FilterValue = "all",
                GroupKey = rootGroupKey,
                Glyph = "\uEDA2",
                IconForeground = "#C98C00",
                IconBackground = "#FFD458",
                Count = snapshot.FileCount,
                IsGroupHeader = true,
                IsExpandable = snapshot.Entries.Count > 0 || snapshot.UnresolvedFileCount > 0,
                IsExpanded = isRootExpanded,
                IsSelected = string.IsNullOrWhiteSpace(_resultPathFilter),
                IsCheckable = snapshot.FileCount > 0,
                SelectedCount = snapshot.SelectedFileCount
            }
        };

        foreach (RecoveryPathTreeEntry entry in snapshot.Entries)
        {
            desired.Add(new RecoveryNavigationNode
            {
                Title = entry.Title,
                FilterKind = "path",
                FilterValue = "folder:" + entry.Path,
                GroupKey = "path:folder:" + entry.Path,
                ParentGroupKey = entry.ParentPath == "\\"
                    ? rootGroupKey
                    : "path:folder:" + entry.ParentPath,
                Glyph = "\uE8B7",
                IconForeground = "#C98C00",
                IconBackground = "#FFCD38",
                Count = entry.FileCount,
                IndentWidth = Math.Min(112, entry.Depth * 18),
                IsGroupHeader = true,
                IsExpandable = entry.HasChildren,
                IsExpanded = _expandedNavigationGroups.Contains("path:folder:" + entry.Path),
                IsVisible = false,
                IsCheckable = entry.FileCount > 0,
                SelectedCount = entry.SelectedFileCount
            });
        }

        if (snapshot.UnresolvedFileCount > 0)
        {
            desired.Add(new RecoveryNavigationNode
            {
                Title = "Yolu Bulunamayan Dosyalar",
                FilterKind = "unresolved",
                FilterValue = "unresolved",
                GroupKey = "path:unresolved",
                ParentGroupKey = rootGroupKey,
                Glyph = "\uE7C3",
                IconForeground = "#8A94A6",
                IconBackground = "#F4F6F8",
                Count = snapshot.UnresolvedFileCount,
                IndentWidth = 18,
                IsVisible = false,
                IsCheckable = true,
                SelectedCount = snapshot.SelectedUnresolvedFileCount
            });
        }

        ReconcilePathNavigation(desired);
        RefreshPathNavigationVisibility();
    }

    private void ReconcilePathNavigation(IReadOnlyList<RecoveryNavigationNode> desired)
    {
        var existingByKey = PathNavigation
            .GroupBy(node => node.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<RecoveryNavigationNode>(desired.Count);

        foreach (RecoveryNavigationNode candidate in desired)
        {
            if (existingByKey.TryGetValue(candidate.GroupKey, out RecoveryNavigationNode? existing) &&
                string.Equals(existing.FilterKind, candidate.FilterKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.FilterValue, candidate.FilterValue, StringComparison.OrdinalIgnoreCase))
            {
                existing.Title = candidate.Title;
                existing.Count = candidate.Count;
                existing.IsExpandable = candidate.IsExpandable;
                existing.IsExpanded = candidate.IsExpanded;
                existing.IsSelected = candidate.IsSelected;
                existing.IsCheckable = candidate.IsCheckable;
                existing.SelectedCount = candidate.SelectedCount;
                ordered.Add(existing);
            }
            else
            {
                ordered.Add(candidate);
            }
        }

        SynchronizeNavigationCollection(PathNavigation, ordered);
    }

    private void SynchronizeNavigationCollection(
        ObservableCollection<RecoveryNavigationNode> collection,
        IReadOnlyList<RecoveryNavigationNode> desired)
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        for (int index = 0; index < desired.Count; index++)
        {
            RecoveryNavigationNode target = desired[index];
            if (index < collection.Count && ReferenceEquals(collection[index], target))
                continue;

            int existingIndex = collection.IndexOf(target);
            if (existingIndex >= 0)
                collection.Move(existingIndex, index);
            else
                collection.Insert(index, target);
        }

        while (collection.Count > desired.Count)
            collection.RemoveAt(collection.Count - 1);
    }

    private void RefreshPathNavigationVisibility()
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        var nodesByKey = PathNavigation
            .Where(node => !string.IsNullOrWhiteSpace(node.GroupKey))
            .GroupBy(node => node.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (RecoveryNavigationNode node in PathNavigation)
        {
            if (string.Equals(node.GroupKey, "path:root", StringComparison.OrdinalIgnoreCase))
            {
                node.IsVisible = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.ParentGroupKey))
            {
                node.IsVisible = true;
                continue;
            }

            node.IsVisible = nodesByKey.TryGetValue(node.ParentGroupKey, out RecoveryNavigationNode? parent)
                             && parent.IsVisible
                             && parent.IsExpanded;
        }

        SynchronizeNavigationCollection(
            VisiblePathNavigation,
            PathNavigation.Where(node => node.IsVisible).ToList());
    }

    private void SetResultNavigationMode(object? parameter)
    {
        bool typeMode = !string.Equals(parameter?.ToString(), "Path", StringComparison.OrdinalIgnoreCase);

        if (_isResultTypeNavigationActive == typeMode)
            return;

        _isResultTypeNavigationActive = typeMode;
        if (typeMode)
        {
            // YOL'da seçilen klasör TÜR sayımlarını ve sonuçlarını gizlice daraltmamalıdır.
            _resultPathFilter = string.Empty;
        }
        else
        {
            // TÜR'deki uzantı/grup seçimi YOL ağacını eksik göstermemelidir.
            ResultCategory = "Tümü";
            _resultTypeGroup = "all";
            _resultExtension = string.Empty;
        }

        // Tarama sürerken ağır final organizasyonu ertelenir; fakat kullanıcı YOL/TÜR'e
        // bastığında ilgili navigasyon ağacı gecikmeden hazır olmalıdır.
        if (_deferScanResultOrganization && IsRecoveryWorkspace)
            RefreshLiveNavigationForMode(typeMode);
        else if (!typeMode && _recoveryNavigationInitialized && IsRecoveryWorkspace)
            RebuildPathNavigation();
        else if (typeMode && _recoveryNavigationInitialized && IsRecoveryWorkspace)
            RebuildTypeNavigationCounts();

        UpdateNavigationSelection();
        RecoveryFilesView.Refresh();
        OnPropertyChanged(nameof(IsResultTypeNavigationActive));
        OnPropertyChanged(nameof(IsResultPathNavigationActive));
        OnPropertyChanged(nameof(ActiveResultFilterTitle));
        RaiseResultState();
    }

    private void ToggleNavigationGroup(object? parameter)
    {
        if (parameter is not RecoveryNavigationNode node || !node.IsExpandable)
            return;

        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
            _expandedNavigationGroups.Add(node.GroupKey);
        else
            _expandedNavigationGroups.Remove(node.GroupKey);

        if (node.GroupKey.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
        {
            RefreshPathNavigationVisibility();
            return;
        }

        foreach (RecoveryNavigationNode child in TypeNavigation)
        {
            if (!child.IsGroupHeader && string.Equals(child.ParentGroupKey, node.GroupKey, StringComparison.OrdinalIgnoreCase))
                child.IsVisible = node.IsExpanded;
        }
    }

    private void TogglePathNodeSelection(object? parameter)
    {
        if (parameter is not RecoveryNavigationNode node || !node.IsCheckable)
            return;

        bool shouldSelect = node.IsChecked != true;
        _isBulkSelectionUpdate = true;
        try
        {
            foreach (RecoveryFileItem file in RecoveryFiles)
            {
                if (MatchesNonPathResultFilters(file) && MatchesPathNode(file, node))
                    file.IsChecked = shouldSelect;
            }
        }
        finally
        {
            _isBulkSelectionUpdate = false;
        }

        RefreshPathNavigationSelectionStates();
        RaiseResultState();
        RaiseCommandStates();
    }

    private void SetResultNavigationFilter(object? parameter)
    {
        if (parameter is not RecoveryNavigationNode node)
            return;

        switch (node.FilterKind)
        {
            case "category":
                ResultCategory = "Tümü";
                _resultTypeGroup = node.FilterValue;
                _resultExtension = string.Empty;
                _resultPathFilter = string.Empty;
                _isResultTypeNavigationActive = true;
                break;
            case "extension":
                ResultCategory = "Tümü";
                _resultExtension = RecoveryTypeCatalog.NormalizeExtension(node.FilterValue);
                _resultTypeGroup = RecoveryTypeCatalog.GetGroupKey(_resultExtension);
                _resultPathFilter = string.Empty;
                _isResultTypeNavigationActive = true;
                break;
            case "path":
            case "unresolved":
                ResultCategory = "Tümü";
                _resultTypeGroup = "all";
                _resultExtension = string.Empty;
                _resultPathFilter = node.FilterValue;
                _isResultTypeNavigationActive = false;
                break;
            default:
                if (node.GroupKey.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                {
                    ResultCategory = "Tümü";
                    _resultTypeGroup = "all";
                    _resultExtension = string.Empty;
                    _resultPathFilter = string.Empty;
                    _isResultTypeNavigationActive = false;
                }
                else
                {
                    ResultCategory = "Tümü";
                    _resultTypeGroup = "all";
                    _resultExtension = string.Empty;
                    _resultPathFilter = string.Empty;
                    _isResultTypeNavigationActive = true;
                }
                break;
        }

        UpdateNavigationSelection();
        RecoveryFilesView.Refresh();
        OnPropertyChanged(nameof(IsResultTypeNavigationActive));
        OnPropertyChanged(nameof(IsResultPathNavigationActive));
        OnPropertyChanged(nameof(ActiveResultFilterTitle));
        RaiseResultState();
    }

    private void UpdateNavigationSelection()
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        foreach (RecoveryNavigationNode node in TypeNavigation)
        {
            node.IsSelected = node.FilterKind switch
            {
                "all" => string.Equals(_resultTypeGroup, "all", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(_resultExtension),
                "category" => string.IsNullOrWhiteSpace(_resultExtension) && string.Equals(_resultTypeGroup, node.FilterValue, StringComparison.OrdinalIgnoreCase),
                "extension" => string.Equals(_resultExtension, RecoveryTypeCatalog.NormalizeExtension(node.FilterValue), StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        foreach (RecoveryNavigationNode node in PathNavigation)
        {
            node.IsSelected = node.FilterKind == "all"
                ? string.IsNullOrWhiteSpace(_resultPathFilter)
                : string.Equals(_resultPathFilter, node.FilterValue, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool MatchesPathFilter(RecoveryFileItem file, string filter)
    {
        if (filter.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            return RecoveryPathTreeService.MatchesFolder(file, filter[7..]);

        if (filter.Equals("unresolved", StringComparison.OrdinalIgnoreCase))
            return RecoveryPathTreeService.TryGetFolderPath(file) is null;

        if (filter.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(GetSourceBucket(file), filter[7..], StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static string? TryGetFolderPath(RecoveryFileItem file) =>
        RecoveryPathTreeService.TryGetFolderPath(file);

    private static string NormalizeFolderNavigationPath(string? path) =>
        RecoveryPathTreeService.NormalizeFolderPath(path);

    private static bool MatchesPathNode(RecoveryFileItem file, RecoveryNavigationNode node) =>
        node.FilterKind switch
        {
            "all" => true,
            "path" when node.FilterValue.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) =>
                RecoveryPathTreeService.MatchesFolder(file, node.FilterValue[7..]),
            "unresolved" => RecoveryPathTreeService.TryGetFolderPath(file) is null,
            _ => false
        };

    private void RefreshPathNavigationSelectionStates()
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        if (!_recoveryNavigationInitialized || PathNavigation.Count == 0)
            return;

        RecoveryPathTreeSnapshot snapshot = RecoveryPathTreeService.Build(
            RecoveryFiles.Where(MatchesNonPathResultFilters),
            _historicalFolderPaths);
        var entries = snapshot.Entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        foreach (RecoveryNavigationNode node in PathNavigation)
        {
            if (node.FilterKind == "all")
            {
                node.IsCheckable = snapshot.FileCount > 0;
                node.SelectedCount = snapshot.SelectedFileCount;
            }
            else if (node.FilterKind == "unresolved")
            {
                node.IsCheckable = snapshot.UnresolvedFileCount > 0;
                node.SelectedCount = snapshot.SelectedUnresolvedFileCount;
            }
            else if (node.FilterValue.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) &&
                     entries.TryGetValue(NormalizeFolderNavigationPath(node.FilterValue[7..]), out RecoveryPathTreeEntry? entry))
            {
                node.IsCheckable = entry.FileCount > 0;
                node.SelectedCount = entry.SelectedFileCount;
            }
        }
    }

    private void ApplyPathSelectionDelta(RecoveryFileItem file, int delta)
    {
        if (!EnsureNavigationDispatcherAccess())
            return;

        if (!_recoveryNavigationInitialized || PathNavigation.Count == 0 ||
            !MatchesNonPathResultFilters(file) || delta == 0)
        {
            return;
        }

        var nodesByKey = PathNavigation.ToDictionary(node => node.GroupKey, StringComparer.OrdinalIgnoreCase);
        if (nodesByKey.TryGetValue("path:root", out RecoveryNavigationNode? root))
            root.SelectedCount += delta;

        string? folderPath = RecoveryPathTreeService.TryGetFolderPath(file);
        if (folderPath is null)
        {
            if (nodesByKey.TryGetValue("path:unresolved", out RecoveryNavigationNode? unresolved))
                unresolved.SelectedCount += delta;
            return;
        }

        string normalized = NormalizeFolderNavigationPath(folderPath);
        if (normalized == "\\")
            return;

        string current = string.Empty;
        foreach (string part in normalized.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "\\" + part;
            if (nodesByKey.TryGetValue("path:folder:" + current, out RecoveryNavigationNode? folder))
                folder.SelectedCount += delta;
        }
    }

    private string BuildPathBreadcrumb(string path)
    {
        string normalized = NormalizeFolderNavigationPath(path);
        string root = SelectedDevice?.DisplayName ?? "Taranan Konum";
        if (normalized == "\\")
            return root;

        return root + "  ›  " + string.Join("  ›  ", normalized.Trim('\\').Split('\\'));
    }

    private static string GetSourceBucket(RecoveryFileItem file)
    {
        string source = file.SourceText ?? string.Empty;
        if (source.Contains("Derin RAW", StringComparison.OrdinalIgnoreCase)) return "deep";
        if (source.Contains("yeniden", StringComparison.OrdinalIgnoreCase) || source.Contains("Video Parçası", StringComparison.OrdinalIgnoreCase)) return "rebuild";
        if (source.Contains("NTFS", StringComparison.OrdinalIgnoreCase)) return "ntfs";
        if (source.Contains("exFAT", StringComparison.OrdinalIgnoreCase)) return "exfat";
        if (source.Contains("FAT32", StringComparison.OrdinalIgnoreCase)) return "fat32";
        return "other";
    }

    private static string GetSourceBucketTitle(string key) => key.ToLowerInvariant() switch
    {
        "deep" => "Derin Tarama",
        "rebuild" => "Yeniden Oluşturulanlar",
        "ntfs" => "NTFS Kayıtları",
        "exfat" => "exFAT Kayıtları",
        "fat32" => "FAT32 Kayıtları",
        _ => "Diğer Konumlar"
    };

    private void RefreshDevices()
    {
        ApplyDevices(_storageDeviceService.GetDevices(), false, false);
    }

    private async Task RefreshDevicesAsync()
    {
        if (IsRefreshingDevices || IsBusy)
            return;

        IsRefreshingDevices = true;
        StatusTitle = "Storage Topology Refresh";
        StatusDetail = "Windows storage topology ve physical-device descriptors yeniden enumerate ediliyor.";

        try
        {
            IReadOnlyList<StorageDeviceInfo> devices = await Task.Run(_storageDeviceService.GetDevices);
            await Task.Delay(220);
            ApplyDevices(devices, true, true);
            await TryResumePendingProjectAsync();
        }
        catch (Exception ex)
        {
            StatusTitle = "Storage Enumeration Failed";
            StatusDetail = ex.Message;
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    public async Task RefreshAfterMediaRemovalAsync(IReadOnlyCollection<string>? removedRoots = null)
    {
        if (IsRefreshingDevices)
            return;

        string[] normalizedRemovedRoots = (removedRoots ?? Array.Empty<string>())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeRootPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool activeScan = IsScanRunning && !string.IsNullOrWhiteSpace(_activeScanRootPath);
        bool sourceRemoved = activeScan &&
            (normalizedRemovedRoots.Any(root => string.Equals(root, _activeScanRootPath, StringComparison.OrdinalIgnoreCase)) ||
             (normalizedRemovedRoots.Length == 0 && !IsVolumeReady(_activeScanRootPath)));

        // Baska bir USB/disk cikti diye aktif taramayi asla iptal etme.
        // Aygit kartlarini sessizce yenile; scan task ve secili kaynak nesnesi aynen yasamaya devam eder.
        if (activeScan && !sourceRemoved)
        {
            await RefreshDeviceListDuringActiveScanAsync();
            return;
        }

        // Taranan kaynagin kendisi ciktiysa fiziksel okuma devam edemez. Islemi "durdurulmus"
        // saymak yerine resume noktasini koru, handle'lari guvenli kapat ve ayni aygit geri gelene
        // kadar bekle. Yeniden baglaninca TryResumePendingProjectAsync ayni noktadan otomatik baslatir.
        if (sourceRemoved)
        {
            RecoveryProjectState? disconnectState = BuildProjectState(forceResume: true);
            if (disconnectState is not null)
            {
                _pendingProjectState = disconnectState;
                PersistKnownProjectCheckpoint(disconnectState);
            }

            _scanWaitingForReconnect = true;
            _scanDisconnectInterruptRequested = true;
            InterruptScanForReconnect();

            try
            {
                await WaitForCurrentOperationAsync();
            }
            catch (OperationCanceledException)
            {
            }

            IsRefreshingDevices = true;
            try
            {
                await Task.Delay(180);
                IReadOnlyList<StorageDeviceInfo> devices = await Task.Run(_storageDeviceService.GetDevices);
                ApplyDevices(devices, true, false);
                ApplyReconnectWaitingStatus();
            }
            catch (Exception ex)
            {
                AppLog.Error("Kaynak aygit cikarildiktan sonra aygit listesi yenilenemedi.", ex);
                ApplyReconnectWaitingStatus();
            }
            finally
            {
                IsRefreshingDevices = false;
            }

            return;
        }

        // Scan thread aygit cikmasini timer'dan once fark edip bekleme moduna girmis olabilir.
        // Bu durumda gec gelen WM_DEVICECHANGE olayi hicbir seyi durdurmamali; yalniz listeyi yeniler.
        if (_scanWaitingForReconnect)
        {
            IsRefreshingDevices = true;
            try
            {
                await Task.Delay(120);
                IReadOnlyList<StorageDeviceInfo> devices = await Task.Run(_storageDeviceService.GetDevices);
                ApplyDevices(devices, true, false);
                ApplyReconnectWaitingStatus();
            }
            catch (Exception ex)
            {
                AppLog.Error("Kaynak yeniden baglanma beklenirken aygit listesi yenilenemedi.", ex);
                ApplyReconnectWaitingStatus();
            }
            finally
            {
                IsRefreshingDevices = false;
            }

            return;
        }

        // Tarama disindaki mevcut davranisi koru (or. disk imaji/kurtarma).
        RecoveryProjectState? state = BuildProjectState(IsScanRunning || _scanResumeAvailable);
        if (state is not null)
            _pendingProjectState = state;

        if (IsBusy)
        {
            StopOperation();
            try
            {
                await WaitForCurrentOperationAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }

        IsRefreshingDevices = true;
        StatusTitle = "Storage Topology Refresh";
        StatusDetail = "Device-change event alındı; storage topology yeniden enumerate ediliyor.";

        try
        {
            await Task.Delay(320);
            IReadOnlyList<StorageDeviceInfo> devices = await Task.Run(_storageDeviceService.GetDevices);
            ApplyDevices(devices, true, true);
        }
        catch (Exception ex)
        {
            StatusTitle = "Storage Enumeration Failed";
            StatusDetail = ex.Message;
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    private async Task RefreshDeviceListDuringActiveScanAsync()
    {
        if (IsRefreshingDevices)
            return;

        IsRefreshingDevices = true;
        try
        {
            await Task.Delay(120);
            IReadOnlyList<StorageDeviceInfo> devices = await Task.Run(_storageDeviceService.GetDevices);
            MergeDevicesDuringActiveScan(devices);
        }
        catch (Exception ex)
        {
            // Aygit listesi yenileme hatasi tarama motorunu etkilememeli.
            AppLog.Error("Aktif tarama sirasinda aygit listesi yenilenemedi.", ex);
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    private void MergeDevicesDuringActiveScan(IReadOnlyList<StorageDeviceInfo> detectedDevices)
    {
        StorageDeviceInfo? activeDevice = SelectedDevice;
        string activeRoot = _activeScanRootPath;
        var merged = new List<StorageDeviceInfo>();

        foreach (StorageDeviceInfo detected in detectedDevices)
        {
            string detectedRoot = NormalizeRootPath(detected.RootPath);
            if (activeDevice is not null &&
                !string.IsNullOrWhiteSpace(activeRoot) &&
                string.Equals(detectedRoot, activeRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!merged.Contains(activeDevice))
                    merged.Add(activeDevice);
                continue;
            }

            merged.Add(detected);
        }

        // Windows kisa sureli bir volume-change gecisinde taranan birimi listeden dusururse bile
        // scan handle'i calisiyorsa karti koru; sonraki olayda liste tekrar dogrulanir.
        if (activeDevice is not null && !merged.Contains(activeDevice))
            merged.Insert(0, activeDevice);

        Devices.Clear();
        foreach (StorageDeviceInfo device in merged)
        {
            device.IsSelected = ReferenceEquals(device, activeDevice);
            Devices.Add(device);
        }

        if (activeDevice is not null)
            SelectedDevice = activeDevice;
    }

    private void InterruptScanForReconnect()
    {
        _pauseGate?.Resume();
        IsPaused = false;
        ApplyReconnectWaitingStatus();
        _operationCts?.Cancel();
    }

    private void ApplyReconnectWaitingStatus()
    {
        StatusTitle = "Kaynak aygıt bağlantısı bekleniyor";
        StatusDetail = "Tarama durumu kaydedildi; aynı aygıt yeniden bağlandığında kaldığı noktadan otomatik devam edilecek.";
        ProgressTitle = "Kaynak aygıt bekleniyor";
        ProgressDetail = "Aynı aygıtın yeniden bağlanması bekleniyor; doğrulandıktan sonra tarama otomatik devam edecek.";
        ProgressSpeedText = "Bağlantı bekleniyor";
        ProgressEtaText = "Aygıt bekleniyor";
    }

    private static bool IsVolumeReady(string rootPath)
    {
        try
        {
            if (PhysicalDriveAccessService.TryParsePhysicalDriveNumber(rootPath, out int physicalDriveNumber))
                return PhysicalDriveAccessService.IsAccessible(physicalDriveNumber);

            string normalized = NormalizeRootPath(rootPath);
            DriveInfo? drive = DriveInfo.GetDrives().FirstOrDefault(item =>
                string.Equals(NormalizeRootPath(item.RootDirectory.FullName), normalized, StringComparison.OrdinalIgnoreCase));
            return drive?.IsReady == true;
        }
        catch
        {
            return false;
        }
    }

    public async Task RefreshAfterMediaArrivalAsync()
    {
        if (IsRefreshingDevices || IsBusy)
            return;

        IsRefreshingDevices = true;

        try
        {
            await Task.Delay(250);
            IReadOnlyList<StorageDeviceInfo> devices = await Task.Run(_storageDeviceService.GetDevices);
            ApplyDevices(devices, true, false);
            await TryResumePendingProjectAsync();
        }
        catch (Exception ex)
        {
            StatusTitle = "Storage Enumeration Failed";
            StatusDetail = ex.Message;
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    private void ApplyDevices(IReadOnlyList<StorageDeviceInfo> detectedDevices, bool refreshed, bool forceFirstSelection)
    {
        if (forceFirstSelection)
            SetActiveNavigation("Overview");

        string? previous = SelectedDevice?.SourceKey;
        Devices.Clear();

        foreach (StorageDeviceInfo device in OrderDevicesForDisplay(detectedDevices))
            Devices.Add(device);

        if (_pendingProjectState is not null)
        {
            StorageDeviceInfo? projectDevice = FindMatchingProjectDevice(_pendingProjectState);
            foreach (StorageDeviceInfo device in Devices)
                device.IsSelected = ReferenceEquals(device, projectDevice);

            SelectedDevice = projectDevice;
            StatusTitle = projectDevice is null ? "Kayıtlı tarama • Kaynak bekleniyor" : "Kayıtlı tarama hazır";
            StatusDetail = projectDevice is null
                ? "Kayıtlı taramanın kaynak aygıtı yeniden bağlandığında işlem kaldığı noktadan devam edecek."
                : $"{projectDevice.DisplayName} proje kaynağı olarak doğrulandı.";
            return;
        }

        StorageDeviceInfo? selected = forceFirstSelection
            ? Devices.FirstOrDefault(d => d.IsReady)
            : Devices.FirstOrDefault(d => string.Equals(d.SourceKey, previous, StringComparison.OrdinalIgnoreCase))
              ?? Devices.FirstOrDefault(d => d.IsReady && d.Kind == "USB / SD Bellek")
              ?? Devices.FirstOrDefault(d => d.IsReady);

        bool sameDevice = !forceFirstSelection &&
                          selected is not null &&
                          !string.IsNullOrWhiteSpace(previous) &&
                          string.Equals(selected.SourceKey, previous, StringComparison.OrdinalIgnoreCase);

        if (selected is not null)
        {
            if (sameDevice)
            {
                foreach (StorageDeviceInfo device in Devices)
                    device.IsSelected = ReferenceEquals(device, selected);
                SelectedDevice = selected;
            }
            else
            {
                SelectDevice(selected);
            }
        }
        else
        {
            foreach (StorageDeviceInfo device in Devices)
                device.IsSelected = false;

            SelectedDevice = null;
            if (forceFirstSelection)
                ClearResults(resetFilters: true);
        }

        StatusTitle = Devices.Count == 0
            ? "Aygıt bulunamadı"
            : refreshed ? "Aygıtlar yenilendi" : "Aygıtlar hazır";
        StatusDetail = Devices.Count == 0
            ? "USB, SD kart veya disk bağlayıp Yenile'ye basın."
            : $"{Devices.Count} aktif depolama birimi algılandı.";
    }

    private static IEnumerable<StorageDeviceInfo> OrderDevicesForDisplay(IEnumerable<StorageDeviceInfo> devices)
    {
        return devices
            .OrderBy(GetDeviceDisplayGroup)
            .ThenBy(GetDriveLetterSortKey)
            .ThenBy(device => device.PhysicalDriveNumber ?? int.MaxValue)
            .ThenBy(device => device.PartitionOffsetBytes)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private static int GetDeviceDisplayGroup(StorageDeviceInfo device)
    {
        if (TryGetMountedDriveLetter(device, out _))
            return 0;
        if (device.IsPartitionSource)
            return 1;
        if (device.IsWholePhysicalDisk)
            return 2;
        return 3;
    }

    private static int GetDriveLetterSortKey(StorageDeviceInfo device)
    {
        return TryGetMountedDriveLetter(device, out char letter)
            ? char.ToUpperInvariant(letter) - 'A'
            : 1000;
    }

    private static bool TryGetMountedDriveLetter(StorageDeviceInfo device, out char letter)
    {
        letter = '\0';
        string root = (device.RootPath ?? string.Empty).Trim();
        if (root.Length < 2 || root[1] != ':' || !char.IsLetter(root[0]))
            return false;

        letter = char.ToUpperInvariant(root[0]);
        return true;
    }

    private void SelectDevice(object? parameter)
    {
        if (parameter is not StorageDeviceInfo selected)
            return;

        foreach (StorageDeviceInfo device in Devices)
            device.IsSelected = ReferenceEquals(device, selected);

        _isFolderScanSession = false;
        _folderScanPath = string.Empty;
        SelectedDevice = selected;
        ClearResults(resetFilters: true);

        StatusTitle = selected.IsReady ? "Kaynak Aygıt • Bağlandı" : "Kaynak Aygıt • Kullanılamıyor";
        StatusDetail = selected.IsReady
            ? $"{selected.DisplayName} • {selected.FileSystem} • {RecoveryFileItem.FormatBytes(selected.TotalBytes)}" +
              (selected.HasHealthInfo ? $" • {selected.HealthText}" : string.Empty)
            : "Aygıt Windows tarafından hazır olarak raporlanmıyor.";
    }

    private void ReturnToDeviceSelection()
    {
        _isFolderScanSession = false;
        _isUnifiedLostDataScan = false;
        _folderScanPath = string.Empty;
        _activeScanRootPath = string.Empty;
        SetActiveNavigation("Overview");
        ClearResults(resetFilters: true);
        OnPropertyChanged(nameof(FolderScanPath));
        OnPropertyChanged(nameof(IsRecoveryWorkspace));
        OnPropertyChanged(nameof(IsLandingWorkspace));
    }

    private void SetScanMode(object? parameter)
    {
        string mode = parameter?.ToString() ?? string.Empty;
        bool deep = mode.Equals("Deep", StringComparison.OrdinalIgnoreCase);
        _isFolderScanSession = false;
        _isUnifiedLostDataScan = false;
        ScanMode = deep ? ScanMode.Deep : ScanMode.Quick;
        SetActiveNavigation(deep ? "Deep" : "Quick");
    }

    private void SetQuickScanScope(object? parameter)
    {
        string value = parameter?.ToString() ?? "All";
        QuickScanScope = value.ToUpperInvariant() switch
        {
            "PHOTO" => DeepScanTarget.Photo,
            "VIDEO" => DeepScanTarget.Video,
            "DOCUMENT" => DeepScanTarget.Document,
            _ => DeepScanTarget.All
        };

        _isFolderScanSession = false;
        _isUnifiedLostDataScan = false;
        ScanMode = ScanMode.Quick;
        SetActiveNavigation("Quick");
        StatusTitle = $"Hızlı Tarama • {QuickScanScopeTitle}";
        StatusDetail = "Seçim hatırlandı. Hızlı Tarama düğmesine tıklayınca seçili aygıt doğrudan taranır.";
    }

    private async Task StartQuickScanAsync()
    {
        StorageDeviceInfo? selectedDevice = SelectedDevice;
        if (selectedDevice?.IsReady != true || IsBusy)
            return;

        StorageDeviceInfo requestedDevice = selectedDevice;
        if (!TryResolveQuickScanTarget(requestedDevice, out StorageDeviceInfo quickTarget, out string resolutionDetail))
        {
            StatusTitle = "Hızlı Tarama • Bölüm Seçimi Gerekli";
            StatusDetail = resolutionDetail;
            MessageBox.Show(
                L(resolutionDetail),
                L("Hızlı Tarama • NSX Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ReferenceEquals(quickTarget, requestedDevice))
        {
            SelectDevice(quickTarget);
            StatusTitle = "Hızlı Tarama • Bölüm Otomatik Eşleştirildi";
            StatusDetail = resolutionDetail;
        }

        _isFolderScanSession = false;
        _isUnifiedLostDataScan = false;
        ScanMode = ScanMode.Quick;
        SetActiveNavigation("Quick");
        StatusTitle = $"Hızlı Tarama • {QuickScanScopeTitle}";
        StatusDetail = string.IsNullOrWhiteSpace(resolutionDetail)
            ? $"{SelectedDevice!.DisplayName} • {QuickScanScopeTitle} filtresiyle tarama başlatılıyor."
            : $"{resolutionDetail} • {QuickScanScopeTitle} filtresiyle metadata taraması başlatılıyor.";
        await StartScanAsync(resumeExisting: false);
    }

    private bool TryResolveQuickScanTarget(
        StorageDeviceInfo selected,
        out StorageDeviceInfo target,
        out string detail)
    {
        target = selected;
        detail = string.Empty;

        if (!selected.IsWholePhysicalDisk)
        {
            if (IsQuickScanFileSystemSupported(selected.FileSystem))
            {
                // A mounted drive-letter source must stay that exact source. Automatically
                // switching D:/E:/USB to a PhysicalDrive partition view makes the UI appear to
                // scan another disk and also couples normal scans to physical topology IOCTLs.
                // Physical RAW views remain available for explicit whole-disk/lost-partition flows.
                target = selected;
                return true;
            }

            // A damaged legacy volume may be reported as RAW/unknown by Windows even though its
            // MBR entry and recoverable NTFS/FAT metadata still exist on the physical disk. Do
            // not reject Quick Scan at the drive-letter layer; resolve the physical partition
            // table first and let the filesystem-specific metadata rescue validate it.
            if (selected.PhysicalDriveNumber is int &&
                _storageDeviceService.TryCreateWholePhysicalDevice(selected, out StorageDeviceInfo? wholePhysical) &&
                wholePhysical is not null &&
                _storageDeviceService.TryResolveQuickScanPartition(wholePhysical, out StorageDeviceInfo? rawResolved, out string rawResolveDetail) &&
                rawResolved is not null)
            {
                target = rawResolved;
                detail = $"{selected.DisplayName} Windows tarafından {selected.FileSystem} olarak raporlandı; ancak fiziksel partition metadata'sı bulundu. {rawResolveDetail}";
                return true;
            }

            if (NtfsQuickScanService.IsLegacyRotationalQuickPath(selected))
            {
                target = selected;
                detail = $"{selected.DisplayName} dosya sistemi metadata olarak doğrulanamadı; eski dönel disk için salt-okunur Legacy Surface Quick doğrudan seçili kaynakta başlatılacak.";
                return true;
            }

            detail = $"{selected.DisplayName} dosya sistemi {selected.FileSystem} olarak görünüyor ve fiziksel partition metadata'sı da doğrulanamadı. Hızlı Tarama için kullanılabilir NTFS/FAT32/exFAT metadata bulunamadı; Derin Tarama kullanılabilir.";
            return false;
        }

        if (selected.PhysicalDriveNumber is not int physicalDriveNumber)
        {
            detail = "Fiziksel disk numarası doğrulanamadı. Hızlı Tarama için NTFS/FAT32/exFAT bölümünü seçin.";
            return false;
        }

        List<StorageDeviceInfo> candidates = Devices
            .Where(device => device.IsReady &&
                             !device.IsWholePhysicalDisk &&
                             device.PhysicalDriveNumber == physicalDriveNumber &&
                             IsQuickScanFileSystemSupported(device.FileSystem) &&
                             device.TotalBytes >= 16L * 1024 * 1024)
            .GroupBy(device => device.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(device => device.TotalBytes)
            .ToList();

        if (candidates.Count == 0)
        {
            if (_storageDeviceService.TryResolveQuickScanPartition(selected, out StorageDeviceInfo? resolved, out string resolveDetail) &&
                resolved is not null)
            {
                target = resolved;
                detail = resolveDetail;
                return true;
            }

            if (NtfsQuickScanService.IsLegacyRotationalQuickPath(selected))
            {
                target = selected;
                detail = string.IsNullOrWhiteSpace(resolveDetail)
                    ? "Eski dönel diskte partition metadata doğrulanamadı; salt-okunur Legacy Surface Quick tüm fiziksel kaynakta başlatılacak."
                    : $"{resolveDetail} Metadata kurtarılamadı; salt-okunur Legacy Surface Quick tüm fiziksel kaynakta başlatılacak.";
                return true;
            }

            detail = string.IsNullOrWhiteSpace(resolveDetail)
                ? "Bu fiziksel diskte Hızlı Tarama için doğrulanmış NTFS/FAT32/exFAT bölüm bulunamadı. Kayıp veri için Evrensel/Derin Tarama ile RAW taramaya devam edin."
                : $"{resolveDetail} Kayıp veri için Evrensel/Derin Tarama ile RAW taramaya devam edin.";
            return false;
        }

        List<StorageDeviceInfo> mounted = candidates.Where(device => !device.IsPartitionSource).ToList();
        if (mounted.Count == 1)
        {
            StorageDeviceInfo mountedCandidate = mounted[0];
            bool remainingAreSmallSystemPartitions = candidates
                .Where(device => !ReferenceEquals(device, mountedCandidate))
                .All(device => device.TotalBytes <= 2L * 1024 * 1024 * 1024 ||
                               device.TotalBytes <= mountedCandidate.TotalBytes / 20);

            if (remainingAreSmallSystemPartitions)
            {
                target = mountedCandidate;
                detail = $"{selected.DisplayName} için Hızlı Tarama kaynağı otomatik olarak {target.DisplayName} ({target.FileSystem}) bölümüne bağlandı.";
                return true;
            }
        }

        if (candidates.Count == 1)
        {
            target = candidates[0];
            detail = $"{selected.DisplayName} için Hızlı Tarama kaynağı otomatik olarak {target.DisplayName} ({target.FileSystem}) bölümüne bağlandı.";
            return true;
        }

        detail = $"PhysicalDrive{physicalDriveNumber} üzerinde {candidates.Count:N0} ayrı hızlı-taranabilir bölüm bulundu. Yanlış bölümü taramamak için önce cihaz listesinden istediğiniz sürücü/bölüm kartını seçin.";
        return false;
    }

    private static bool IsQuickScanFileSystemSupported(string? fileSystem) =>
        PortableQuickScanPolicy.IsQuickScanFileSystemSupported(fileSystem);

    private void SetDeepScanScope(object? parameter)
    {
        string value = parameter?.ToString() ?? string.Empty;
        DeepScanScope = value.ToUpperInvariant() switch
        {
            "PHOTO" => DeepScanTarget.Photo,
            "VIDEO" => DeepScanTarget.Video,
            "DOCUMENT" => DeepScanTarget.Document,
            "ALL" => DeepScanTarget.All,
            _ => DeepScanTarget.None
        };

        ScanMode = ScanMode.Deep;
        SetActiveNavigation("Deep");
        StatusTitle = $"Derin Tarama • {DeepScanScopeTitle}";
        StatusDetail = ScanModeDescription;
    }

    private void SetFolderScanScope(object? parameter)
    {
        string value = parameter?.ToString() ?? string.Empty;
        FolderScanScope = value.ToUpperInvariant() switch
        {
            "PHOTO" => DeepScanTarget.Photo,
            "VIDEO" => DeepScanTarget.Video,
            "DOCUMENT" => DeepScanTarget.Document,
            "ALL" => DeepScanTarget.All,
            _ => DeepScanTarget.None
        };

        SetActiveNavigation("Folder");
        StatusTitle = $"Klasör Tara • {FolderScanScopeTitle}";
        StatusDetail = FolderScanScope == DeepScanTarget.None
            ? "Resim, Video veya Dosya türlerinden en az birini seçin."
            : "Tür seçimi hazır. Klasör Seç ve Tara ile hedef klasörü belirleyin.";
    }

    public void ActivateFolderScanNavigation() => SetActiveNavigation("Folder");

    private async Task BrowseAndStartFolderScanAsync()
    {
        SetActiveNavigation("Folder");

        if (FolderScanScope == DeepScanTarget.None)
            FolderScanScope = DeepScanTarget.All;

        var dialog = new OpenFolderDialog
        {
            Title = L("Taranacak klasörü seçin"),
            Multiselect = false
        };

        if (SelectedDevice?.IsReady == true && Directory.Exists(SelectedDevice.RootPath))
            dialog.InitialDirectory = SelectedDevice.RootPath;
        else
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        string folderPath = Path.GetFullPath(dialog.FolderName);
        await StartKnownFolderScanAsync(folderPath);
    }

    private async Task StartRecycleBinScanAsync()
    {
        string root = SelectedDevice?.RootPath
            ?? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
            ?? @"C:\";
        string recyclePath = Path.Combine(root, "$Recycle.Bin");
        await StartKnownFolderScanAsync(recyclePath);
    }

    private async Task StartKnownFolderScanAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            MessageBox.Show(
                L("Seçilen konum bulunamadı veya erişilemiyor."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (FolderScanScope == DeepScanTarget.None)
            FolderScanScope = DeepScanTarget.All;

        string normalizedFolderPath = Path.GetFullPath(folderPath);
        string? folderRoot = Path.GetPathRoot(normalizedFolderPath);
        if (string.IsNullOrWhiteSpace(folderRoot))
            return;

        StorageDeviceInfo? device = Devices.FirstOrDefault(item =>
            item.IsReady &&
            string.Equals(
                NormalizeRootPath(item.RootPath),
                NormalizeRootPath(folderRoot),
                StringComparison.OrdinalIgnoreCase));

        if (device is null)
        {
            await RefreshDevicesAsync();
            device = Devices.FirstOrDefault(item =>
                item.IsReady &&
                string.Equals(
                    NormalizeRootPath(item.RootPath),
                    NormalizeRootPath(folderRoot),
                    StringComparison.OrdinalIgnoreCase));
        }

        if (device is null)
        {
            MessageBox.Show(
                L("Seçilen konumun bulunduğu aygıt algılanamadı."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(device.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                L("Klasör odaklı silinmiş dosya kurtarma NTFS birimlerinde kullanılabilir. FAT32/exFAT aygıtlarda Derin Tarama kullanın."),
                L("Klasör Tara"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        foreach (StorageDeviceInfo item in Devices)
            item.IsSelected = ReferenceEquals(item, device);
        SelectedDevice = device;

        _folderScanPath = normalizedFolderPath;
        _isUnifiedLostDataScan = false;
        _isFolderScanSession = true;
        await StartFolderScanAsync(normalizedFolderPath, resumeExisting: false);
    }

    private async Task StartFolderScanAsync(string folderPath, bool resumeExisting)
    {
        StorageDeviceInfo? device = SelectedDevice;
        if (device is null || !device.IsReady)
            return;

        if (FolderScanScope == DeepScanTarget.None)
            return;

        if (!ElevationService.EnsureAdministratorForRawAccess())
            return;

        _activeScanRootPath = NormalizeRootPath(device.RootPath);
        _isFolderScanSession = true;
        _folderScanPath = folderPath;
        SetActiveNavigation("Folder");

        if (!resumeExisting)
        {
            _expandedNavigationGroups.Clear();
            ClearResults(resetFilters: false);
            SelectedRecoveryFile = null;
            _scanResumePosition = 0;
            _scanResumeTotal = 0;
            _scanCheckpoint = null;
            _projectId = Guid.NewGuid().ToString("N");
            _currentProjectPath = null;
        }
        else
        {
            _resumeReplayInProgress = true;
            _resumePositionFloor = Math.Max(0, _scanResumePosition);
            _resumePercentFloor = Math.Clamp(ProgressPercent, 0d, 100d);
        }

        _scanResumeAvailable = true;
        BeginOperation(
            resumeExisting ? "Klasör taraması yeniden doğrulanıyor" : "Klasör taraması hazırlanıyor",
            $"{folderPath} • NTFS klasör zinciri salt-okunur incelenecek.");
        if (resumeExisting)
        {
            ProgressPercent = _resumePercentFloor;
            ProgressFoundText = $"{RecoveryFiles.Count:N0} dosya";
        }
        IsScanRunning = true;
        _deferScanResultOrganization = true;
        IsOrganizingResults = false;
        _acceptLiveScanResults = true;
        bool notifyScanCompleted = false;

        try
        {
            CancellationToken token = _operationCts!.Token;
            var progress = new Progress<OperationProgress>(ApplyProgress);

            ScanReport? report = await Task.Run(() =>
            {
                try
                {
                    return _ntfsFolderScanService.Scan(
                        device,
                        folderPath,
                        FolderScanScope,
                        progress,
                        _pauseGate,
                        token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            });

            _acceptLiveScanResults = false;

            if (token.IsCancellationRequested || report is null)
            {
                _scanResumeAvailable = true;
                if (_scanDisconnectInterruptRequested || _scanWaitingForReconnect)
                {
                    ApplyReconnectWaitingStatus();
                    return;
                }

                StatusTitle = "Klasör taraması durduruldu";
                StatusDetail = "Bulunan dosyalar korunuyor; kaynak diskte herhangi bir değişiklik yapılmadı.";
                ProgressTitle = "Klasör taraması durduruldu";
                ProgressDetail = $"Bulunan dosya: {RecoveryFiles.Count:N0}";
                ProgressFoundText = $"{RecoveryFiles.Count:N0} dosya";
                ProgressEtaText = "—";
                return;
            }

            IReadOnlyList<RecoveryFileItem> organizedFiles = await OrganizeScanResultsAsync(device, report.Files, token);
            report = report with { Files = organizedFiles };
            await CommitOrganizedResultsAsync(report.Files);
            IsOrganizingResults = false;
            _scanResumeAvailable = false;
            _scanResumePosition = _scanResumeTotal;

            StatusTitle = "Klasör taraması tamamlandı";
            StatusDetail = report.Summary;
            ProgressTitle = "Klasör taraması tamamlandı";
            ProgressDetail = $"Doğrulanan dosya: {report.Files.Count:N0} • seçili klasör kapsamı tamamlandı.";
            ProgressPercent = 100;
            ProgressFoundText = $"{report.Files.Count:N0} dosya";
            ProgressEtaText = "Tamamlandı";

            if (report.Files.Count > 0)
            {
                SelectedRecoveryFile = report.Files[0];
                report.Files[0].IsChecked = true;
                QueueAutomaticPreviews(report.Files);
            }

            notifyScanCompleted = true;
        }
        catch (Exception ex)
        {
            _scanResumeAvailable = true;
            if (!IsVolumeReady(device.RootPath))
            {
                RecoveryProjectState? disconnectState = BuildProjectState(forceResume: true);
                if (disconnectState is not null)
                {
                    _pendingProjectState = disconnectState;
                    PersistKnownProjectCheckpoint(disconnectState);
                }

                _scanWaitingForReconnect = true;
                _scanDisconnectInterruptRequested = true;
                AppLog.Error("Klasor taramasi sirasinda kaynak aygit baglantisi kesildi; otomatik yeniden baglanma bekleniyor.", ex);
                ApplyReconnectWaitingStatus();
            }
            else
            {
                AppLog.Error("Klasör taraması tamamlanamadı.", ex);
                StatusTitle = "Klasör taraması tamamlanamadı";
                StatusDetail = ex.Message;
                ProgressTitle = "Klasör tarama hatası";
                ProgressDetail = ex.Message;
                MessageBox.Show(
                    L(ex.Message),
                    L("NSX Veri Kurtarma Pro"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            bool waitingForReconnect = _scanWaitingForReconnect;
            _acceptLiveScanResults = false;
            FlushDeferredResultOrganization();
            IsOrganizingResults = false;
            IsScanRunning = false;
            EndOperation();
            _resumeReplayInProgress = false;
            _resumePositionFloor = 0;
            _resumePercentFloor = 0d;
            if (!waitingForReconnect)
            {
                _activeScanRootPath = string.Empty;
                _scanDisconnectInterruptRequested = false;
            }

            if (notifyScanCompleted)
                ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string NormalizeRootPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (PhysicalDriveAccessService.TryParsePhysicalDriveNumber(value, out _))
            return value.Trim();

        string full = Path.GetFullPath(value);
        string? root = Path.GetPathRoot(full);
        return (root ?? full).TrimEnd('\\') + "\\";
    }

    private static DeepScanTarget NormalizeQuickScanScope(DeepScanTarget value) => value switch
    {
        DeepScanTarget.Photo => DeepScanTarget.Photo,
        DeepScanTarget.Video => DeepScanTarget.Video,
        DeepScanTarget.Document => DeepScanTarget.Document,
        _ => DeepScanTarget.All
    };

    private static bool MatchesQuickScanScope(RecoveryFileItem file, DeepScanTarget scope) => scope switch
    {
        DeepScanTarget.Photo => file.Category == "Fotoğraf",
        DeepScanTarget.Video => file.Category == "Video",
        DeepScanTarget.Document => file.Category == "Belge",
        _ => true
    };

    private ScanReport ApplyQuickScanScope(ScanReport report)
    {
        if (ScanMode != ScanMode.Quick || QuickScanScope == DeepScanTarget.All)
            return report;

        List<RecoveryFileItem> filtered = report.Files
            .Where(file => MatchesQuickScanScope(file, QuickScanScope))
            .ToList();

        return report with
        {
            Files = filtered,
            Summary = $"Hızlı Tarama • {QuickScanScopeTitle} • {filtered.Count:N0} dosya doğrulandı."
        };
    }

    private static string QuickScanScopePreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NSX Yazılım",
        "NSX Veri Kurtarma Pro",
        "quick-scan-scope.txt");

    private static DeepScanTarget LoadQuickScanScopePreference()
    {
        try
        {
            string path = QuickScanScopePreferencePath;
            if (File.Exists(path) && Enum.TryParse(File.ReadAllText(path).Trim(), true, out DeepScanTarget value))
                return NormalizeQuickScanScope(value);
        }
        catch
        {
            // UI tercihi okunamazsa tarama motoru etkilenmez; güvenli varsayılan Tümü'dür.
        }

        return DeepScanTarget.All;
    }

    private static void SaveQuickScanScopePreference(DeepScanTarget value)
    {
        try
        {
            string path = QuickScanScopePreferencePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, NormalizeQuickScanScope(value).ToString());
        }
        catch
        {
            // Tercih dosyası yazılamasa da aktif oturumdaki seçim korunur.
        }
    }

    private async Task StartUnifiedLostDataScanAsync()
    {
        if (SelectedDevice?.IsReady != true || IsBusy)
            return;

        // Windows "biçimlendir" dedigi RAW/hasarli USB/SD/HDD'yi drive-letter olarak
        // acamayabilir ve UI'da yalnizca whole PhysicalDrive gorunur. Unified scan baslarken
        // guvenli tek veri bolumu partition tablosu/backup VBR'den bulunabiliyorsa ayni
        // fiziksel aygitin partition-view'una otomatik gec. Bu kritik: NTFS DataRun/FAT cluster
        // offsetleri partition-relative kalir ve bulunan dosyalar daha sonra dogru yerden kurtarilir.
        string automaticSourceDetail = string.Empty;
        StorageDeviceInfo requestedDevice = SelectedDevice;
        if (requestedDevice.IsWholePhysicalDisk)
        {
            // Partition/VBR probing raw PhysicalDrive okumalari yapar. Bunu UI thread'inde
            // calistirmak bozuk USB controller'larda pencerenin "dondu" sanilmasina yol acar.
            // Yetkiyi once al, ardindan sadece kisa kaynak-kesif isini worker thread'e tasi.
            if (!ElevationService.EnsureAdministratorForRawAccess())
                return;

            StatusTitle = "Kayıp Veri Taraması • Kaynak yapısı analiz ediliyor";
            StatusDetail = $"{requestedDevice.DisplayName} • partition tablosu, primary/backup VBR ve dosya sistemi imzası salt-okunur doğrulanıyor.";

            (bool Resolved, StorageDeviceInfo? Partition, string Detail) resolution = await Task.Run(() =>
            {
                bool resolved = _storageDeviceService.TryResolveUnifiedScanPartition(
                    requestedDevice,
                    out StorageDeviceInfo? partition,
                    out string detail);
                return (resolved, partition, detail);
            });

            if (resolution.Resolved && resolution.Partition is not null)
            {
                SelectDevice(resolution.Partition);
                automaticSourceDetail = resolution.Detail;
            }
        }

        // V5 public scan contract: one button, one automatic pipeline. Legacy Quick/Deep/Folder
        // entry points remain only for older .nsx project compatibility. New scans always use
        // the full metadata + historical path + RAW pipeline for every supported file class.
        _isFolderScanSession = false;
        _folderScanPath = string.Empty;
        _isUnifiedLostDataScan = true;
        ScanMode = ScanMode.Deep;
        DeepScanScope = DeepScanTarget.All;
        SetActiveNavigation("Deep");

        StatusTitle = "Kayıp Veri Taraması • Evrensel Mod";
        StatusDetail = string.IsNullOrWhiteSpace(automaticSourceDetail)
            ? "HDD, SSD/NVMe, USB, SD/microSD, harici disk ve kamera depolaması tek akışta taranır. NTFS/exFAT/FAT12/FAT16/FAT32 metadata otomatik algılanır; tanınmayan/RAW kaynaklarda tam imza taraması kesintisiz devam eder."
            : automaticSourceDetail;
        await StartScanAsync(resumeExisting: false);
    }

    private Task StartSelectedScanAsync() =>
        ScanMode == ScanMode.Quick
            ? StartQuickScanAsync()
            : StartScanAsync(resumeExisting: false);

    private Task StartScanAsync() => StartScanAsync(resumeExisting: false);

    private async Task StartScanAsync(bool resumeExisting)
    {
        if (!resumeExisting)
            _isFolderScanSession = false;
        SetActiveNavigation(ScanMode == ScanMode.Deep ? "Deep" : "Quick");
        StorageDeviceInfo? device = SelectedDevice;
        if (device is null || !device.IsReady)
            return;

        if (ScanMode == ScanMode.Deep && DeepScanScope == DeepScanTarget.None)
        {
            StatusTitle = "Deep Scan • Target Class Missing";
            StatusDetail = "Deep Scan pipeline için en az bir target data class tanımlanmalıdır.";
            MessageBox.Show(
                L("Derin taramayı başlatmak için en az bir tarama türü seçin."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ElevationService.EnsureAdministratorForRawAccess())
            return;

        _activeScanRootPath = NormalizeRootPath(device.RootPath);
        string fileSystem = (device.FileSystem ?? string.Empty).Trim().ToUpperInvariant();
        bool quickResumeIsExact = ScanMode != ScanMode.Quick ||
                                  fileSystem == "NTFS" ||
                                  (fileSystem is not ("FAT" or "FAT12" or "FAT16" or "FAT32" or "EXFAT"));
        bool quickSurfaceResume = resumeExisting && ScanMode == ScanMode.Quick && IsQuickSurfaceCheckpoint(_scanCheckpoint);
        List<RecoveryFileItem>? seedResults = resumeExisting && quickResumeIsExact
            ? RecoveryFiles.ToList()
            : null;
        long resumePosition = resumeExisting && quickResumeIsExact
            ? Math.Max(0, _scanResumePosition)
            : 0;
        _resumeReplayInProgress = resumeExisting && (!quickResumeIsExact || quickSurfaceResume);
        _resumePositionFloor = _resumeReplayInProgress ? Math.Max(0, _scanResumePosition) : 0;
        _resumePercentFloor = _resumeReplayInProgress ? Math.Clamp(ProgressPercent, 0d, 100d) : 0d;

        if (!resumeExisting)
        {
            _expandedNavigationGroups.Clear();
            ClearResults(resetFilters: false);
            SelectedRecoveryFile = null;
            _scanResumePosition = 0;
            _scanResumeTotal = ScanMode == ScanMode.Deep ? device.TotalBytes : 0;
            _scanCheckpoint = null;
            _projectId = Guid.NewGuid().ToString("N");
            _currentProjectPath = null;
        }

        _scanResumeAvailable = true;
        RecoveryMediaProfile mediaProfile = RecoveryMediaProfileService.Create(device);
        string trimNotice = mediaProfile.TrimAware && mediaProfile.TrimEnabled == true
            ? " • TRIM aktif: silinmiş bloklar denetleyici tarafından önceden sıfırlanmış olabilir; bulunan fiziksel veri yine tam taranır."
            : string.Empty;
        string cameraNotice = mediaProfile.CameraOptimized
            ? _isUnifiedLostDataScan
                ? " • Kamera/AVCHD kapsayıcıları hızlı tek geçişte aranır; ağır fragment onarımı Önizle/Onar aşamasına bırakılır."
                : " • Kamera/AVCHD öncelikleri ve ileri video reconstruction aktif."
            : string.Empty;
        string healthNotice = mediaProfile.SafeScanPreferred
            ? _isUnifiedLostDataScan
                ? $" • {device.HealthText}: {device.HealthSummary} • Kayıp veri taraması medya sağlığı nedeniyle 1 MB güvenli okuma penceresinde çalışacak."
                : ScanMode == ScanMode.Deep
                    ? $" • {device.HealthText}: {device.HealthSummary} • Derin Tarama başlangıçtan itibaren 1 MB güvenli okuma penceresine kilitlendi."
                    : $" • {device.HealthText}: {device.HealthSummary} • Hızlı Tarama opsiyonel USN önceliğini atlayıp doğrudan dosya sistemi metadata kayıtlarına geçecek."
            : device.SmartAvailable
                ? $" • {device.HealthText}."
                : string.Empty;
        double restoredResumePercent = resumeExisting ? Math.Clamp(ProgressPercent, 0d, 100d) : 0d;
        string operationTitle = _isUnifiedLostDataScan
            ? resumeExisting ? "Kayıp veri taraması kaldığı noktadan hazırlanıyor" : "Kayıp veri taraması hazırlanıyor"
            : resumeExisting
                ? (ScanMode == ScanMode.Quick ? "Hızlı tarama kaldığı noktadan hazırlanıyor" : "Derin tarama kaldığı noktadan hazırlanıyor")
                : (ScanMode == ScanMode.Quick ? "Hızlı tarama hazırlanıyor" : "Derin tarama hazırlanıyor");

        BeginOperation(
            operationTitle,
            resumeExisting
                ? $"{device.DisplayName} • kayıtlı tarama konumu doğrulanıyor • {mediaProfile.DiagnosticText}.{cameraNotice}{trimNotice}{healthNotice}"
                : $"{device.DisplayName} • kaynak disk güvenli salt-okunur modda hazırlanıyor • tüm dosya türleri + otomatik dosya sistemi metadata + ham veri tek akışta analiz edilecek • {mediaProfile.DiagnosticText}.{cameraNotice}{trimNotice}{healthNotice}");
        if (resumeExisting)
        {
            ProgressPercent = restoredResumePercent;
            ProgressFoundText = $"{RecoveryFiles.Count:N0} dosya";
        }
        IsScanRunning = true;
        _deferScanResultOrganization = true;
        IsOrganizingResults = false;
        _acceptLiveScanResults = !resumeExisting || quickResumeIsExact;
        bool notifyScanCompleted = false;

        try
        {
            CancellationToken token = _operationCts!.Token;
            var progress = new Progress<OperationProgress>(ApplyProgress);

            ScanReport? report = await Task.Run(() =>
            {
                try
                {
                    return ScanMode == ScanMode.Quick
                        ? _quickScanService.Scan(
                            device,
                            progress,
                            _pauseGate,
                            token,
                            resumePosition,
                            seedResults,
                            QuickScanScope,
                            resumeExisting ? _scanCheckpoint : null)
                        : _deepScanService.Scan(
                            device,
                            progress,
                            _pauseGate,
                            token,
                            resumePosition,
                            seedResults,
                            DeepScanScope,
                            skipMetadataStage: false,
                            resumeCheckpoint: resumeExisting ? _scanCheckpoint : null,
                            unifiedFastScan: _isUnifiedLostDataScan);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            });

            _acceptLiveScanResults = false;
            if (report is not null)
            {
                MergeHistoricalFolderPaths(report.HistoricalFolders);
                report = ApplyQuickScanScope(report);
                if (_isUnifiedLostDataScan)
                    report = report with { Summary = NormalizeUnifiedScanText(report.Summary) };
            }

            if (token.IsCancellationRequested || report is null)
            {
                if (report is not null && report.Files.Count > 0)
                    ReplaceResults(report.Files);

                _scanResumeAvailable = CanResumeInterruptedScan();
                if (_scanDisconnectInterruptRequested || _scanWaitingForReconnect)
                {
                    ApplyReconnectWaitingStatus();
                    return;
                }

                StatusTitle = "Tarama durduruldu";
                StatusDetail = "Tarama durduruldu. Kaynak diskte herhangi bir değişiklik yapılmadı.";
                ProgressTitle = "Tarama durduruldu";
                ProgressDetail = $"Bulunan dosya: {RecoveryFiles.Count:N0}";
                ProgressFoundText = $"{RecoveryFiles.Count:N0} dosya";
                ProgressEtaText = "—";

                if (RecoveryFiles.Count > 0)
                    SelectedRecoveryFile = RecoveryFiles[0];

                return;
            }

            IReadOnlyList<RecoveryFileItem> organizedFiles = await OrganizeScanResultsAsync(device, report.Files, token);
            report = report with { Files = organizedFiles };
            await CommitOrganizedResultsAsync(report.Files);
            IsOrganizingResults = false;
            _scanResumeAvailable = false;
            if (_scanResumeTotal > 0)
                _scanResumePosition = _scanResumeTotal;

            StatusTitle = "Tarama tamamlandı";
            StatusDetail = report.Summary;
            ProgressTitle = "Tarama tamamlandı";
            ProgressDetail = BuildCompletedScanDiagnosticText(report);
            ProgressPercent = 100;
            ProgressFoundText = $"{report.Files.Count:N0} dosya";
            ProgressEtaText = "Tamamlandı";

            if (report.Files.Count > 0)
            {
                SelectedRecoveryFile = report.Files[0];
                report.Files[0].IsChecked = true;
            }


            notifyScanCompleted = true;
        }
        catch (OperationCanceledException)
        {
            _scanResumeAvailable = CanResumeInterruptedScan();
            if (_scanDisconnectInterruptRequested || _scanWaitingForReconnect)
            {
                ApplyReconnectWaitingStatus();
            }
            else
            {
                StatusTitle = "Tarama iptal edildi";
                StatusDetail = "Kaynak diskte herhangi bir değişiklik yapılmadı.";
                ProgressTitle = "İşlem iptal edildi";
                ProgressDetail = "Tarama kullanıcı isteğiyle durduruldu.";
            }

            if (RecoveryFiles.Count > 0)
                SelectedRecoveryFile = RecoveryFiles[0];
        }
        catch (Exception ex)
        {
            _scanResumeAvailable = CanResumeInterruptedScan();
            if (!IsVolumeReady(device.RootPath))
            {
                RecoveryProjectState? disconnectState = BuildProjectState(forceResume: true);
                if (disconnectState is not null)
                {
                    _pendingProjectState = disconnectState;
                    PersistKnownProjectCheckpoint(disconnectState);
                }

                _scanWaitingForReconnect = true;
                _scanDisconnectInterruptRequested = true;
                AppLog.Error("Tarama sirasinda kaynak aygit baglantisi kesildi; otomatik yeniden baglanma bekleniyor.", ex);
                ApplyReconnectWaitingStatus();
            }
            else
            {
                AppLog.Error("Tarama tamamlanamadı.", ex);
                StatusTitle = "Tarama tamamlanamadı";
                StatusDetail = ex.Message;
                ProgressTitle = "Tarama hatası";
                ProgressDetail = ex.Message;
                MessageBox.Show(
                    L(ex.Message),
                    L("NSX Veri Kurtarma Pro"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            bool waitingForReconnect = _scanWaitingForReconnect;
            _acceptLiveScanResults = false;
            FlushDeferredResultOrganization();
            IsOrganizingResults = false;
            IsScanRunning = false;
            EndOperation();
            _resumeReplayInProgress = false;
            _resumePositionFloor = 0;
            _resumePercentFloor = 0d;
            if (!waitingForReconnect)
            {
                _activeScanRootPath = string.Empty;
                _scanDisconnectInterruptRequested = false;
            }

            if (notifyScanCompleted)
                ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PersistKnownProjectCheckpoint(RecoveryProjectState state)
    {
        if (string.IsNullOrWhiteSpace(_currentProjectPath))
            return;

        try
        {
            _projectService.Save(_currentProjectPath, state);
        }
        catch (Exception ex)
        {
            AppLog.Error("NSX project checkpoint could not be persisted after source disconnect.", ex);
        }
    }

    private bool CanResumeInterruptedScan()
    {
        if (ScanMode == ScanMode.Deep)
            return _scanCheckpoint is not null || _scanResumeTotal <= 0 || _scanResumePosition < _scanResumeTotal;

        if (ScanMode == ScanMode.Quick && !_isFolderScanSession && _scanCheckpoint is not null)
        {
            if (IsQuickSurfaceCheckpoint(_scanCheckpoint))
                return !_scanCheckpoint.RawScanCompleted || _scanCheckpoint.ResumeTotal <= 0 || _scanCheckpoint.ResumePosition < _scanCheckpoint.ResumeTotal;

            if (IsQuickMetadataCheckpoint(_scanCheckpoint))
                return true;
        }

        return _scanResumeTotal <= 0 || _scanResumePosition < _scanResumeTotal;
    }

    private async Task CreateImageAsync()
    {
        SetActiveNavigation("Image");
        StorageDeviceInfo? device = SelectedDevice;
        if (device is null || !device.IsReady)
            return;

        if (!ElevationService.EnsureAdministratorForRawAccess())
            return;

        var dialog = new SaveFileDialog
        {
            Title = L("Disk imajını farklı bir fiziksel diske kaydedin"),
            Filter = L("Disk İmajı (*.img)|*.img|Tüm Dosyalar (*.*)|*.*"),
            AddExtension = true,
            DefaultExt = ".img",
            FileName = $"NSX_Image_{BuildImageSourceName(device)}_{DateTime.Now:yyyyMMdd_HHmm}.img"
        };

        if (dialog.ShowDialog() != true)
            return;

        DestinationSafetyCheck imageDestinationSafety = CheckDestinationSafety(device, dialog.FileName);
        if (!imageDestinationSafety.IsSafe)
        {
            MessageBox.Show(
                L(imageDestinationSafety.Message),
                L("Güvenli Kurtarma"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        BeginOperation(
            "Disk İmajı Hazırlanıyor",
            "Kaynak salt okunur aktarılacak; geçerli bir NSX checkpoint bulunursa imaj güvenli noktadan sürdürülecek.");

        try
        {
            CancellationToken token = _operationCts!.Token;
            var progress = new Progress<OperationProgress>(ApplyProgress);

            DiskImageReport report = await Task.Run(
                () => _diskImageService.CreateImage(device, dialog.FileName, progress, _pauseGate, token),
                token);

            StatusTitle = report.AlreadyCompleted
                ? "Disk imajı zaten tamamlanmış"
                : report.WasResumed
                    ? "Disk imajı sürdürülerek tamamlandı"
                    : "Disk imajı tamamlandı";
            StatusDetail = report.BadBlocks == 0
                ? $"Tüm sektörler okundu. Retry ile kurtarılan: {report.RecoveredSectors:N0} sektör • {report.ImagePath}"
                : $"İmaj oluşturuldu. {report.BadBlocks:N0} sektör ({RecoveryFileItem.FormatBytes(report.UnreadableBytes)}) okunamadı ve sıfırlandı. Harita: {report.MapPath}";

            ProgressTitle = "Disk imajı kaydedildi";
            ProgressDetail = report.BadBlocks == 0
                ? $"{RecoveryFileItem.FormatBytes(report.ImageBytes)} • bad sector yok"
                : $"{RecoveryFileItem.FormatBytes(report.ImageBytes)} • kalan {report.BadBlocks:N0} bad sector";
            ProgressPercent = 100;
            ProgressProcessedText = RecoveryFileItem.FormatBytes(report.ImageBytes);
            ProgressEtaText = "Tamamlandı";
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Disk imajı alma kullanıcı tarafından iptal edildi.");
            StatusTitle = "Disk imajı duraklatıldı";
            StatusDetail = $"Yarım imaj ve checkpoint korundu. Aynı kaynakla aynı dosyayı seçerek kaldığı yerden sürdürebilirsiniz: {dialog.FileName}.nsxmap.json";
            ProgressEtaText = "Resume hazır";
        }
        catch (Exception ex)
        {
            AppLog.Error("Disk imajı oluşturulamadı.", ex);
            StatusTitle = "Disk imajı oluşturulamadı";
            bool checkpointPreserved = File.Exists(dialog.FileName) && File.Exists(dialog.FileName + ".nsxmap.json");
            StatusDetail = checkpointPreserved
                ? $"{ex.Message} Yarım imaj ve son güvenli checkpoint korundu."
                : ex.Message;
            MessageBox.Show(L(StatusDetail), L("NSX Veri Kurtarma Pro"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RecoverSelectedAsync()
    {
        List<RecoveryFileItem> selected = RecoveryFiles.Where(f => f.IsChecked).ToList();
        if (selected.Count == 0 && SelectedRecoveryFile is not null)
            selected.Add(SelectedRecoveryFile);

        await RecoverItemsAsync(selected);
    }

    private async Task RecoverSingleAsync()
    {
        if (SelectedRecoveryFile is null)
            return;

        await RecoverItemsAsync([SelectedRecoveryFile]);
    }

    public async Task RecoverPreviewItemAsync(StorageDeviceInfo device, RecoveryFileItem item)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(item);

        if (!device.IsReady)
        {
            MessageBox.Show(
                L("Dosyanın bulunduğu kaynak aygıt artık erişilebilir değil."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (IsScanRunning)
        {
            SelectedRecoveryFile = item;
            await RecoverItemsAsync([item], device);
            return;
        }

        if (IsBusy)
        {
            MessageBox.Show(
                L("Devam eden işlem tamamlanmadan yeni bir kurtarma başlatılamaz."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SelectedRecoveryFile = item;
        await RecoverItemsAsync([item], device);
    }

    private bool EnsureLicenseAllowsRecovery()
    {
        if (LicenseService.GetStatus().IsLicensed)
            return true;

        var licenseWindow = new NSXVeriKurtarmaPro.SystemCenterWindow(NSXVeriKurtarmaPro.SystemCenterSection.License);
        if (Application.Current?.MainWindow is Window owner && owner.IsVisible)
            licenseWindow.Owner = owner;

        licenseWindow.ShowDialog();
        return LicenseService.GetStatus().IsLicensed;
    }

    private async Task RecoverPreviewWhileScanningAsync(StorageDeviceInfo device, RecoveryFileItem item)
    {
        if (!EnsureLicenseAllowsRecovery())
            return;

        bool requiresRawAccess = !item.IsRepaired ||
                                 string.IsNullOrWhiteSpace(item.RepairedFilePath) ||
                                 !File.Exists(item.RepairedFilePath);
        if (requiresRawAccess && !ElevationService.EnsureAdministratorForRawAccess())
            return;

        if (!await _previewRecoveryGate.WaitAsync(0))
        {
            MessageBox.Show(
                L("Başka bir önizleme kurtarması devam ediyor. Tamamlandığında yeniden deneyin."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = L("Bu dosyanın kurtarılacağı farklı disk/klasörü seçin"),
                Multiselect = false
            };

            if (folderDialog.ShowDialog() != true)
                return;

            string destination = folderDialog.FolderName;
            DestinationSafetyCheck previewDestinationSafety = CheckDestinationSafety(device, destination);
            if (!previewDestinationSafety.IsSafe)
            {
                MessageBox.Show(
                    L(previewDestinationSafety.Message),
                    L("Güvenli Kurtarma"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureDestinationCapacityForSelection([item], destination))
                return;

            RecoveryBatchReport report = await Task.Run(() =>
                _recoveryService.Recover(
                    device,
                    [item],
                    destination,
                    progress: null,
                    pauseGate: null,
                    cancellationToken: CancellationToken.None));

            MessageBox.Show(
                L(report.Succeeded > 0
                    ? $"Dosya başarıyla kurtarıldı.\n\nHedef:\n{report.Destination}"
                    : "Dosya kurtarılamadı. Kaynak veri yapısı doğrulanamadı veya okunamadı."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                report.Succeeded > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                L(ex.Message),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _previewRecoveryGate.Release();
        }
    }

    private async Task RecoverItemsAsync(IReadOnlyList<RecoveryFileItem> selected, StorageDeviceInfo? sourceDevice = null)
    {
        StorageDeviceInfo? device = sourceDevice ?? SelectedDevice;
        if (device is null)
            return;

        if (selected.Count == 0)
        {
            MessageBox.Show(
                L("Kurtarmak istediğiniz en az bir dosyayı işaretleyin."),
                L("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!EnsureLicenseAllowsRecovery())
            return;

        bool requiresRawAccess = selected.Any(item =>
            !item.IsRepaired ||
            string.IsNullOrWhiteSpace(item.RepairedFilePath) ||
            !File.Exists(item.RepairedFilePath));
        if (requiresRawAccess && !ElevationService.EnsureAdministratorForRawAccess())
            return;

        var folderDialog = new OpenFolderDialog
        {
            Title = L("Kurtarılan dosyalar için farklı bir disk/klasör seçin"),
            Multiselect = false
        };

        if (folderDialog.ShowDialog() != true)
            return;

        string destination = folderDialog.FolderName;
        DestinationSafetyCheck destinationSafety = CheckDestinationSafety(device, destination);
        if (!destinationSafety.IsSafe)
        {
            MessageBox.Show(
                L(destinationSafety.Message),
                L("Güvenli Kurtarma"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!EnsureDestinationCapacityForSelection(selected, destination))
            return;

        bool scanWasRunning = IsScanRunning;
        bool scanWasAlreadyPaused = scanWasRunning && IsPaused;
        bool pauseScanForRecovery = scanWasRunning && !scanWasAlreadyPaused;
        bool startedStandaloneOperation = false;
        bool scanResumed = false;
        var recoveryCancellation = scanWasRunning ? new CancellationTokenSource() : null;
        _isRecoveryInProgress = true;
        OnPropertyChanged(nameof(SelectionStatusText));
        RaiseCommandStates();

        try
        {
            if (!scanWasRunning)
            {
                SetActiveNavigation("Recovery");
                BeginOperation(
                    "Dosyalar kurtarılmaya hazırlanıyor",
                    $"{selected.Count:N0} dosya seçildi.");
                startedStandaloneOperation = true;
            }
            else if (pauseScanForRecovery)
            {
                OperationPauseGate? scanGate = _pauseGate;
                scanGate?.Pause();
                _operationStopwatch?.Stop();
                IsPaused = true;
                StatusTitle = "Tarama kurtarma için duraklatıldı";
                StatusDetail = $"Kurtarılacak dosya: {selected.Count:N0} • tarama konumu güvenli biçimde kaydedildi.";
                ProgressTitle = "Tarama duraklatıldı";
                ProgressDetail = "Kurtarma tamamlandığında tarama kaldığı noktadan otomatik devam edecek.";
                ProgressSpeedText = "Kurtarma bekleniyor";
                ProgressElapsedText = FormatDuration((_operationStopwatch?.Elapsed ?? TimeSpan.Zero).TotalSeconds);
                ProgressEtaText = "Kurtarma sonrası devam";

                if (scanGate is not null)
                {
                    bool acknowledged = await Task.Run(() => scanGate.WaitUntilPaused(TimeSpan.FromSeconds(4)));
                    if (!acknowledged)
                        AppLog.Info("Tarama duraklatma onayı süre içinde gelmedi; kurtarma salt-okunur ayrı okuyucu ile devam ediyor.");
                }
            }

            long selectedBytes = selected.Sum(item => Math.Max(0L, item.SizeBytes));
            RecoveryProgressWindow recoveryWindow = new RecoveryProgressWindow(
                selected.Count,
                selectedBytes,
                destination,
                pauseScanForRecovery,
                scanWasAlreadyPaused);
            if (Application.Current?.MainWindow is Window recoveryOwner)
                recoveryWindow.Owner = recoveryOwner;

            CancellationToken token = scanWasRunning
                ? recoveryCancellation!.Token
                : _operationCts!.Token;

            using var recoveryPauseGate = new OperationPauseGate();
            recoveryWindow.PauseRequested += (_, _) =>
            {
                recoveryPauseGate.Pause();
                if (!scanWasRunning)
                {
                    StatusTitle = "Kurtarma duraklatıldı";
                    StatusDetail = "Aktif dosya güvenli blok sınırında bekliyor. Devam edildiğinde aynı noktadan sürecek.";
                }
            };
            recoveryWindow.ResumeRequested += (_, _) =>
            {
                recoveryPauseGate.Resume();
                if (!scanWasRunning)
                {
                    StatusTitle = "Dosyalar kurtarılıyor";
                    StatusDetail = "Kurtarma aynı noktadan devam ediyor.";
                }
            };
            recoveryWindow.CancelRequested += (_, _) =>
            {
                recoveryPauseGate.Resume();
                if (scanWasRunning)
                    recoveryCancellation?.Cancel();
                else
                    _operationCts?.Cancel();
            };

            var recoveryProgress = new Progress<OperationProgress>(progress =>
            {
                recoveryWindow.UpdateProgress(progress);
                if (!scanWasRunning && !recoveryPauseGate.IsPaused)
                    ApplyProgress(progress);
            });

            recoveryWindow.Show();

            try
            {
                RecoveryBatchReport report = await Task.Run(
                    () => _recoveryService.Recover(
                        device,
                        selected,
                        destination,
                        recoveryProgress,
                        pauseGate: recoveryPauseGate,
                        cancellationToken: token),
                    token);

                scanResumed = ResumeScanAfterRecovery(pauseScanForRecovery);

                if (scanWasRunning)
                {
                    StatusTitle = scanResumed
                        ? "Tarama devam ediyor"
                        : "Kurtarma tamamlandı";
                    StatusDetail = scanResumed
                        ? $"{report.Succeeded:N0} dosya kurtarıldı • tarama kaldığı noktadan devam ediyor."
                        : $"{report.Succeeded:N0} dosya kurtarıldı.";
                }
                else
                {
                    StatusTitle = report.Failed == 0
                        ? "Kurtarma tamamlandı"
                        : "Kurtarma tamamlandı • bazı dosyalar kurtarılamadı";
                    StatusDetail = $"Kurtarılan {report.Succeeded:N0} • Kısmi {report.Partial:N0} • Başarısız {report.Failed:N0} • Hedef: {report.Destination}";
                    ProgressTitle = "Kurtarma tamamlandı";
                    ProgressDetail = $"Yazılan veri {RecoveryFileItem.FormatBytes(report.WrittenBytes)} • Kurtarılan {report.Succeeded:N0}";
                    ProgressPercent = 100;
                    ProgressProcessedText = RecoveryFileItem.FormatBytes(report.WrittenBytes);
                    ProgressFoundText = $"{report.Succeeded:N0} dosya kurtarıldı";
                    ProgressEtaText = "Tamamlandı";
                }

                recoveryWindow.ShowCompleted(
                    report,
                    scanResumed,
                    scanWasAlreadyPaused && IsScanRunning);
            }
            catch (OperationCanceledException)
            {
                scanResumed = ResumeScanAfterRecovery(pauseScanForRecovery);
                if (scanWasRunning)
                {
                    StatusTitle = scanResumed ? "Tarama devam ediyor" : "Kurtarma durduruldu";
                    StatusDetail = scanResumed
                        ? "Kurtarma durduruldu; tarama kaldığı noktadan devam ediyor."
                        : "Kurtarma durduruldu. Tamamlanmış dosyalar hedef klasörde bırakıldı.";
                }
                else
                {
                    StatusTitle = "Kurtarma iptal edildi";
                    StatusDetail = "Tamamlanan dosyalar hedef klasörde korunuyor.";
                }

                recoveryWindow.ShowCancelled(
                    scanResumed,
                    scanWasAlreadyPaused && IsScanRunning);
            }
            catch (Exception ex)
            {
                scanResumed = ResumeScanAfterRecovery(pauseScanForRecovery);
                AppLog.Error("Kurtarma tamamlanamadı.", ex);
                StatusTitle = scanWasRunning && scanResumed ? "Tarama devam ediyor" : "Kurtarma tamamlanamadı";
                StatusDetail = scanWasRunning && scanResumed
                    ? $"Kurtarma hatası: {ex.Message} • tarama kaldığı noktadan devam ediyor."
                    : ex.Message;

                recoveryWindow.ShowFailed(
                    ex.Message,
                    scanResumed,
                    scanWasAlreadyPaused && IsScanRunning);
            }

            await recoveryWindow.WaitForCloseAsync();
        }
        finally
        {
            if (pauseScanForRecovery && !scanResumed)
                ResumeScanAfterRecovery(shouldResume: true);

            recoveryCancellation?.Dispose();

            if (startedStandaloneOperation)
                EndOperation();

            _isRecoveryInProgress = false;
            OnPropertyChanged(nameof(SelectionStatusText));
            RaiseCommandStates();
        }
    }

    private bool ResumeScanAfterRecovery(bool shouldResume)
    {
        if (!shouldResume || !IsScanRunning)
            return false;

        _pauseGate?.Resume();
        _operationStopwatch?.Start();
        IsPaused = false;
        ProgressTitle = _isUnifiedLostDataScan
            ? "Kayıp veri taraması kaldığı noktadan hazırlanıyor"
            : ScanMode == ScanMode.Deep
                ? "Derin tarama kaldığı noktadan hazırlanıyor"
                : ScanMode == ScanMode.Quick
                    ? "Hızlı tarama kaldığı noktadan hazırlanıyor"
                    : "Taramaya devam ediliyor";
        ProgressDetail = "Kurtarma tamamlandı; tarama kaldığı noktadan devam ediyor.";
        ProgressSpeedText = "Hazırlanıyor";
        ProgressElapsedText = FormatDuration((_operationStopwatch?.Elapsed ?? TimeSpan.Zero).TotalSeconds);
        ProgressEtaText = "Hesaplanıyor";
        _lastProgressElapsed = _operationStopwatch?.Elapsed ?? TimeSpan.Zero;
        _lastProgressBytes = Math.Max(0L, _scanResumePosition);
        return true;
    }

    private void ToggleSelectAll()
    {
        List<RecoveryFileItem> visible = RecoveryFilesView.Cast<RecoveryFileItem>().ToList();
        if (visible.Count == 0)
            return;

        bool shouldSelect = visible.Any(f => !f.IsChecked);
        _isBulkSelectionUpdate = true;
        try
        {
            foreach (RecoveryFileItem file in visible)
                file.IsChecked = shouldSelect;
        }
        finally
        {
            _isBulkSelectionUpdate = false;
        }

        RefreshPathNavigationSelectionStates();
        RaiseResultState();
        RaiseCommandStates();
    }

    private void ClearRecoverySelection()
    {
        _isBulkSelectionUpdate = true;
        try
        {
            foreach (RecoveryFileItem file in RecoveryFiles)
                file.IsChecked = false;
        }
        finally
        {
            _isBulkSelectionUpdate = false;
        }

        RefreshPathNavigationSelectionStates();
        RaiseResultState();
        RaiseCommandStates();
    }

    private void ShowAllResults()
    {
        ResultSearchText = string.Empty;
        ResultCategory = "Tümü";
        _resultTypeGroup = "all";
        _resultExtension = string.Empty;
        _resultPathFilter = string.Empty;
        if (_recoveryNavigationInitialized && IsResultPathNavigationActive)
            RebuildPathNavigation();
        UpdateNavigationSelection();
        RecoveryFilesView.Refresh();
        OnPropertyChanged(nameof(ActiveResultFilterTitle));
        RaiseResultState();
    }

    private void ClearResults(bool resetFilters = false)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _previewRequested.Clear();
        _visiblePreviewRequested.Clear();
        _automaticPreviewQueue.Clear();
        _captureDateQueue.Clear();
        _captureDateRequested.Clear();
        _automaticPhotoPreviewQueued = 0;
        _automaticVideoPreviewQueued = 0;

        foreach (RecoveryFileItem file in RecoveryFiles)
            file.PropertyChanged -= RecoveryFile_PropertyChanged;

        RecoveryFiles.Clear();
        _selectionStateCache.Clear();
        _historicalFolderPaths.Clear();
        _pathRootExpansionInitialized = false;
        SelectedRecoveryFile = null;
        _scanResumePosition = 0;
        _scanResumeTotal = 0;
        _scanResumeAvailable = false;
        _resumeReplayInProgress = false;
        _resumePositionFloor = 0;
        _resumePercentFloor = 0d;
        _pendingProjectState = null;
        _currentProjectPath = null;
        _liveNavigationRefreshPending = false;
        _lastLiveNavigationRefreshTick = 0L;

        if (resetFilters)
        {
            _expandedNavigationGroups.Clear();
            _resultSearchText = string.Empty;
            _resultCategory = "Tümü";
            _resultTypeGroup = "all";
            _resultExtension = string.Empty;
            _resultPathFilter = string.Empty;
            _isResultTypeNavigationActive = false;
            OnPropertyChanged(nameof(ResultSearchText));
            OnPropertyChanged(nameof(ResultCategory));
            OnPropertyChanged(nameof(IsResultTypeNavigationActive));
            OnPropertyChanged(nameof(IsResultPathNavigationActive));
            OnPropertyChanged(nameof(ActiveResultFilterTitle));
        }

        RefreshRecoveryNavigation();
        RecoveryFilesView.Refresh();
        RaiseResultState();
    }


    public void RequestVisiblePreviews(IReadOnlyList<RecoveryFileItem> items)
    {
        if (!ProUiSettings.Current.AutoGenerateThumbnails)
            return;

        if (SelectedDevice is not { IsReady: true } device || items.Count == 0)
            return;

        CancellationToken token = EnsurePreviewToken();
        List<RecoveryFileItem> pending = items
            .Where(i => i.PreviewImage is null
                        && (i.Category == "Fotoğraf" || i.Category == "Video")
                        && !_visiblePreviewRequested.Contains(i))
            .Take(PreviewBatchSize)
            .ToList();

        if (pending.Count == 0)
            return;

        foreach (RecoveryFileItem item in pending)
        {
            _visiblePreviewRequested.Add(item);
            _previewRequested.Add(item);
        }

        _ = GeneratePreviewsAsync(device, pending, token);
    }

    private void QueueCaptureDates(IReadOnlyList<RecoveryFileItem> items)
    {
        if (SelectedDevice is not { IsReady: true } device || items.Count == 0)
            return;

        CancellationToken token = EnsurePreviewToken();

        // Dosya sistemi timestamp'i varsa listeye anında düşür. EXIF/container metadata
        // çözümü aşağıdaki tek iş parçacıklı kuyrukta arka planda çalışır ve daha güvenilir
        // bir tarih bulduğunda fallback değerini yükseltir.
        MediaCreationTimeService.PopulateFileSystemFallback(items, token);

        foreach (RecoveryFileItem item in items)
        {
            if (MediaCreationTimeService.NeedsMetadataProbe(item) &&
                _captureDateRequested.Add(item))
            {
                _captureDateQueue.Enqueue(item);
            }
        }

        if (!_captureDatePumpRunning)
            _ = RunCaptureDatePumpAsync(device, token);
    }

    private async Task RunCaptureDatePumpAsync(
        StorageDeviceInfo device,
        CancellationToken cancellationToken)
    {
        if (_captureDatePumpRunning)
            return;

        _captureDatePumpRunning = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = new List<RecoveryFileItem>(CaptureDateBatchSize);
                while (_captureDateQueue.Count > 0 && batch.Count < CaptureDateBatchSize)
                    batch.Add(_captureDateQueue.Dequeue());

                if (batch.Count == 0)
                    break;

                IReadOnlyList<MediaCreationTimeService.CaptureDateProbeResult> resolved;
                try
                {
                    resolved = await Task.Run(
                        () => MediaCreationTimeService.Probe(device, batch, cancellationToken),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    foreach (RecoveryFileItem item in batch)
                        _captureDateRequested.Remove(item);
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                // await sonrası WPF synchronization context'e dönülür; binding'e bağlı
                // property'ler yalnız UI thread üzerinde güncellenir.
                foreach (MediaCreationTimeService.CaptureDateProbeResult result in resolved)
                {
                    _captureDateRequested.Remove(result.Item);
                    if (!result.CapturedAt.HasValue)
                        continue;

                    result.Item.CapturedAt = result.CapturedAt;
                    result.Item.CaptureDateSource = result.Source;
                }
            }
        }
        finally
        {
            _captureDatePumpRunning = false;

            if (_captureDateQueue.Count > 0
                && _previewCts is { IsCancellationRequested: false } cts
                && SelectedDevice is { IsReady: true } nextDevice)
            {
                _ = RunCaptureDatePumpAsync(nextDevice, cts.Token);
            }
        }
    }

    private void QueueAutomaticPreviews(IReadOnlyList<RecoveryFileItem> items)
    {
        if (!ProUiSettings.Current.AutoGenerateThumbnails)
            return;

        if (SelectedDevice is not { IsReady: true } device || items.Count == 0)
            return;

        CancellationToken token = EnsurePreviewToken();

        int photoBudget = AutomaticPhotoPreviewBudget;
        int videoBudget = AutomaticVideoPreviewBudget;

        // Dönel HDD'de tarama sırasında binlerce thumbnail için random seek yapmak sequential
        // Quick/Deep I/O'yu dramatik biçimde yavaşlatır. Kullanıcı ilk görsel geri bildirimi yine
        // hızlı alır; geniş thumbnail kuyruğu final Organizasyon fazından sonra devam eder.
        if (IsScanRunning)
        {
            bool rotational = NtfsQuickScanService.IsLegacyRotationalQuickPath(device) ||
                              device.VisualKind is "Hdd" or "FixedDisk" or "ExternalHdd";
            photoBudget = rotational ? Math.Min(photoBudget, 48) : Math.Min(photoBudget, 480);
            videoBudget = rotational ? Math.Min(videoBudget, 6) : Math.Min(videoBudget, 24);
        }

        int remainingPhotoBudget = photoBudget - _automaticPhotoPreviewQueued;
        int remainingVideoBudget = videoBudget - _automaticVideoPreviewQueued;
        if (remainingPhotoBudget <= 0 && remainingVideoBudget <= 0)
            return;

        List<RecoveryFileItem> pendingPhotos = remainingPhotoBudget > 0
            ? items
                .Where(i => i.PreviewImage is null
                            && i.Category == "Fotoğraf"
                            && !_previewRequested.Contains(i))
                .Take(remainingPhotoBudget)
                .ToList()
            : new List<RecoveryFileItem>();

        var pendingPhotoSet = new HashSet<RecoveryFileItem>(pendingPhotos);
        List<RecoveryFileItem> pendingVideos = remainingVideoBudget > 0
            ? items
                .Where(i => i.PreviewImage is null
                            && i.Category == "Video"
                            && !_previewRequested.Contains(i)
                            && !pendingPhotoSet.Contains(i))
                .OrderBy(i => FileTypeHelper.GetVideoPriority(i.Extension))
                .Take(remainingVideoBudget)
                .ToList()
            : new List<RecoveryFileItem>();

        if (pendingPhotos.Count == 0 && pendingVideos.Count == 0)
            return;

        foreach (RecoveryFileItem item in pendingPhotos)
        {
            _previewRequested.Add(item);
            _automaticPreviewQueue.Enqueue(item);
        }

        foreach (RecoveryFileItem item in pendingVideos)
        {
            _previewRequested.Add(item);
            _automaticPreviewQueue.Enqueue(item);
        }

        _automaticPhotoPreviewQueued += pendingPhotos.Count;
        _automaticVideoPreviewQueued += pendingVideos.Count;

        if (!_automaticPreviewPumpRunning)
            _ = RunAutomaticPreviewPumpAsync(device, token);
    }

    private CancellationToken EnsurePreviewToken()
    {
        if (_previewCts is null || _previewCts.IsCancellationRequested)
        {
            _previewCts?.Dispose();
            _previewCts = new CancellationTokenSource();
        }

        return _previewCts.Token;
    }

    private async Task RunAutomaticPreviewPumpAsync(
        StorageDeviceInfo device,
        CancellationToken cancellationToken)
    {
        if (_automaticPreviewPumpRunning)
            return;

        _automaticPreviewPumpRunning = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested && ProUiSettings.Current.AutoGenerateThumbnails)
            {
                var batch = new List<RecoveryFileItem>(PreviewBatchSize);
                while (_automaticPreviewQueue.Count > 0 && batch.Count < PreviewBatchSize)
                {
                    RecoveryFileItem item = _automaticPreviewQueue.Dequeue();
                    if (item.PreviewImage is null)
                        batch.Add(item);
                }

                if (batch.Count == 0)
                    break;

                await GeneratePreviewsAsync(device, batch, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _automaticPreviewPumpRunning = false;

            if (_automaticPreviewQueue.Count > 0
                && _previewCts is { IsCancellationRequested: false } cts
                && SelectedDevice is { IsReady: true } nextDevice)
            {
                _ = RunAutomaticPreviewPumpAsync(nextDevice, cts.Token);
            }
        }
    }

    private async Task GeneratePreviewsAsync(
        StorageDeviceInfo device,
        IReadOnlyList<RecoveryFileItem> items,
        CancellationToken cancellationToken)
    {
        bool gateEntered = false;
        try
        {
            await _previewBuildGate.WaitAsync(cancellationToken);
            gateEntered = true;

            if (cancellationToken.IsCancellationRequested)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            await Task.Run(() => _previewService.BuildPreviews(
                device,
                items,
                cancellationToken,
                preview =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || dispatcher.HasShutdownStarted
                        || dispatcher.HasShutdownFinished)
                        return;

                    dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() =>
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            preview.Item.PreviewImage = preview.Image;
                            if (preview.Width > 0 && preview.Height > 0)
                                preview.Item.SetResolution(preview.Width, preview.Height);
                        }));
                }));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (gateEntered)
                _previewBuildGate.Release();
        }
    }

    private void ReplaceResults(IReadOnlyList<RecoveryFileItem> items, bool metadataAlreadyOrganized = false)
    {
        SelectedRecoveryFile = null;

        var incomingItems = new HashSet<RecoveryFileItem>(items);
        _previewRequested.RemoveWhere(item => !incomingItems.Contains(item));
        _visiblePreviewRequested.RemoveWhere(item => !incomingItems.Contains(item));
        _captureDateRequested.RemoveWhere(item => !incomingItems.Contains(item));

        foreach (RecoveryFileItem file in RecoveryFiles)
            file.PropertyChanged -= RecoveryFile_PropertyChanged;

        _isReplacingResults = true;
        try
        {
            using (RecoveryFilesView.DeferRefresh())
            {
                _selectionStateCache.Clear();
                AttachResultItems(items);
                RecoveryFiles.ReplaceAll(items);
            }
        }
        finally
        {
            _isReplacingResults = false;
        }
        if (!metadataAlreadyOrganized)
            QueueCaptureDates(items);
        QueueAutomaticPreviews(items);
        if (!_deferScanResultOrganization)
        {
            if (IsRecoveryWorkspace)
                RefreshRecoveryNavigation();
            RaiseResultState();
            RaiseCommandStates();
        }
    }

    private void AttachResultItems(IEnumerable<RecoveryFileItem> items)
    {
        foreach (RecoveryFileItem item in items)
        {
            item.PropertyChanged -= RecoveryFile_PropertyChanged;
            item.PropertyChanged += RecoveryFile_PropertyChanged;
            _selectionStateCache[item] = item.IsChecked;
        }
    }

    private void RecoveryFile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (e.PropertyName == nameof(RecoveryFileItem.RecoveredOriginalPath))
            {
                QueueRecoveryNavigationRefresh();
            }
            else if (e.PropertyName == nameof(RecoveryFileItem.IsChecked) ||
                     e.PropertyName == nameof(RecoveryFileItem.PreviewImage))
            {
                dispatcher.BeginInvoke(new Action(() => RecoveryFile_PropertyChanged(sender, e)));
            }

            return;
        }

        if (e.PropertyName == nameof(RecoveryFileItem.IsChecked))
        {
            if (sender is not RecoveryFileItem file)
                return;

            bool hasPreviousState = _selectionStateCache.TryGetValue(file, out bool wasChecked);
            _selectionStateCache[file] = file.IsChecked;
            if (_isBulkSelectionUpdate)
                return;

            if (hasPreviousState && wasChecked != file.IsChecked)
                ApplyPathSelectionDelta(file, file.IsChecked ? 1 : -1);
            else
                RefreshPathNavigationSelectionStates();
            RaiseResultState();
            RaiseCommandStates();
            return;
        }

        if (e.PropertyName == nameof(RecoveryFileItem.RecoveredOriginalPath) &&
            IsRecoveryWorkspace && !IsResultTypeNavigationActive)
        {
            if (_deferScanResultOrganization)
                RequestLiveNavigationRefresh();
            else
                RebuildPathNavigation();
        }

        if (ReferenceEquals(sender, SelectedRecoveryFile)
            && e.PropertyName == nameof(RecoveryFileItem.PreviewImage))
        {
            RaiseCommandStates();
        }
    }

    private bool FilterRecoveryFile(object item)
    {
        if (item is not RecoveryFileItem file)
            return false;

        if (!MatchesNonPathResultFilters(file))
            return false;

        return string.IsNullOrWhiteSpace(_resultPathFilter) || MatchesPathFilter(file, _resultPathFilter);
    }

    private bool MatchesNonPathResultFilters(RecoveryFileItem file)
    {
        if (!MatchesProResultFilters(file))
            return false;

        if (!string.Equals(_resultTypeGroup, "all", StringComparison.OrdinalIgnoreCase) &&
            !RecoveryTypeCatalog.ExtensionBelongsTo(file.Extension, _resultTypeGroup))
            return false;

        if (!string.IsNullOrWhiteSpace(_resultExtension) &&
            !string.Equals(RecoveryTypeCatalog.NormalizeExtension(file.Extension), _resultExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        string search = ResultSearchText.Trim();
        if (search.Length == 0)
            return true;

        return file.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               file.Extension.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               file.RecoveryState.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               file.LocationText.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool MatchesProResultFilters(RecoveryFileItem file)
    {
        if (!IsProResultFilterEnabled)
            return true;

        if (HideZeroByteFiles && file.SizeBytes <= 0)
            return false;

        if (HideThumbnailFiles && IsLikelyThumbnailFile(file))
            return false;

        if (HideDamagedFiles && IsLikelyDamagedFile(file))
            return false;

        if (HideExistingFiles && file.IsExistingFile)
            return false;

        if (HideSystemTemporaryFiles && IsLikelySystemTemporaryFile(file))
            return false;

        if (MinimumRecoveryConfidence > 0 &&
            file.HasRecoveryConfidence &&
            file.RecoveryConfidenceScore < MinimumRecoveryConfidence)
        {
            return false;
        }

        if (MinimumFileSizeMb > 0d)
        {
            long minimumBytes = (long)Math.Round(MinimumFileSizeMb * 1024d * 1024d, MidpointRounding.AwayFromZero);
            if (file.SizeBytes < minimumBytes)
                return false;
        }

        return true;
    }

    private static bool IsLikelyDamagedFile(RecoveryFileItem file)
    {
        string state = file.RecoveryState ?? string.Empty;
        if (state.Equals("Zayıf", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Kısmi", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Video Parçası", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (file.VideoHealthScore.HasValue && file.VideoHealthScore.Value < 40)
            return true;

        return file.HasRecoveryConfidence && file.RecoveryConfidenceScore < 35;
    }

    private static bool IsLikelySystemTemporaryFile(RecoveryFileItem file)
    {
        string fileName = (file.FileName ?? string.Empty).Trim().ToLowerInvariant();
        string extension = RecoveryTypeCatalog.NormalizeExtension(file.Extension);
        string recoveredPath = (file.RecoveredOriginalPath ?? string.Empty).ToLowerInvariant();
        string sourceText = (file.SourceText ?? string.Empty).ToLowerInvariant();
        string combined = recoveredPath + "|" + sourceText;

        if (fileName is "desktop.ini" or ".ds_store" or "pagefile.sys" or "hiberfil.sys" or "swapfile.sys")
            return true;

        if (fileName.StartsWith("~$", StringComparison.Ordinal) ||
            fileName.EndsWith(".tmp", StringComparison.Ordinal) ||
            fileName.EndsWith(".temp", StringComparison.Ordinal))
        {
            return true;
        }

        if (extension is "tmp" or "temp")
            return true;

        return combined.Contains("\\system volume information\\", StringComparison.Ordinal) ||
               combined.Contains("/.cache/", StringComparison.Ordinal) ||
               combined.Contains("\\.cache\\", StringComparison.Ordinal) ||
               combined.Contains("/.spotlight-v100/", StringComparison.Ordinal) ||
               combined.Contains("\\.spotlight-v100\\", StringComparison.Ordinal);
    }

    private static bool IsLikelyThumbnailFile(RecoveryFileItem file)
    {
        string fileName = (file.FileName ?? string.Empty).ToLowerInvariant();
        string recoveredPath = (file.RecoveredOriginalPath ?? string.Empty).ToLowerInvariant();
        string sourceText = (file.SourceText ?? string.Empty).ToLowerInvariant();
        string combined = fileName + "|" + recoveredPath + "|" + sourceText;

        // Windows / tarayıcı / uygulama thumbnail cache kayıtlarını uzantıdan bağımsız ayıkla.
        if (fileName.Equals("thumbs.db", StringComparison.Ordinal) ||
            fileName.StartsWith("thumbcache_", StringComparison.Ordinal) ||
            fileName.StartsWith("thumbcache-", StringComparison.Ordinal) ||
            combined.Contains("\\.thumbnails\\", StringComparison.Ordinal) ||
            combined.Contains("\\thumbnails\\", StringComparison.Ordinal) ||
            combined.Contains("/thumbnails/", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(file.Category, "Fotoğraf", StringComparison.OrdinalIgnoreCase))
            return false;

        return combined.Contains("thumbnail", StringComparison.Ordinal) ||
               combined.Contains("thumbcache", StringComparison.Ordinal) ||
               fileName.StartsWith("thumb_", StringComparison.Ordinal) ||
               fileName.StartsWith("thumb-", StringComparison.Ordinal) ||
               fileName.StartsWith("tn_", StringComparison.Ordinal) ||
               fileName.StartsWith("tn-", StringComparison.Ordinal) ||
               fileName.EndsWith("_thumb.jpg", StringComparison.Ordinal) ||
               fileName.EndsWith("_thumb.jpeg", StringComparison.Ordinal) ||
               fileName.EndsWith("_thumb.png", StringComparison.Ordinal) ||
               fileName.EndsWith(".thumb", StringComparison.Ordinal);
    }

    private void RequestLiveNavigationRefresh()
    {
        if (!_deferScanResultOrganization || !IsRecoveryWorkspace ||
            (RecoveryFiles.Count == 0 && _historicalFolderPaths.Count == 0))
            return;

        long now = Environment.TickCount64;

        // İlk sonuç batch'inde navigasyonu bekletme. Kullanıcı tarama sürerken YOL/TÜR'ü
        // hemen görsün; sonraki yenilemeler UI'yi yormamak için kısa aralıkla throttle edilir.
        if (!_recoveryNavigationInitialized || now - _lastLiveNavigationRefreshTick >= LiveNavigationRefreshIntervalMs)
        {
            RefreshLiveNavigationForMode(_isResultTypeNavigationActive);
            return;
        }

        if (_liveNavigationRefreshPending)
            return;

        int delayMs = (int)Math.Clamp(
            LiveNavigationRefreshIntervalMs - (now - _lastLiveNavigationRefreshTick),
            80L,
            LiveNavigationRefreshIntervalMs);

        _liveNavigationRefreshPending = true;
        _ = ScheduleLiveNavigationRefreshAsync(delayMs);
    }

    private async Task ScheduleLiveNavigationRefreshAsync(int delayMs)
    {
        await Task.Delay(delayMs);

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            _liveNavigationRefreshPending = false;
            if (_deferScanResultOrganization && IsRecoveryWorkspace &&
                (RecoveryFiles.Count > 0 || _historicalFolderPaths.Count > 0))
                RefreshLiveNavigationForMode(_isResultTypeNavigationActive);
        }));
    }

    private void RefreshLiveNavigationForMode(bool typeMode)
    {
        if (!IsRecoveryWorkspace || (RecoveryFiles.Count == 0 && _historicalFolderPaths.Count == 0))
            return;

        if (!_recoveryNavigationInitialized)
        {
            BuildTypeNavigation();
            _recoveryNavigationInitialized = true;
        }
        else if (typeMode)
        {
            RebuildTypeNavigationCounts();
        }

        if (!typeMode)
            RebuildPathNavigation();

        UpdateNavigationSelection();
        _lastLiveNavigationRefreshTick = Environment.TickCount64;
    }

    private async Task CommitOrganizedResultsAsync(IReadOnlyList<RecoveryFileItem> files)
    {
        Stopwatch watch = Stopwatch.StartNew();
        // Unlike Task.Yield (Normal priority), Background lets pending input/render work run.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        ProgressDetail = "Dosya listesi güncelleniyor.";
        ReplaceResults(files, metadataAlreadyOrganized: true);
        ProgressPercent = 96;
        long listMs = watch.ElapsedMilliseconds;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        // ReplaceAll already refreshed the ICollectionView once.
        ProgressDetail = "Klasör ağacı ve dosya türleri güncelleniyor.";
        FlushDeferredResultOrganization(refreshView: false);
        ProgressPercent = 100;
        ProgressSpeedText = "Tamamlandı";
        ProgressEtaText = "Tamamlandı";
        long navigationMs = watch.ElapsedMilliseconds - listMs;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        AppLog.Info($"[SCAN FINALIZE] files={files.Count}, listMs={listMs}, navigationMs={navigationMs}, totalMs={watch.ElapsedMilliseconds}");
    }
    private void FlushDeferredResultOrganization(bool refreshView = true)
    {
        if (!_deferScanResultOrganization)
            return;

        _deferScanResultOrganization = false;
        _liveNavigationRefreshPending = false;
        if (refreshView)
            RecoveryFilesView.Refresh();
        if (IsRecoveryWorkspace)
            RefreshRecoveryNavigation();
        RaiseResultState();
    }

    private void RaiseLiveScanResultState()
    {
        OnPropertyChanged(nameof(FoundCount));
        OnPropertyChanged(nameof(FoundBytes));
        OnPropertyChanged(nameof(FoundBytesText));
        OnPropertyChanged(nameof(PhotoCount));
        OnPropertyChanged(nameof(VideoCount));
        OnPropertyChanged(nameof(DocumentCount));
        OnPropertyChanged(nameof(DisplayedCount));
        OnPropertyChanged(nameof(IsResultsEmpty));
        OnPropertyChanged(nameof(ResultSummaryText));
        OnPropertyChanged(nameof(IsRecoveryWorkspace));
        OnPropertyChanged(nameof(IsLandingWorkspace));
    }

    private async Task<IReadOnlyList<RecoveryFileItem>> OrganizeScanResultsAsync(
        StorageDeviceInfo device,
        IReadOnlyList<RecoveryFileItem> files,
        CancellationToken cancellationToken)
    {
        ProgressTitle = "Bulunan dosyalar organize ediliyor...";
        ProgressDetail = "Tarama tamamlandı • tür, klasör, tarih ve kalite indeksleri hazırlanıyor.";
        ProgressPercent = 0;
        ProgressProcessedText = $"0 / {files.Count:N0} kayıt";
        ProgressFoundText = $"{files.Count:N0} dosya";
        ProgressSpeedText = "Organize ediliyor";
        ProgressEtaText = "Hesaplanıyor";

        IsOrganizingResults = true;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if (files.Count == 0)
            return files;

        Stopwatch organizeWatch = Stopwatch.StartNew();
        IProgress<(int Processed, int Total, string Phase)> organizeProgress =
            new Progress<(int Processed, int Total, string Phase)>(state =>
        {
            int processed = Math.Clamp(state.Processed, 0, Math.Max(0, state.Total));
            int total = Math.Max(0, state.Total);
            ProgressPercent = total <= 0 ? 0d : Math.Clamp(processed * 70d / total, 0d, 70d);
            ProgressProcessedText = $"{processed:N0} / {total:N0} kayıt";
            ProgressDetail = $"{state.Phase} • {processed:N0}/{total:N0}.";

            double rate = organizeWatch.Elapsed.TotalSeconds > 0.35
                ? processed / organizeWatch.Elapsed.TotalSeconds
                : 0d;
            ProgressSpeedText = rate > 0 ? $"{rate:N0} kayıt/sn" : "Organize ediliyor";
            ProgressEtaText = rate > 0 && total > processed
                ? FormatDuration((total - processed) / rate)
                : "—";
        });

        // Referans programlardaki gibi tarama I/O'su bitmeden ağır organizasyon yapılmaz.
        // Bu faz özellikle CPU/RAM ağırlıklıdır: diski yeniden okumaz, thumbnail üretmez ve
        // recovery motoruyla yarışmaz. Mevcut metadata üzerinden indeks hazırlığı yapılır.
        await Task.Run(() =>
        {
            int total = files.Count;
            const int reportEvery = 8192;
            int nextReport = Math.Min(reportEvery, total);

            for (int index = 0; index < total; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Property erişimleri bilinçli olarak hafiftir; bu geçiş collection/tree kurmadan
                // önce veri modelini sıcak cache'e alır ve organizasyon yüzdesini gerçek işten üretir.
                RecoveryFileItem? item = files[index];
                if (item is null)
                    continue;

                _ = item.TypeSortKey;
                _ = item.ResultDateSortValue;
                _ = item.QualitySortValue;
                _ = item.LocationText;

                int processed = index + 1;
                if (processed >= nextReport || processed == total)
                {
                    organizeProgress.Report((processed, total, "Kayıt indeksi hazırlanıyor"));
                    nextReport = Math.Min(total, processed + reportEvery);
                }
            }
        }, cancellationToken);

        ProgressPercent = 78;
        ProgressTitle = "Bulunan dosyalar organize ediliyor...";
        ProgressDetail = "Final liste sıralaması hazırlanıyor.";
        ProgressSpeedText = "Final indeks";
        ProgressEtaText = "Kısa süre";

        List<RecoveryFileItem> ordered = await Task.Run(
            () => RecoveryScanPriorityService.OrderNewestFirst(files).ToList(),
            cancellationToken);

        ProgressPercent = 92;
        ProgressDetail = "Tür ve klasör görünümü tek seferde oluşturuluyor.";
        ProgressProcessedText = $"{ordered.Count:N0} / {ordered.Count:N0} kayıt";
        await Task.Yield();

        return ordered;
    }

    private void RaiseResultState()
    {
        OnPropertyChanged(nameof(FoundCount));
        OnPropertyChanged(nameof(FoundBytes));
        OnPropertyChanged(nameof(FoundBytesText));
        OnPropertyChanged(nameof(PhotoCount));
        OnPropertyChanged(nameof(VideoCount));
        OnPropertyChanged(nameof(DocumentCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedBytesText));
        OnPropertyChanged(nameof(HasRecoverySelection));
        OnPropertyChanged(nameof(HasSingleRecoveryFileInfo));
        OnPropertyChanged(nameof(IsRecoveryInfoEmpty));
        OnPropertyChanged(nameof(SelectedPhotoCount));
        OnPropertyChanged(nameof(SelectedVideoCount));
        OnPropertyChanged(nameof(SelectedDocumentCount));
        OnPropertyChanged(nameof(SelectedPhotoBytes));
        OnPropertyChanged(nameof(SelectedVideoBytes));
        OnPropertyChanged(nameof(SelectedDocumentBytes));
        OnPropertyChanged(nameof(SelectedPhotoBytesText));
        OnPropertyChanged(nameof(SelectedVideoBytesText));
        OnPropertyChanged(nameof(SelectedDocumentBytesText));
        OnPropertyChanged(nameof(SelectedExtensionCount));
        OnPropertyChanged(nameof(SelectedRepairedCount));
        OnPropertyChanged(nameof(SelectedRepairedText));
        OnPropertyChanged(nameof(SelectedVisibleCount));
        OnPropertyChanged(nameof(SelectionCoverageText));
        OnPropertyChanged(nameof(SelectionTypeText));
        OnPropertyChanged(nameof(SelectionRequiredSpaceText));
        OnPropertyChanged(nameof(SelectionSourceText));
        OnPropertyChanged(nameof(SelectionSourceDetail));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(DisplayedCount));
        OnPropertyChanged(nameof(IsResultsEmpty));
        OnPropertyChanged(nameof(ResultSummaryText));
        OnPropertyChanged(nameof(IsRecoveryWorkspace));
        OnPropertyChanged(nameof(IsLandingWorkspace));
        OnPropertyChanged(nameof(ActiveResultFilterTitle));
        OnPropertyChanged(nameof(AreAllVisibleSelected));
        OnPropertyChanged(nameof(IsResultFilterAllActive));
        OnPropertyChanged(nameof(IsResultFilterPhotoActive));
        OnPropertyChanged(nameof(IsResultFilterVideoActive));
        OnPropertyChanged(nameof(IsResultFilterDocumentActive));
    }

    private void BeginOperation(string title, string detail)
    {
        _operationCts?.Dispose();
        _pauseGate?.Dispose();
        _operationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationCts = new CancellationTokenSource();
        _pauseGate = new OperationPauseGate();
        _operationStopwatch = Stopwatch.StartNew();
        _lastProgressBytes = 0;
        _lastProgressElapsed = TimeSpan.Zero;
        _smoothedProgressBytesPerSecond = 0d;

        ProgressPercent = 0;
        ProgressTitle = title;
        ProgressDetail = detail;
        ProgressProcessedText = "Hazırlanıyor";
        ProgressFoundText = "0 dosya";
        ProgressSpeedText = "Hazırlanıyor";
        ProgressElapsedText = "0 sn";
        ProgressEtaText = "Hesaplanıyor";
        IsPaused = false;
        IsBusy = true;
    }

    private void PauseOperation()
    {
        if (!IsBusy || IsPaused)
            return;

        _pauseGate?.Pause();
        _operationStopwatch?.Stop();
        IsPaused = true;
        ProgressTitle = "Tarama duraklatıldı";
        ProgressDetail = "Pause gate açık; Resume komutunda persisted offset üzerinden worker devam eder.";
        ProgressSpeedText = "Duraklatıldı";
        ProgressElapsedText = FormatDuration((_operationStopwatch?.Elapsed ?? TimeSpan.Zero).TotalSeconds);
        ProgressEtaText = "Bekliyor";
    }

    private void ResumeOperation()
    {
        if (!IsBusy || !IsPaused)
            return;

        _pauseGate?.Resume();
        _operationStopwatch?.Start();
        IsPaused = false;
        ProgressTitle = "Tarama devam ediyor";
        ProgressDetail = "Tarama kaldığı noktadan sürdürüldü.";
        ProgressSpeedText = "Hazırlanıyor";
        ProgressElapsedText = FormatDuration((_operationStopwatch?.Elapsed ?? TimeSpan.Zero).TotalSeconds);
        ProgressEtaText = "Hesaplanıyor";
        _lastProgressElapsed = _operationStopwatch?.Elapsed ?? TimeSpan.Zero;
        _lastProgressBytes = Math.Max(0L, _scanResumePosition);
    }

    private void StopOperation()
    {
        if (!IsBusy)
            return;

        // Duraklatılmış iş parçacığının iptal token'ını görebilmesi için önce kapıyı aç.
        _pauseGate?.Resume();
        IsPaused = false;
        ProgressTitle = "Tarama durduruluyor";
        ProgressDetail = "İşlem güvenli biçimde sonlandırılıyor; kaynak diskte herhangi bir değişiklik yapılmaz.";
        ProgressSpeedText = "Durduruluyor";
        ProgressEtaText = "—";
        _operationCts?.Cancel();
    }

    private void EndOperation()
    {
        TaskCompletionSource<bool>? completion = _operationCompletion;
        _operationCompletion = null;
        _operationStopwatch?.Stop();
        ProgressElapsedText = FormatDuration((_operationStopwatch?.Elapsed ?? TimeSpan.Zero).TotalSeconds);
        _operationStopwatch = null;
        _pauseGate?.Resume();
        _pauseGate?.Dispose();
        _pauseGate = null;
        IsPaused = false;
        IsBusy = false;
        _operationCts?.Dispose();
        _operationCts = null;
        completion?.TrySetResult(true);
    }

    public string? CurrentProjectPath => _currentProjectPath;

    public bool HasSavableProject =>
        RecoveryFiles.Count > 0 ||
        IsScanRunning ||
        _scanResumeAvailable ||
        _pendingProjectState is not null;

    public async Task<bool> LoadProjectAsync(string projectPath)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(projectPath))
            return false;

        RecoveryProjectState? state = await Task.Run(() => _projectService.Load(projectPath));
        if (state is null)
        {
            MessageBox.Show(
                LocalizationService.Current.Translate("Seçilen NSX kurtarma projesi açılamadı veya proje dosyası geçerli değil."),
                LocalizationService.Current.Translate("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        state.Files ??= [];
        if (state.Files.Count == 0 && !state.ResumeScan)
        {
            MessageBox.Show(
                LocalizationService.Current.Translate("Bu NSX projesinde devam ettirilecek tarama veya kurtarma sonucu bulunamadı."),
                LocalizationService.Current.Translate("NSX Veri Kurtarma Pro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        _currentProjectPath = RecoveryProjectService.EnsureProjectExtension(projectPath);
        _pendingProjectState = state;
        _projectId = string.IsNullOrWhiteSpace(state.ProjectId) ? Guid.NewGuid().ToString("N") : state.ProjectId;

        if (state.DeviceIsPartitionSource &&
            state.DevicePhysicalDriveNumber is int projectPhysicalDrive &&
            state.DevicePartitionOffsetBytes >= 0 &&
            state.DevicePartitionLengthBytes > 0)
        {
            int resolvedPhysicalDrive = projectPhysicalDrive;
            string projectSerial = (state.DeviceSerialNumber ?? string.Empty).Trim();
            if (projectSerial.Length > 0)
            {
                StorageDeviceInfo? reattachedPhysical = Devices.FirstOrDefault(item =>
                    item.IsWholePhysicalDisk &&
                    item.PhysicalDriveNumber.HasValue &&
                    string.Equals(
                        (item.HardwareSerialNumber ?? string.Empty).Trim(),
                        projectSerial,
                        StringComparison.OrdinalIgnoreCase));
                if (reattachedPhysical?.PhysicalDriveNumber is int currentPhysicalDrive)
                    resolvedPhysicalDrive = currentPhysicalDrive;
            }

            _storageDeviceService.RegisterDiscoveredPartitions([new PartitionCandidate(
                resolvedPhysicalDrive,
                state.DevicePartitionOffsetBytes,
                state.DevicePartitionLengthBytes,
                string.IsNullOrWhiteSpace(state.DeviceFileSystem) ? "RAW" : state.DeviceFileSystem,
                string.IsNullOrWhiteSpace(state.DevicePartitionScheme) ? "NSX Session" : state.DevicePartitionScheme,
                "NSX Kayıtlı Bölüm",
                string.IsNullOrWhiteSpace(state.DevicePartitionIdentity)
                    ? $"NSX-{resolvedPhysicalDrive}-{state.DevicePartitionOffsetBytes:X}"
                    : state.DevicePartitionIdentity,
                Math.Clamp(state.DevicePartitionConfidence <= 0 ? 95 : state.DevicePartitionConfidence, 1, 100),
                state.DeviceWasRecoveredPartition)]);

            IReadOnlyList<StorageDeviceInfo> refreshedSources = await Task.Run(_storageDeviceService.GetDevices);
            Devices.Clear();
            foreach (StorageDeviceInfo source in refreshedSources)
                Devices.Add(source);
        }
        _scanCheckpoint = state.ScanCheckpoint is null ? null : CloneCheckpoint(state.ScanCheckpoint);
        QuickScanScope = state.QuickScanScope;
        DeepScanScope = state.DeepScanScope;
        FolderScanScope = state.FolderScanScope;
        _folderScanPath = state.FolderScanPath ?? string.Empty;
        _isFolderScanSession = state.IsFolderScan;
        ScanMode = state.ScanMode;
        _isUnifiedLostDataScan = !state.IsFolderScan && state.ScanMode == ScanMode.Deep;
        SetActiveNavigation(state.IsFolderScan ? "Folder" : state.ScanMode == ScanMode.Deep ? "Deep" : "Quick");
        _scanResumePosition = Math.Max(0, _scanCheckpoint?.ResumePosition ?? state.ResumePosition);
        _scanResumeTotal = Math.Max(0, _scanCheckpoint?.ResumeTotal ?? state.ResumeTotal);
        _scanResumeAvailable = state.ResumeScan && !state.ScanCompleted;

        _resultTypeGroup = string.IsNullOrWhiteSpace(state.ResultTypeGroup) ? "all" : state.ResultTypeGroup;
        _resultExtension = state.ResultExtension ?? string.Empty;
        _resultPathFilter = state.ResultPathFilter ?? string.Empty;
        _isResultTypeNavigationActive = state.IsResultTypeNavigationActive;
        _expandedNavigationGroups.Clear();
        foreach (string group in state.ExpandedNavigationGroups ?? [])
        {
            if (!string.IsNullOrWhiteSpace(group))
                _expandedNavigationGroups.Add(group);
        }
        _pathRootExpansionInitialized = true;
        _historicalFolderPaths.Clear();
        MergeHistoricalFolderPaths(state.HistoricalFolders);

        StorageDeviceInfo? device = FindMatchingProjectDevice(state);
        foreach (StorageDeviceInfo item in Devices)
            item.IsSelected = ReferenceEquals(item, device);
        SelectedDevice = device;

        List<RecoveryFileItem> restoredFiles = state.Files
            .Select(file => file.ToItem())
            .ToList();
        ReplaceResults(restoredFiles);

        ResultCategory = string.IsNullOrWhiteSpace(state.ResultCategory) ? "Tümü" : state.ResultCategory;
        ResultSearchText = state.ResultSearchText ?? string.Empty;
        RecoveryFilesView.Refresh();
        RefreshRecoveryNavigation();
        OnPropertyChanged(nameof(IsResultTypeNavigationActive));
        OnPropertyChanged(nameof(IsResultPathNavigationActive));
        OnPropertyChanged(nameof(ActiveResultFilterTitle));

        SelectedRecoveryFile = RestoreSelectedResult(state.SelectedResultKey, restoredFiles);
        ProgressPercent = GetRestoredProjectPercent(state);
        ProgressFoundText = $"{RecoveryFiles.Count:N0} dosya";
        ProgressSpeedText = state.ResumeScan ? "Bekliyor" : "—";
        ProgressEtaText = state.ScanCompleted ? "Tamamlandı" : "—";

        if (_scanResumeTotal > 0)
        {
            ProgressProcessedText = state.IsFolderScan
                ? $"{_scanResumePosition:N0} / {_scanResumeTotal:N0}"
                : state.ScanMode == ScanMode.Deep || IsQuickSurfaceCheckpoint(_scanCheckpoint)
                    ? $"{RecoveryFileItem.FormatBytes(_scanResumePosition)} / {RecoveryFileItem.FormatBytes(_scanResumeTotal)}"
                    : $"{_scanResumePosition:N0} / {_scanResumeTotal:N0}";
        }
        else
        {
            ProgressProcessedText = _scanResumePosition > 0 ? $"{_scanResumePosition:N0}" : "0";
        }

        if (state.ResumeScan && !state.ScanCompleted)
        {
            ProgressTitle = "Kayıtlı Tarama • Devam Ediyor";
            ProgressDetail = "Kayıtlı tarama durumu geri yüklendi.";
            ProgressSpeedText = "Bekliyor";
            ProgressEtaText = "Bekliyor";
        }

        if (device is null)
        {
            ProgressTitle = state.ScanCompleted ? "NSX Kayıtlı Tarama • Yüklendi" : "NSX Kayıtlı Tarama • Kaynak Bekleniyor";
            ProgressDetail = "Kaynak aygıt bağlı değil; aynı aygıt yeniden bağlandığında kayıtlı tarama otomatik devam edecek.";
            StatusTitle = "NSX Kayıtlı Tarama • Yüklendi";
            StatusDetail = "Kaynak aygıt doğrulanmadan tarama başlatılmaz.";
            return true;
        }

        QueueAutomaticPreviews(restoredFiles);
        await TryResumePendingProjectAsync();
        return true;
    }

    public bool SaveProject(string projectPath)
    {
        try
        {
            RecoveryProjectState? state = BuildProjectState(IsScanRunning || _scanResumeAvailable);
            if (state is null)
                return false;

            string normalizedPath = RecoveryProjectService.EnsureProjectExtension(projectPath);
            _projectService.Save(normalizedPath, state);
            _currentProjectPath = normalizedPath;
            if (_pendingProjectState is not null)
                _pendingProjectState = state;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("NSX kurtarma projesi kaydedilemedi.", ex);
            return false;
        }
    }

    private RecoveryProjectState? BuildProjectState(bool forceResume)
    {
        RecoveryProjectState? pending = _pendingProjectState;
        StorageDeviceInfo? device = SelectedDevice;

        if (device is null && pending is null)
            return null;

        if (RecoveryFiles.Count == 0 && !forceResume && !_scanResumeAvailable)
            return null;

        bool resume = forceResume || _scanResumeAvailable;
        RecoveryScanCheckpoint? checkpoint = BuildProjectCheckpoint(pending);
        RecoveryMediaProfile? mediaProfile = device is null ? null : RecoveryMediaProfileService.Create(device);

        return new RecoveryProjectState
        {
            Version = RecoveryProjectService.CurrentVersion,
            ProjectId = _projectId,
            ApplicationVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? string.Empty,
            SavedAtUtc = DateTime.UtcNow,
            DeviceRootPath = device?.RootPath ?? pending?.DeviceRootPath ?? string.Empty,
            DeviceDisplayName = device?.DisplayName ?? pending?.DeviceDisplayName ?? string.Empty,
            DeviceFileSystem = device?.FileSystem ?? pending?.DeviceFileSystem ?? string.Empty,
            DeviceTotalBytes = device?.TotalBytes ?? pending?.DeviceTotalBytes ?? 0,
            DevicePhysicalDriveNumber = device?.PhysicalDriveNumber ?? pending?.DevicePhysicalDriveNumber,
            DeviceIsWholePhysicalDisk = device?.IsWholePhysicalDisk ?? pending?.DeviceIsWholePhysicalDisk ?? false,
            DeviceIsPartitionSource = device?.IsPartitionSource ?? pending?.DeviceIsPartitionSource ?? false,
            DevicePartitionOffsetBytes = device?.PartitionOffsetBytes ?? pending?.DevicePartitionOffsetBytes ?? 0,
            DevicePartitionLengthBytes = device?.PartitionLengthBytes ?? pending?.DevicePartitionLengthBytes ?? 0,
            DevicePartitionIdentity = device?.PartitionIdentity ?? pending?.DevicePartitionIdentity ?? string.Empty,
            DevicePartitionScheme = device?.PartitionScheme ?? pending?.DevicePartitionScheme ?? string.Empty,
            DeviceWasRecoveredPartition = device?.IsRecoveredPartition ?? pending?.DeviceWasRecoveredPartition ?? false,
            DevicePartitionConfidence = device?.PartitionConfidence ?? pending?.DevicePartitionConfidence ?? 0,
            DeviceSerialNumber = device?.HardwareSerialNumber ?? pending?.DeviceSerialNumber ?? string.Empty,
            DeviceVisualKind = device?.VisualKind ?? pending?.DeviceVisualKind ?? string.Empty,
            DeviceBusTypeText = device?.BusTypeText ?? pending?.DeviceBusTypeText ?? string.Empty,
            DeviceCameraContentDetected = device?.CameraContentDetected ?? pending?.DeviceCameraContentDetected ?? false,
            MediaProfileKey = mediaProfile?.Key ?? pending?.MediaProfileKey ?? string.Empty,
            ScanMode = ScanMode,
            QuickScanScope = QuickScanScope,
            DeepScanScope = DeepScanScope,
            IsFolderScan = _isFolderScanSession,
            FolderScanPath = _folderScanPath,
            FolderScanScope = FolderScanScope,
            ResumeScan = resume,
            ScanCompleted = !resume && ProgressPercent >= 100d,
            WasPaused = IsPaused,
            ResumePosition = Math.Max(0, _scanResumePosition),
            ResumeTotal = Math.Max(0, _scanResumeTotal),
            ProgressPercent = Math.Clamp(ProgressPercent, 0d, 100d),
            ScanCheckpoint = checkpoint,
            ResultCategory = ResultCategory,
            ResultSearchText = ResultSearchText,
            ResultTypeGroup = _resultTypeGroup,
            ResultExtension = _resultExtension,
            ResultPathFilter = _resultPathFilter,
            IsResultTypeNavigationActive = _isResultTypeNavigationActive,
            ExpandedNavigationGroups = _expandedNavigationGroups.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            HistoricalFolders = _historicalFolderPaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            SelectedResultKey = SelectedRecoveryFile is null ? string.Empty : BuildRecoveryItemKey(SelectedRecoveryFile),
            Files = RecoveryFiles.Select(RecoveryProjectFileState.FromItem).ToList()
        };
    }

    private RecoveryScanCheckpoint? BuildProjectCheckpoint(RecoveryProjectState? pending)
    {
        if (ScanMode == ScanMode.Quick && !_isFolderScanSession)
        {
            RecoveryScanCheckpoint? quickSource = _scanCheckpoint ?? pending?.ScanCheckpoint;
            RecoveryScanCheckpoint quickCheckpoint = quickSource is not null &&
                                                     (IsQuickMetadataCheckpoint(quickSource) || IsQuickSurfaceCheckpoint(quickSource))
                ? CloneCheckpoint(quickSource)
                : new RecoveryScanCheckpoint
                {
                    PassNumber = 1,
                    Stage = "quick-metadata",
                    MetadataStageCompleted = false
                };

            quickCheckpoint.ResumePosition = Math.Max(0, _scanResumePosition);
            quickCheckpoint.ResumeTotal = Math.Max(0, _scanResumeTotal);
            if (IsQuickMetadataCheckpoint(quickCheckpoint))
            {
                quickCheckpoint.MetadataStageCompleted = quickCheckpoint.ResumeTotal > 0 &&
                                                         quickCheckpoint.ResumePosition >= quickCheckpoint.ResumeTotal;
                quickCheckpoint.RawScanCompleted = false;
            }
            else if (IsQuickSurfaceCheckpoint(quickCheckpoint))
            {
                quickCheckpoint.MetadataStageCompleted = true;
                quickCheckpoint.RawScanCompleted = quickCheckpoint.ResumeTotal > 0 &&
                                                   quickCheckpoint.ResumePosition >= quickCheckpoint.ResumeTotal;
            }

            quickCheckpoint.ScannedRanges = [];
            quickCheckpoint.PendingFragmentNodes = [];
            quickCheckpoint.PendingFragmentNodesAreDelta = false;
            return quickCheckpoint;
        }

        if (ScanMode != ScanMode.Deep)
            return null;

        RecoveryScanCheckpoint? source = _scanCheckpoint ?? pending?.ScanCheckpoint;
        RecoveryScanCheckpoint checkpoint = source is null
            ? new RecoveryScanCheckpoint
            {
                PassNumber = _scanResumePosition > 0 ? 2 : 1,
                Stage = _scanResumePosition > 0 ? "deep-raw" : "metadata",
                MetadataStageCompleted = _scanResumePosition > 0
            }
            : CloneCheckpoint(source);

        checkpoint.ResumePosition = Math.Max(checkpoint.ResumePosition, Math.Max(0, _scanResumePosition));
        checkpoint.ResumeTotal = Math.Max(checkpoint.ResumeTotal, Math.Max(0, _scanResumeTotal));
        if (checkpoint.ResumeTotal > 0 && checkpoint.ResumePosition >= checkpoint.ResumeTotal && checkpoint.PassNumber >= 3)
            checkpoint.RawScanCompleted = true;

        if (checkpoint.ResumePosition > 0)
        {
            long length = checkpoint.ResumeTotal > 0
                ? Math.Min(checkpoint.ResumePosition, checkpoint.ResumeTotal)
                : checkpoint.ResumePosition;
            if (length > 0)
            {
                checkpoint.ScannedRanges =
                [
                    new RecoveryScannedRange
                    {
                        Offset = 0,
                        Length = length,
                        Kind = "RAW"
                    }
                ];
            }
        }

        return checkpoint;
    }

    private static RecoveryScanCheckpoint CloneCheckpoint(RecoveryScanCheckpoint source) => new()
    {
        PassNumber = source.PassNumber,
        Stage = source.Stage,
        ResumePosition = source.ResumePosition,
        ResumeTotal = source.ResumeTotal,
        MetadataStageCompleted = source.MetadataStageCompleted,
        RawScanCompleted = source.RawScanCompleted,
        FragmentStageCompleted = source.FragmentStageCompleted,
        FinalValidationStarted = source.FinalValidationStarted,
        AdaptiveBlockSize = source.AdaptiveBlockSize,
        AdaptiveSafeScanActive = source.AdaptiveSafeScanActive,
        AdaptiveBlocksObserved = source.AdaptiveBlocksObserved,
        AdaptiveCleanBlocks = source.AdaptiveCleanBlocks,
        AdaptiveDenseBlocks = source.AdaptiveDenseBlocks,
        AdaptiveErrorBlocks = source.AdaptiveErrorBlocks,
        AdaptiveUnreadableBytes = source.AdaptiveUnreadableBytes,
        AdaptiveBlockSizeChanges = source.AdaptiveBlockSizeChanges,
        AdaptiveReadSamples = source.AdaptiveReadSamples,
        AdaptiveReadBytes = source.AdaptiveReadBytes,
        AdaptiveReadMilliseconds = source.AdaptiveReadMilliseconds,
        AdaptivePeakBytesPerSecond = source.AdaptivePeakBytesPerSecond,
        AdaptiveSlowReadSamples = source.AdaptiveSlowReadSamples,
        AdaptiveFastReadSamples = source.AdaptiveFastReadSamples,
        BadSectorFailureEvents = source.BadSectorFailureEvents,
        BadSectorRecoveredBytes = source.BadSectorRecoveredBytes,
        BadSectorSkippedKnownBadReads = source.BadSectorSkippedKnownBadReads,
        BadSectors = (source.BadSectors ?? []).Select(item => new RecoveryBadSectorSnapshot
        {
            Offset = item.Offset,
            Length = item.Length,
            FailureEvents = item.FailureEvents
        }).ToList(),
        ScannedRanges = (source.ScannedRanges ?? []).Select(item => new RecoveryScannedRange
        {
            Offset = item.Offset,
            Length = item.Length,
            Kind = item.Kind
        }).ToList(),
        PendingFragmentNodesAreDelta = source.PendingFragmentNodesAreDelta,
        PendingFragmentNodes = (source.PendingFragmentNodes ?? []).Select(CloneFragmentCheckpointItem).ToList()
    };

    private static RecoveryFragmentCheckpointItem CloneFragmentCheckpointItem(RecoveryFragmentCheckpointItem source) => new()
    {
        FileName = source.FileName,
        Extension = source.Extension,
        SizeBytes = source.SizeBytes,
        RecoveryState = source.RecoveryState,
        TypeGlyph = source.TypeGlyph,
        SourceText = source.SourceText,
        SourceKind = source.SourceKind,
        SourceOffset = source.SourceOffset,
        TransformKind = source.TransformKind
    };

    private static bool IsQuickMetadataCheckpoint(RecoveryScanCheckpoint? checkpoint) =>
        checkpoint is not null &&
        string.Equals(checkpoint.Stage, "quick-metadata", StringComparison.OrdinalIgnoreCase);

    private static bool IsQuickSurfaceCheckpoint(RecoveryScanCheckpoint? checkpoint)
    {
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Stage))
            return false;

        return checkpoint.Stage.StartsWith("quick-surface", StringComparison.OrdinalIgnoreCase) ||
               checkpoint.Stage.StartsWith("quick-free-space", StringComparison.OrdinalIgnoreCase);
    }

    private static RecoveryScanCheckpoint CreateQuickMetadataCheckpoint(OperationProgress progress) => new()
    {
        PassNumber = 1,
        Stage = "quick-metadata",
        ResumePosition = Math.Max(0, progress.ProcessedBytes),
        ResumeTotal = Math.Max(0, progress.TotalBytes),
        MetadataStageCompleted = progress.TotalBytes > 0 && progress.ProcessedBytes >= progress.TotalBytes,
        RawScanCompleted = false,
        FragmentStageCompleted = false,
        FinalValidationStarted = false,
        ScannedRanges = []
    };

    private static double GetRestoredProjectPercent(RecoveryProjectState state)
    {
        double saved = Math.Clamp(state.ProgressPercent, 0d, 100d);
        if (!state.ResumeScan || state.ScanCompleted || state.ScanMode != ScanMode.Quick || state.IsFolderScan)
            return saved;

        RecoveryScanCheckpoint? checkpoint = state.ScanCheckpoint;
        long position = Math.Max(0, checkpoint?.ResumePosition ?? state.ResumePosition);
        long total = Math.Max(0, checkpoint?.ResumeTotal ?? state.ResumeTotal);
        if (total <= 0 || position >= total)
            return saved >= 100d ? 99d : saved;

        // Quick Scan progress is phase-local. Persisted position/total is the authoritative
        // cursor, so an older stale 100% UI value can never hide an unfinished session.
        return Math.Clamp(position * 100d / total, 0d, 99.9d);
    }

    private static RecoveryScanCheckpoint MergeCheckpoint(
        RecoveryScanCheckpoint? current,
        RecoveryScanCheckpoint incoming)
    {
        RecoveryScanCheckpoint merged = CloneCheckpoint(incoming);
        if (!incoming.PendingFragmentNodesAreDelta)
        {
            merged.PendingFragmentNodesAreDelta = false;
            return merged;
        }

        var anchors = new Dictionary<(long Offset, string Extension, RecoveryTransformKind Transform), RecoveryFragmentCheckpointItem>();
        if (current?.PendingFragmentNodes is { Count: > 0 })
        {
            foreach (RecoveryFragmentCheckpointItem item in current.PendingFragmentNodes)
            {
                string extension = FileTypeHelper.Normalize(item.Extension);
                anchors[(item.SourceOffset, extension, item.TransformKind)] = CloneFragmentCheckpointItem(item);
            }
        }

        foreach (RecoveryFragmentCheckpointItem item in incoming.PendingFragmentNodes ?? [])
        {
            string extension = FileTypeHelper.Normalize(item.Extension);
            anchors[(item.SourceOffset, extension, item.TransformKind)] = CloneFragmentCheckpointItem(item);
        }

        merged.PendingFragmentNodes = anchors
            .OrderBy(pair => pair.Key.Offset)
            .Select(pair => pair.Value)
            .ToList();
        merged.PendingFragmentNodesAreDelta = false;
        return merged;
    }

    private static string BuildRecoveryItemKey(RecoveryFileItem item) =>
        $"{(int)item.SourceKind}|{item.SourceOffset}|{item.SizeBytes}|{item.FileName}";

    private static RecoveryFileItem? RestoreSelectedResult(string? key, IReadOnlyList<RecoveryFileItem> files)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            RecoveryFileItem? match = files.FirstOrDefault(item =>
                string.Equals(BuildRecoveryItemKey(item), key, StringComparison.Ordinal));
            if (match is not null)
                return match;
        }

        return files.FirstOrDefault(item => item.IsChecked) ?? files.FirstOrDefault();
    }

    private StorageDeviceInfo? FindMatchingProjectDevice(RecoveryProjectState state)
    {
        static bool SameFileSystem(StorageDeviceInfo device, RecoveryProjectState project) =>
            string.Equals(
                (device.FileSystem ?? string.Empty).Trim(),
                (project.DeviceFileSystem ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);

        static bool MatchesOptional(string actual, string expected) =>
            string.IsNullOrWhiteSpace(expected) ||
            string.Equals((actual ?? string.Empty).Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

        static bool SameExtendedIdentity(StorageDeviceInfo device, RecoveryProjectState project) =>
            MatchesOptional(device.VisualKind, project.DeviceVisualKind) &&
            MatchesOptional(device.BusTypeText, project.DeviceBusTypeText) &&
            device.IsWholePhysicalDisk == project.DeviceIsWholePhysicalDisk &&
            device.IsPartitionSource == project.DeviceIsPartitionSource &&
            (!project.DeviceIsPartitionSource ||
             (device.PartitionOffsetBytes == project.DevicePartitionOffsetBytes &&
              device.PartitionLengthBytes == project.DevicePartitionLengthBytes &&
              MatchesOptional(device.PartitionIdentity, project.DevicePartitionIdentity)));

        string projectSerial = (state.DeviceSerialNumber ?? string.Empty).Trim();
        if (projectSerial.Length > 0)
        {
            StorageDeviceInfo? serialMatch = Devices.FirstOrDefault(device =>
                device.IsReady &&
                device.TotalBytes == state.DeviceTotalBytes &&
                SameFileSystem(device, state) &&
                SameExtendedIdentity(device, state) &&
                string.Equals((device.HardwareSerialNumber ?? string.Empty).Trim(), projectSerial, StringComparison.OrdinalIgnoreCase));
            if (serialMatch is not null)
                return serialMatch;
        }

        if (state.DevicePhysicalDriveNumber.HasValue)
        {
            StorageDeviceInfo? physicalMatch = Devices.FirstOrDefault(device =>
                device.IsReady &&
                device.PhysicalDriveNumber == state.DevicePhysicalDriveNumber &&
                device.TotalBytes == state.DeviceTotalBytes &&
                SameFileSystem(device, state) &&
                SameExtendedIdentity(device, state) &&
                (string.Equals(device.DisplayName, state.DeviceDisplayName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(device.RootPath, state.DeviceRootPath, StringComparison.OrdinalIgnoreCase)));
            if (physicalMatch is not null)
                return physicalMatch;
        }

        StorageDeviceInfo? exactRootMatch = Devices.FirstOrDefault(device =>
            device.IsReady &&
            device.TotalBytes == state.DeviceTotalBytes &&
            SameFileSystem(device, state) &&
            SameExtendedIdentity(device, state) &&
            string.Equals(device.RootPath, state.DeviceRootPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(device.DisplayName, state.DeviceDisplayName, StringComparison.OrdinalIgnoreCase));
        if (exactRootMatch is not null)
            return exactRootMatch;

        // Removable media and cameras can reconnect under a different drive letter.
        // Only accept this relaxed match when it is unique, so a same-size second device
        // cannot silently become the recovery source.
        List<StorageDeviceInfo> relocatedCandidates = Devices
            .Where(device =>
                device.IsReady &&
                device.TotalBytes == state.DeviceTotalBytes &&
                SameFileSystem(device, state) &&
                SameExtendedIdentity(device, state) &&
                string.Equals(device.DisplayName, state.DeviceDisplayName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return relocatedCandidates.Count == 1 ? relocatedCandidates[0] : null;
    }

    private async Task TryResumePendingProjectAsync()
    {
        RecoveryProjectState? state = _pendingProjectState;
        if (state is null)
            return;

        StorageDeviceInfo? device = FindMatchingProjectDevice(state);
        if (device is null)
            return;

        foreach (StorageDeviceInfo item in Devices)
            item.IsSelected = ReferenceEquals(item, device);
        SelectedDevice = device;
        QueueAutomaticPreviews(RecoveryFiles.ToList());

        bool reconnectingSource = _scanWaitingForReconnect;
        _scanWaitingForReconnect = false;
        _scanDisconnectInterruptRequested = false;
        _activeScanRootPath = NormalizeRootPath(device.RootPath);
        _pendingProjectState = null;
        if (!state.ResumeScan || state.ScanCompleted || IsBusy)
        {
            StatusTitle = "Kayıtlı Kurtarma • Yüklendi";
            StatusDetail = $"Kayıtlı sonuçlar geri yüklendi • {RecoveryFiles.Count:N0} dosya.";
            ProgressTitle = state.ScanCompleted ? "Tarama tamamlandı" : "Kayıtlı tarama hazır";
            ProgressDetail = state.ScanCompleted
                ? $"{RecoveryFiles.Count:N0} kurtarılabilir aday listelendi."
                : "Kayıtlı tarama durumu geri yüklendi.";
            return;
        }

        StatusTitle = reconnectingSource ? "Kaynak Aygıt • Yeniden Bağlandı" : "Kayıtlı Tarama • Devam Ediyor";
        StatusDetail = reconnectingSource
            ? $"{device.DisplayName} doğrulandı; tarama kesilmeden kayıtlı noktadan otomatik sürdürülüyor."
            : $"{device.DisplayName} doğrulandı; tarama kayıtlı noktadan sürdürülüyor.";
        await Task.Delay(reconnectingSource ? 120 : 250);
        if (state.IsFolderScan && !string.IsNullOrWhiteSpace(state.FolderScanPath))
            await StartFolderScanAsync(state.FolderScanPath, resumeExisting: true);
        else
            await StartScanAsync(resumeExisting: true);
    }

    public void RequestShutdown()
    {
        _previewCts?.Cancel();
        _previewService.Dispose();
        if (IsBusy)
            StopOperation();
    }

    public Task WaitForCurrentOperationAsync() => _operationCompletion?.Task ?? Task.CompletedTask;

    private void ApplyProgress(OperationProgress progress)
    {
        if (_acceptLiveScanResults && progress.NewFiles is { Count: > 0 })
        {
            IReadOnlyList<RecoveryFileItem> newFiles = ScanMode == ScanMode.Quick && QuickScanScope != DeepScanTarget.All
                ? progress.NewFiles.Where(file => MatchesQuickScanScope(file, QuickScanScope)).ToList()
                : progress.NewFiles;

            if (newFiles.Count > 0)
            {
                AttachResultItems(newFiles);
                RecoveryFiles.AddRange(newFiles);

                // Thumbnail kullanıcıya anında güven verir; EXIF/container tarihi, klasör ağacı,
                // tür sayımları ve final sıralama ise tarama bittikten sonraki Organizasyon fazında yapılır.
                QueueAutomaticPreviews(newFiles);
            }
        }

        bool historicalFoldersChanged = MergeHistoricalFolderPaths(progress.NewHistoricalFolders);

        // NTFS path enrichment worker tarafında mevcut item nesnelerine sonradan yazılabilir.
        // Progress<T> bu metoda UI context üzerinde döndüğü için YOL ağacını burada, mevcut
        // 900 ms throttle ile güvenli biçimde yenile. V7'de tarihsel klasör kataloğu dosya
        // sonucu olmasa bile aynı refresh hattına girer.
        if (_deferScanResultOrganization && !_isResultTypeNavigationActive &&
            (RecoveryFiles.Count > 0 || historicalFoldersChanged || _historicalFolderPaths.Count > 0))
        {
            RequestLiveNavigationRefresh();
        }

        double incomingPercent = Math.Clamp(progress.Percent, 0d, 100d);
        ProgressPercent = _resumeReplayInProgress
            ? Math.Max(_resumePercentFloor, incomingPercent)
            : incomingPercent;

        // Duraklat'a basıldığı anda worker'dan daha önce kuyruğa girmiş bir Progress
        // bildirimi başlığı tekrar "taranıyor" yapmasın. Sayısal metrikler güncellenebilir
        // fakat kullanıcı durum metni Duraklat/Devam kontrolünde kalır.
        if (!IsPaused)
        {
            ProgressTitle = _isUnifiedLostDataScan ? NormalizeUnifiedScanText(progress.Title) : progress.Title;
            ProgressDetail = _isUnifiedLostDataScan ? NormalizeUnifiedScanText(progress.Detail) : progress.Detail;
        }

        if (progress.Checkpoint is not null)
        {
            if (ScanMode == ScanMode.Deep)
            {
                _scanCheckpoint = MergeCheckpoint(_scanCheckpoint, progress.Checkpoint);
            }
            else if (ScanMode == ScanMode.Quick && !_isFolderScanSession)
            {
                // Quick checkpoints are immutable snapshots created per existing progress tick.
                // Keep the snapshot directly: no extra disk I/O and no per-tick deep-copy cost.
                _scanCheckpoint = progress.Checkpoint;
            }
        }
        else if (ScanMode == ScanMode.Quick && !_isFolderScanSession && progress.TotalBytes > 0 && !IsQuickSurfaceCheckpoint(_scanCheckpoint))
        {
            // Metadata progress uses record units. Persist it as an explicit phase so a save
            // at metadata %100 is still understood as "surface phase may be pending".
            _scanCheckpoint = CreateQuickMetadataCheckpoint(progress);
        }

        RecoveryScanCheckpoint? activeCheckpoint = ScanMode == ScanMode.Deep || (ScanMode == ScanMode.Quick && !_isFolderScanSession)
            ? _scanCheckpoint
            : null;
        long checkpointProcessed = Math.Max(0, activeCheckpoint?.ResumePosition ?? 0);
        long liveProcessed = Math.Max(0, progress.ProcessedBytes);
        long processed = Math.Max(checkpointProcessed, liveProcessed);
        long resumeProcessed = activeCheckpoint is not null ? checkpointProcessed : liveProcessed;
        long checkpointTotal = Math.Max(0, activeCheckpoint?.ResumeTotal ?? 0);
        long total = Math.Max(checkpointTotal, Math.Max(0, progress.TotalBytes));
        if (IsScanRunning)
        {
            _scanResumePosition = _resumeReplayInProgress
                ? Math.Max(_resumePositionFloor, resumeProcessed)
                : resumeProcessed;
            _scanResumeTotal = _resumeReplayInProgress
                ? Math.Max(_scanResumeTotal, total)
                : total;
            _scanResumeAvailable = true;
        }

        ProgressProcessedText = _isFolderScanSession
            ? total > 0
                ? $"{processed:N0} / {total:N0} kayıt"
                : processed > 0 ? $"{processed:N0} kayıt" : "Hazırlanıyor"
            : total > 0
                ? $"{RecoveryFileItem.FormatBytes(processed)} / {RecoveryFileItem.FormatBytes(total)}"
                : processed > 0
                    ? RecoveryFileItem.FormatBytes(processed)
                    : "Hazırlanıyor";
        int visibleFoundCount = ScanMode == ScanMode.Quick && QuickScanScope != DeepScanTarget.All
            ? RecoveryFiles.Count
            : Math.Max(Math.Max(0, progress.FoundCount), RecoveryFiles.Count);
        ProgressFoundText = $"{visibleFoundCount:N0} dosya";

        TimeSpan elapsed = _operationStopwatch?.Elapsed ?? TimeSpan.Zero;
        ProgressElapsedText = FormatDuration(elapsed.TotalSeconds);

        if (IsPaused)
        {
            ProgressSpeedText = "Duraklatıldı";
            ProgressEtaText = "Bekliyor";
            return;
        }

        double intervalSeconds = (elapsed - _lastProgressElapsed).TotalSeconds;
        long intervalBytes = processed - _lastProgressBytes;
        double instantRate = intervalSeconds >= 0.20 && intervalBytes >= 0
            ? intervalBytes / intervalSeconds
            : elapsed.TotalSeconds > 0.75 && processed > 0
                ? processed / elapsed.TotalSeconds
                : 0d;

        if (intervalBytes < 0)
            _smoothedProgressBytesPerSecond = 0d;

        if (instantRate > 0d)
        {
            _smoothedProgressBytesPerSecond = _smoothedProgressBytesPerSecond <= 0d
                ? instantRate
                : (_smoothedProgressBytesPerSecond * 0.72d) + (instantRate * 0.28d);
        }

        if (intervalSeconds >= 0.20)
        {
            _lastProgressElapsed = elapsed;
            _lastProgressBytes = processed;
        }

        double effectiveRate = _smoothedProgressBytesPerSecond > 0d
            ? _smoothedProgressBytesPerSecond
            : instantRate;

        ProgressSpeedText = effectiveRate > 0d
            ? _isFolderScanSession
                ? $"{effectiveRate:N0} kayıt/sn"
                : $"{RecoveryFileItem.FormatBytes((long)effectiveRate)}/sn"
            : ProgressPercent >= 100 ? "Tamamlandı" : "Hazırlanıyor";

        if (ProgressPercent >= 100)
        {
            ProgressEtaText = "Tamamlandı";
        }
        else if (total > processed && effectiveRate > 0d && elapsed.TotalSeconds >= 1d)
        {
            double seconds = (total - processed) / effectiveRate;
            ProgressEtaText = FormatDuration(seconds);
        }
        else
        {
            ProgressEtaText = "Hesaplanıyor";
        }
    }

    private static string NormalizeUnifiedScanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? string.Empty;

        return text
            .Replace("Derin Tarama", "Kayıp Veri Taraması", StringComparison.OrdinalIgnoreCase)
            .Replace("Derin tarama", "Kayıp veri taraması", StringComparison.OrdinalIgnoreCase)
            .Replace("Deep Scan", "Kayıp Veri Taraması", StringComparison.OrdinalIgnoreCase)
            .Replace("Hızlı Tarama", "Kayıp Veri Taraması • Dosya Sistemi", StringComparison.OrdinalIgnoreCase)
            .Replace("Hızlı tarama", "Kayıp veri taraması • dosya sistemi", StringComparison.OrdinalIgnoreCase)
            .Replace("Quick Scan", "Kayıp Veri Taraması • Dosya Sistemi", StringComparison.OrdinalIgnoreCase)
            .Replace(" • Pass 1/4", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" • Pass 2/4", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" • Pass 3/4", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" • Pass 4/4", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 1/4: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 2/4: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 3/4: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 4/4: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 1/4 • ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 2/4 • ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 3/4 • ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Pass 4/4 • ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("4-pass pipeline", "tek tarama", StringComparison.OrdinalIgnoreCase)
            .Replace("Adaptive RAW", "Ham Veri", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCompletedScanDiagnosticText(ScanReport report)
    {
        RecoveryScanDiagnostics? diagnostics = report.Diagnostics;
        if (diagnostics is null)
            return LocalizationService.Current.Translate($"Doğrulanan dosya: {report.Files.Count:N0}");

        string average = diagnostics.AverageReadBytesPerSecond > 0d
            ? RecoveryFileItem.FormatBytes((long)diagnostics.AverageReadBytesPerSecond) + "/sn"
            : "-";
        string peak = diagnostics.PeakReadBytesPerSecond > 0d
            ? RecoveryFileItem.FormatBytes((long)diagnostics.PeakReadBytesPerSecond) + "/sn"
            : "-";
        string unreadable = RecoveryFileItem.FormatBytes(Math.Max(0, diagnostics.UnreadableBytes));
        string source = $"Doğrulanan {report.Files.Count:N0} • I/O ort {average} • tepe {peak} • blok geçişi {diagnostics.BlockSizeChanges:N0} • yüksek güven {diagnostics.HighConfidenceFiles:N0}/{diagnostics.ScoredFiles:N0} • ort %{diagnostics.AverageConfidenceScore:0} • false-positive elenen {diagnostics.FalsePositiveRejected:N0} • okunamayan {unreadable}";
        return LocalizationService.Current.Translate(source);
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "—";

        TimeSpan value = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(99).TotalSeconds));
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours} sa {value.Minutes:00} dk";
        if (value.TotalMinutes >= 1)
            return $"{value.Minutes} dk {value.Seconds:00} sn";
        return $"{Math.Max(1, value.Seconds)} sn";
    }

    private static string BuildImageSourceName(StorageDeviceInfo device)
    {
        string source = device.PhysicalDriveNumber is int physicalDrive
            ? device.IsPartitionSource
                ? $"PhysicalDrive{physicalDrive}_PART_{Math.Max(0, device.PartitionOffsetBytes):X}"
                : device.IsWholePhysicalDisk
                    ? $"PhysicalDrive{physicalDrive}"
                    : device.RootPath.TrimEnd('\\')
            : device.RootPath.TrimEnd('\\');

        foreach (char invalid in Path.GetInvalidFileNameChars())
            source = source.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(source) ? "Source" : source;
    }

    private bool EnsureDestinationCapacityForSelection(
        IReadOnlyList<RecoveryFileItem> selected,
        string destinationPath)
    {
        RecoveryDestinationCapacityInfo capacity = _recoveryService.InspectDestinationCapacity(selected, destinationPath);
        if (!capacity.IsKnown || capacity.CanFit)
            return true;

        string selectedText = RecoveryFileItem.FormatBytes(capacity.SelectedBytes);
        string requiredText = RecoveryFileItem.FormatBytes(capacity.RequiredBytes);
        string availableText = RecoveryFileItem.FormatBytes(capacity.AvailableBytes);
        string totalText = RecoveryFileItem.FormatBytes(capacity.TotalBytes);
        string source = capacity.ExceedsTotalCapacity
            ? $"Seçilen kurtarma verisi hedef sürücünün toplam kapasitesinden büyük.\n\nSeçili veri: {selectedText}\nGüvenli gerekli alan: {requiredText}\nKullanılabilir boş alan: {availableText}\nHedef kapasitesi: {totalText}\nHedef sürücü: {capacity.TargetRoot}\n\nLütfen daha büyük bir fiziksel disk veya USB hedefi seçin."
            : $"Seçilen kurtarma verisi hedef sürücüdeki kullanılabilir boş alana sığmıyor.\n\nSeçili veri: {selectedText}\nGüvenli gerekli alan: {requiredText}\nKullanılabilir boş alan: {availableText}\nHedef kapasitesi: {totalText}\nHedef sürücü: {capacity.TargetRoot}\n\nLütfen boş alanı yeterli farklı bir fiziksel disk veya USB hedefi seçin.";

        MessageBox.Show(
            LocalizationService.Current.Translate(source),
            LocalizationService.Current.Translate("Hedef Alan Yetersiz • NSX Pro"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private readonly record struct DestinationSafetyCheck(bool IsSafe, string Message);

    private static DestinationSafetyCheck CheckDestinationSafety(
        StorageDeviceInfo sourceDevice,
        string destinationPath)
    {
        PhysicalDiskResolution sourceResolution;
        if (sourceDevice.PhysicalDriveNumber is int sourcePhysicalDrive)
        {
            // Tarama motoru bu aygıta zaten PhysicalDrive kimliği üzerinden bağlı. Kurtarma
            // anında aynı kaynağı tekrar "RAID/virtual bus" filtresinden geçirmek Intel RST,
            // bazı NVMe denetleyicileri ve kart/USB köprülerinde yanlış Ambiguous sonucuna
            // yol açabiliyor. Kaynağın bağlanmış kimliğini esas al; mounted volume ise Windows'un
            // anlık device-number bilgisini yalnız tutarlılık kontrolü için oku. Bu kontrol disk
            // taraması yapmaz ve sadece Kurtar komutuna basıldığında çalışır.
            int? liveSourcePhysicalDrive = null;
            if (!sourceDevice.IsWholePhysicalDisk &&
                !sourceDevice.IsPartitionSource &&
                !string.IsNullOrWhiteSpace(sourceDevice.RootPath))
            {
                liveSourcePhysicalDrive = VolumeDeviceNumberService.TryGetPhysicalDriveNumber(sourceDevice.RootPath);
            }

            if (liveSourcePhysicalDrive.HasValue && liveSourcePhysicalDrive.Value != sourcePhysicalDrive)
            {
                return new DestinationSafetyCheck(
                    false,
                    "Kaynak disk güvenlik kontrolü tamamlanamadı. Fiziksel disk kimliği güvenilir biçimde doğrulanamadı. " +
                    "NSX Pro belirsizlikte yazmaya izin vermez; veri kaybı riskini önlemek için işlem durduruldu. " +
                    "Tek fiziksel diske doğrudan bağlı farklı bir hedef seçin.");
            }

            sourceResolution = new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Resolved,
                sourcePhysicalDrive,
                $"PhysicalDrive{sourcePhysicalDrive} via active scan binding");
        }
        else if (sourceDevice.IsWholePhysicalDisk || sourceDevice.IsPartitionSource)
        {
            sourceResolution = new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Source physical disk number is missing.");
        }
        else
        {
            // Recovery writes never target the source. For the same-disk guard we only need
            // a stable PhysicalDrive identity. Intel RST/VMD may expose a normal single SSD
            // as BusType=RAID; that label alone must not block recovery. Accept one consistent
            // Windows volume extent/device number, while true virtual/pool and multi-extent
            // layouts remain fail-closed.
            sourceResolution = VolumeDeviceNumberService.ResolveRecoverySourcePhysicalDisk(sourceDevice.RootPath);
        }

        if (!sourceResolution.IsResolved)
        {
            return new DestinationSafetyCheck(
                false,
                BuildDestinationIdentityWarning("kaynak", sourceResolution));
        }

        PhysicalDiskResolution targetResolution = VolumeDeviceNumberService.ResolveDestinationPhysicalDisk(destinationPath);
        if (!targetResolution.IsResolved)
        {
            return new DestinationSafetyCheck(
                false,
                BuildDestinationIdentityWarning("hedef", targetResolution));
        }

        if (sourceResolution.PhysicalDriveNumber == targetResolution.PhysicalDriveNumber)
        {
            return new DestinationSafetyCheck(
                false,
                $"Seçilen hedef kaynakla aynı fiziksel diskte (PhysicalDrive{sourceResolution.PhysicalDriveNumber}). " +
                "Kurtarılabilir verilerin üzerine yazmamak için farklı bir fiziksel disk veya USB hedefi seçin.");
        }

        return new DestinationSafetyCheck(true, string.Empty);
    }

    private static string BuildDestinationIdentityWarning(
        string side,
        PhysicalDiskResolution resolution)
    {
        string topology = resolution.Status == PhysicalDiskResolutionStatus.Ambiguous
            ? "Depolama topolojisi birden fazla extent, Storage Spaces, RAID veya sanal disk katmanı nedeniyle kesin olarak tek fiziksel diske indirgenemedi."
            : "Fiziksel disk kimliği güvenilir biçimde doğrulanamadı.";

        return $"{char.ToUpperInvariant(side[0])}{side[1..]} disk güvenlik kontrolü tamamlanamadı. {topology} " +
               "NSX Pro belirsizlikte yazmaya izin vermez; veri kaybı riskini önlemek için işlem durduruldu. " +
               "Tek fiziksel diske doğrudan bağlı farklı bir hedef seçin.";
    }

    private void RaiseCommandStates()
    {
        (RefreshDevicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SelectDeviceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ShowOverviewCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetScanModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetQuickScanScopeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartQuickScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SetDeepScanScopeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetFolderScanScopeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseFolderScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RecoverSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RecoverSingleCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ToggleSelectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearRecoverySelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearResultsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ShowAllResultsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ShowResultNavigationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SetResultNavigationFilterCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleNavigationGroupCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReturnToDeviceSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ScanDesktopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ScanRecycleBinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
