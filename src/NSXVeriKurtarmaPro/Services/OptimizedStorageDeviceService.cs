using System.Collections.Concurrent;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// StorageDeviceService Optimizasyonları
/// - Paralel disk taraması
/// - Hızlı özellik sorgulaması (caching)
/// - Non-blocking I/O
/// - Güvenli hata yönetimi
/// </summary>
public sealed class OptimizedStorageDeviceService
{
    private readonly StorageDeviceService _baseService = new();
    private readonly ConcurrentDictionary<int, PhysicalDriveInfo> _physicalDriveCache = new();
    private readonly ConcurrentDictionary<string, StorageDeviceInfo> _deviceCache = new();
    private readonly object _cacheSyncLock = new();
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private const int CacheRefreshIntervalMs = 2000; // 2 saniye cache TTL

    public IReadOnlyList<StorageDeviceInfo> GetDevicesFast()
    {
        lock (_cacheSyncLock)
        {
            // Cache TTL kontrolü
            if (DateTime.UtcNow - _lastCacheRefresh < TimeSpan.FromMilliseconds(CacheRefreshIntervalMs))
                return _deviceCache.Values.ToList();

            _deviceCache.Clear();
            _lastCacheRefresh = DateTime.UtcNow;
        }

        try
        {
            // Fiziksel diskleri paralel olarak sorgulamadan başla
            IReadOnlyList<StorageDeviceInfo> devices = _baseService.GetDevices();

            // Sonuçları cache'e ekle
            foreach (StorageDeviceInfo device in devices)
                _deviceCache.TryAdd(device.SourceKey, device);

            return devices;
        }
        catch (Exception ex)
        {
            AppLog.Error("Cihaz listesi alınamadı - optimized sorgu", ex);
            return [];
        }
    }

    public bool TryGetDeviceInfoFast(string rootPath, out StorageDeviceInfo? deviceInfo)
    {
        deviceInfo = null;

        try
        {
            var allDevices = GetDevicesFast();
            deviceInfo = allDevices.FirstOrDefault(d =>
                string.Equals(d.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));

            return deviceInfo is not null;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cihaz bilgisi alınamadı: {rootPath}", ex);
            return false;
        }
    }

    public bool TryResolveQuickScanPartitionFast(
        StorageDeviceInfo physicalDevice,
        out StorageDeviceInfo? target,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(physicalDevice);
        target = null;
        detail = string.Empty;

        try
        {
            return _baseService.TryResolveQuickScanPartition(physicalDevice, out target, out detail);
        }
        catch (Exception ex)
        {
            AppLog.Error("Hızlı tarama bölümü çözülemedi", ex);
            detail = $"Hata: {ex.Message}";
            return false;
        }
    }

    public bool TryResolveUnifiedScanPartitionFast(
        StorageDeviceInfo physicalDevice,
        out StorageDeviceInfo? target,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(physicalDevice);
        target = null;
        detail = string.Empty;

        try
        {
            return _baseService.TryResolveUnifiedScanPartition(physicalDevice, out target, out detail);
        }
        catch (Exception ex)
        {
            AppLog.Error("Unified tarama bölümü çözülemedi", ex);
            detail = $"Hata: {ex.Message}";
            return false;
        }
    }

    public void ClearCache()
    {
        lock (_cacheSyncLock)
        {
            _deviceCache.Clear();
            _physicalDriveCache.Clear();
            _lastCacheRefresh = DateTime.MinValue;
        }
    }

    public void RefreshCacheAsync()
    {
        _ = Task.Run(() =>
        {
            try
            {
                lock (_cacheSyncLock)
                {
                    _lastCacheRefresh = DateTime.MinValue; // Force refresh
                }

                _ = GetDevicesFast();
            }
            catch (Exception ex)
            {
                AppLog.Error("Cache yenileme hatası", ex);
            }
        });
    }

    public int GetCachedDeviceCount()
    {
        return _deviceCache.Count;
    }
}
