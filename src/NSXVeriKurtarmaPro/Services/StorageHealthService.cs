using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace NSXVeriKurtarmaPro.Services;

internal enum StorageHealthSeverity
{
    Unknown = 0,
    Healthy = 1,
    Caution = 2,
    Critical = 3
}

internal readonly record struct StorageHealthInfo(
    StorageHealthSeverity Severity,
    bool SmartAvailable,
    bool PredictFailure,
    long ReallocatedSectors,
    long ReportedUncorrectable,
    long CommandTimeouts,
    long PendingSectors,
    long OfflineUncorrectable,
    long InterfaceCrcErrors,
    int? TemperatureC,
    string DetectionSource)
{
    public static StorageHealthInfo Unknown => new(
        StorageHealthSeverity.Unknown,
        false,
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        "SMART unavailable");

    public bool SafeScanRecommended => Severity is StorageHealthSeverity.Caution or StorageHealthSeverity.Critical;

    public string StatusText => Severity switch
    {
        StorageHealthSeverity.Critical => "SMART • Kritik • Safe Scan",
        StorageHealthSeverity.Caution => "SMART • Dikkat • Safe Scan",
        StorageHealthSeverity.Healthy => "SMART • İyi",
        _ => "SMART • Bilinmiyor"
    };

    public string Summary
    {
        get
        {
            if (!SmartAvailable)
                return "SMART bilgisi aygıt/USB köprüsü tarafından sunulmadı.";

            var parts = new List<string>();
            if (PredictFailure)
                parts.Add("Windows arıza öngörüsü aktif");
            if (ReallocatedSectors > 0)
                parts.Add($"Reallocated {ReallocatedSectors:N0}");
            if (PendingSectors > 0)
                parts.Add($"Pending {PendingSectors:N0}");
            if (OfflineUncorrectable > 0)
                parts.Add($"Uncorrectable {OfflineUncorrectable:N0}");
            if (ReportedUncorrectable > 0)
                parts.Add($"Reported {ReportedUncorrectable:N0}");
            if (CommandTimeouts > 0)
                parts.Add($"Timeout {CommandTimeouts:N0}");
            if (InterfaceCrcErrors > 0)
                parts.Add($"CRC {InterfaceCrcErrors:N0}");
            if (TemperatureC is int temperature)
                parts.Add($"{temperature} °C");

            return parts.Count == 0
                ? "SMART arıza öngörüsü bildirmiyor."
                : string.Join(" • ", parts);
        }
    }
}

/// <summary>
/// Best-effort SMART/drive-health reader that stays inside the Windows storage stack.
/// It never writes to the source device. USB bridges that do not expose SMART simply
/// return Unknown rather than guessing health from model/capacity heuristics.
/// </summary>
internal static class StorageHealthService
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStoragePredictFailure = 0x002D1100;

    public static StorageHealthInfo TryGetHealth(int physicalDriveNumber)
    {
        if (!OperatingSystem.IsWindows() || physicalDriveNumber < 0)
            return StorageHealthInfo.Unknown;

        using SafeFileHandle handle = CreateFile(
            $@"\\.\PhysicalDrive{physicalDriveNumber}",
            GenericRead,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return StorageHealthInfo.Unknown;

        // STORAGE_PREDICT_FAILURE = ULONG PredictFailure + 512 bytes vendor SMART data.
        byte[] output = new byte[516];
        bool ok = DeviceIoControl(
            handle,
            IoctlStoragePredictFailure,
            IntPtr.Zero,
            0,
            output,
            output.Length,
            out int returned,
            IntPtr.Zero);

        if (!ok || returned < 4)
            return StorageHealthInfo.Unknown;

        bool predictFailure = BitConverter.ToUInt32(output, 0) != 0;
        ReadOnlySpan<byte> smart = returned > 4
            ? output.AsSpan(4, Math.Min(512, returned - 4))
            : ReadOnlySpan<byte>.Empty;

        long reallocated = 0;
        long reportedUncorrectable = 0;
        long commandTimeouts = 0;
        long pending = 0;
        long offlineUncorrectable = 0;
        long crcErrors = 0;
        int? temperature = null;
        bool attributeTableSeen = false;

        // ATA SMART READ DATA: revision word + up to 30 x 12-byte attribute entries.
        if (smart.Length >= 2 + 12)
        {
            for (int index = 0; index < 30; index++)
            {
                int offset = 2 + index * 12;
                if (offset + 12 > smart.Length)
                    break;

                byte id = smart[offset];
                if (id == 0)
                    continue;

                attributeTableSeen = true;
                long raw = ReadRaw48(smart.Slice(offset + 5, 6));
                switch (id)
                {
                    case 5:
                        reallocated = raw;
                        break;
                    case 187:
                        reportedUncorrectable = raw;
                        break;
                    case 188:
                        commandTimeouts = raw;
                        break;
                    case 194:
                        int temp = smart[offset + 5];
                        if (temp is > 0 and < 100)
                            temperature = temp;
                        break;
                    case 197:
                        pending = raw;
                        break;
                    case 198:
                        offlineUncorrectable = raw;
                        break;
                    case 199:
                        crcErrors = raw;
                        break;
                }
            }
        }

        StorageHealthSeverity severity = CalculateSeverity(
            predictFailure,
            reallocated,
            reportedUncorrectable,
            commandTimeouts,
            pending,
            offlineUncorrectable,
            temperature);

        // IOCTL success without an ATA attribute table still gives us Windows' predictive
        // failure bit. Treat a clear bit as Healthy evidence, but do not invent attributes.
        if (!attributeTableSeen && !predictFailure)
            severity = StorageHealthSeverity.Healthy;

        return new StorageHealthInfo(
            severity,
            true,
            predictFailure,
            reallocated,
            reportedUncorrectable,
            commandTimeouts,
            pending,
            offlineUncorrectable,
            crcErrors,
            temperature,
            attributeTableSeen ? "IOCTL_STORAGE_PREDICT_FAILURE + ATA SMART" : "IOCTL_STORAGE_PREDICT_FAILURE");
    }

    private static StorageHealthSeverity CalculateSeverity(
        bool predictFailure,
        long reallocated,
        long reportedUncorrectable,
        long commandTimeouts,
        long pending,
        long offlineUncorrectable,
        int? temperature)
    {
        if (predictFailure ||
            pending >= 8 ||
            offlineUncorrectable >= 8 ||
            reportedUncorrectable >= 8 ||
            reallocated >= 512 ||
            temperature >= 65)
        {
            return StorageHealthSeverity.Critical;
        }

        if (pending > 0 ||
            offlineUncorrectable > 0 ||
            reportedUncorrectable > 0 ||
            reallocated > 0 ||
            commandTimeouts > 0 ||
            temperature >= 55)
        {
            return StorageHealthSeverity.Caution;
        }

        return StorageHealthSeverity.Healthy;
    }

    private static long ReadRaw48(ReadOnlySpan<byte> raw)
    {
        long value = 0;
        int count = Math.Min(6, raw.Length);
        for (int i = 0; i < count; i++)
            value |= (long)raw[i] << (8 * i);
        return value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
