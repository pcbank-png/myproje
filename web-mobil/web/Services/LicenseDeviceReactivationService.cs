using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public static class LicenseDeviceReactivationService
    {
        public static IReadOnlyCollection<string> TransferToVerifiedInstallation(
            License license,
            string newMachineId,
            int maxDeviceCount,
            DateTime now)
        {
            var activeOtherDevices = license.Devices
                .Where(x => !x.IsBlocked &&
                            !x.IsRejected &&
                            !string.Equals(x.MachineId, newMachineId, StringComparison.Ordinal))
                .OrderBy(x => x.LastSeenAt)
                .ThenBy(x => x.FirstActivatedAt)
                .ThenBy(x => x.Id)
                .ToList();

            var retireCount = maxDeviceCount <= 1
                ? activeOtherDevices.Count
                : Math.Max(1, activeOtherDevices.Count - maxDeviceCount + 1);

            var retiredMachineIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var oldDevice in activeOtherDevices.Take(retireCount))
            {
                oldDevice.DeviceStatus = "ReplacedAfterReinstall";
                oldDevice.IsRejected = true;
                oldDevice.BlockReason = "Doğrulanmış e-posta ve lisans anahtarıyla format/cihaz sonrası yeniden aktivasyon yapıldı.";
                oldDevice.LastSeenAt = now;
                retiredMachineIds.Add(oldDevice.MachineId);
            }

            foreach (var certificate in license.OfflineCertificates.Where(x =>
                         !x.IsRevoked &&
                         x.MachineId != null &&
                         retiredMachineIds.Contains(x.MachineId)))
            {
                certificate.IsRevoked = true;
                certificate.RevokedAt = now;
                certificate.RevokedReason = "Lisans yeni kuruluma aktarıldı.";
            }

            if (maxDeviceCount <= 1 || retiredMachineIds.Contains((license.MachineId ?? string.Empty).Trim()))
                license.MachineId = newMachineId;

            return retiredMachineIds;
        }
    }
}
