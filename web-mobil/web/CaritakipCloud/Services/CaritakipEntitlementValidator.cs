using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed record CariEntitlementResult(bool IsActive, bool IsDefinitivelyInactive);

public sealed class CariEntitlementUnavailableException(Exception innerException)
    : Exception("Lisans hizmetine şu anda ulaşılamıyor. Bağlantınız korunuyor; biraz sonra yeniden deneyin.", innerException);

public sealed class CaritakipEntitlementValidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CaritakipEntitlementValidator> _logger;

    public CaritakipEntitlementValidator(
        IServiceScopeFactory scopeFactory,
        ILogger<CaritakipEntitlementValidator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<CariEntitlementResult> ValidateAsync(int licenseId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var license = await db.Licenses.AsNoTracking()
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.Id == licenseId, cancellationToken);

            if (license is null)
                return new CariEntitlementResult(false, true);

            var status = NormalizeStatus(license.LicenseStatus);
            var active = license.IsActive
                && !license.RevokedAt.HasValue
                && (!license.EndDate.HasValue || license.EndDate.Value.Date >= DateTime.Today)
                && status == "Active"
                && license.User is { IsActive: true, IsDeleted: false };
            return new CariEntitlementResult(active, !active);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Entitlement storage failures fail closed, but are not treated as revocations.
            _logger.LogError(ex, "Cari Takip lisans hakkı doğrulanamadı. LicenseId: {LicenseId}", licenseId);
            throw new CariEntitlementUnavailableException(ex);
        }
    }

    private static string NormalizeStatus(string? value)
    {
        var status = (value ?? "Active").Trim();
        if (status.Equals("Suspended", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Askida", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Askıda", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Blocked", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Engelli", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Revoked", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Iptal", StringComparison.OrdinalIgnoreCase)
            || status.Equals("İptal", StringComparison.OrdinalIgnoreCase))
            return "Inactive";
        return status.Equals("Active", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Aktif", StringComparison.OrdinalIgnoreCase) ? "Active" : "Inactive";
    }
}
