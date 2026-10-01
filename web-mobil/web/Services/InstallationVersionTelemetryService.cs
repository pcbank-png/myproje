using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services;

/// <summary>
/// Tum NSX urunleri icin surum telemetrisini lisans/cihaz kaydina guvenli bicimde baglar.
/// Guncelleme kontrolunde lisans anahtari yoksa yalnizca urun + IP + yakin zaman eslesmesi
/// tek bir lisansi isaret ediyorsa kayit yapar; belirsiz eslesmede veri yazmaz.
/// </summary>
public sealed class InstallationVersionTelemetryService
{
    private static readonly TimeSpan AttributionWindow = TimeSpan.FromMinutes(20);

    private readonly ApplicationDbContext _context;
    private readonly ClientIpService _clientIpService;
    private readonly ILogger<InstallationVersionTelemetryService> _logger;

    public InstallationVersionTelemetryService(
        ApplicationDbContext context,
        ClientIpService clientIpService,
        ILogger<InstallationVersionTelemetryService> logger)
    {
        _context = context;
        _clientIpService = clientIpService;
        _logger = logger;
    }

    public async Task CaptureFromUpdateCheckAsync(
        HttpContext httpContext,
        IReadOnlyCollection<string> acceptedProductCodes,
        string? version,
        string? explicitLicenseKey = null,
        string? explicitMachineId = null)
    {
        try
        {
            var normalizedVersion = AppVersionResolver.Resolve(httpContext, version);
            if (string.IsNullOrWhiteSpace(normalizedVersion) || acceptedProductCodes.Count == 0)
                return;

            var normalizedCodes = acceptedProductCodes
                .Select(NormalizeProductCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (normalizedCodes.Count == 0)
                return;

            var licenseKey = AppVersionResolver.ReadLicenseKey(httpContext, explicitLicenseKey);
            var machineId = AppVersionResolver.ReadMachineId(httpContext, explicitMachineId);
            var ipAddress = _clientIpService.GetClientIp(httpContext);

            License? license = null;
            if (!string.IsNullOrWhiteSpace(licenseKey))
            {
                var byKey = await _context.Licenses
                    .Include(x => x.Product)
                    .Include(x => x.Devices)
                    .FirstOrDefaultAsync(x => x.LicenseKey == licenseKey);

                if (byKey != null && MatchesProduct(byKey, normalizedCodes))
                    license = byKey;
            }

            if (license == null && !string.IsNullOrWhiteSpace(ipAddress))
            {
                var cutoff = DateTime.Now.Subtract(AttributionWindow);
                var recentByIp = await _context.Licenses
                    .Include(x => x.Product)
                    .Include(x => x.Devices)
                    .Where(x => x.LastIpAddress == ipAddress && x.LastCheckedAt != null && x.LastCheckedAt >= cutoff)
                    .ToListAsync();

                var matches = recentByIp.Where(x => MatchesProduct(x, normalizedCodes)).Take(2).ToList();
                if (matches.Count == 1)
                    license = matches[0];
            }

            if (license == null)
                return;

            var changed = false;
            if (!string.Equals(license.LastAppVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
            {
                license.LastAppVersion = normalizedVersion;
                changed = true;
            }

            var device = ResolveDevice(license, machineId, ipAddress);
            if (device != null && !string.Equals(device.AppVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
            {
                device.AppVersion = normalizedVersion;
                changed = true;
            }

            // Update kontrolu bir lisans heartbeat'i degildir. Son kullanim/LastSeenAt alanlarini
            // bilerek degistirmiyoruz; sadece surum bilgisini zenginlestiriyoruz.
            if (changed)
                await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Surum telemetrisi guncelleme kontrolunu asla bozmamali.
            _logger.LogDebug(ex, "NSX surum telemetrisi kaydedilemedi.");
        }
    }

    private static LicenseDevice? ResolveDevice(License license, string? machineId, string? ipAddress)
    {
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            var exact = license.Devices.FirstOrDefault(x =>
                string.Equals(x.MachineId, machineId, StringComparison.Ordinal));
            if (exact != null)
                return exact;
        }

        if (string.IsNullOrWhiteSpace(ipAddress))
            return null;

        var cutoff = DateTime.Now.Subtract(AttributionWindow);
        var candidates = license.Devices
            .Where(x => !x.IsBlocked &&
                        !x.IsRejected &&
                        x.LastSeenAt >= cutoff &&
                        string.Equals(x.LastIpAddress, ipAddress, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastSeenAt)
            .Take(2)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool MatchesProduct(License license, HashSet<string> acceptedCodes)
    {
        var candidates = new[]
        {
            license.ProductCode,
            license.Product?.ProductCode,
            license.Product?.Slug
        };

        return candidates
            .Select(NormalizeProductCode)
            .Any(x => !string.IsNullOrWhiteSpace(x) && acceptedCodes.Contains(x));
    }

    private static string NormalizeProductCode(string? value)
        => new((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
}
