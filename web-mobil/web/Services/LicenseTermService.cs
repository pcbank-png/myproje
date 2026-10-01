using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public static class LicenseTermService
    {
        public const string DugunSalonuProductCode = "NSXDUGUNSALONUPRO";

        public static bool NormalizeTerm(License license, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(license);
            if (!IsDugunSalonuLicense(license)) return false;

            bool changed = false;
            bool isLifetime = IsLifetime(license.LicenseType);

            if (isLifetime)
            {
                if (license.EndDate.HasValue)
                {
                    license.EndDate = null;
                    changed = true;
                }
            }
            else
            {
                DateTime startDate = license.StartDate == default
                    ? (license.CreatedAt == default ? now.Date : license.CreatedAt.Date)
                    : license.StartDate.Date;

                if (license.StartDate != startDate)
                {
                    license.StartDate = startDate;
                    changed = true;
                }

                if (!license.EndDate.HasValue)
                {
                    license.EndDate = startDate.AddYears(1);
                    changed = true;
                }
            }

            DateTime? expectedEndDate = license.EndDate?.Date;
            foreach (OfflineLicenseCertificate certificate in license.OfflineCertificates.Where(x => !x.IsRevoked))
            {
                if (SameDate(certificate.ExpiresAt, expectedEndDate)) continue;

                certificate.IsRevoked = true;
                certificate.RevokedAt = now;
                certificate.RevokedReason = "Lisans süresi güncellendiği için sertifika yenilendi.";
                changed = true;
            }

            return changed;
        }

        public static bool IsLifetime(string? licenseType)
        {
            string value = (licenseType ?? string.Empty).Trim();
            return value.Equals("Lifetime", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Sınırsız", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Sinirsiz", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDugunSalonuLicense(License license)
        {
            ArgumentNullException.ThrowIfNull(license);

            return NormalizeProductCode(license.ProductCode) == DugunSalonuProductCode
                   || NormalizeProductCode(license.Product?.Slug) == DugunSalonuProductCode
                   || NormalizeProductCode(license.Product?.Name) == DugunSalonuProductCode;
        }

        private static string NormalizeProductCode(string? value) =>
            new((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        private static bool SameDate(DateTime? left, DateTime? right) =>
            left?.Date == right?.Date;
    }
}
