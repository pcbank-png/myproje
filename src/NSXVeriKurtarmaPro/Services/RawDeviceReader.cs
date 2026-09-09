using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Kaynak birimi yalnızca GENERIC_READ ile açar. Yazma API'si bilinçli olarak yoktur.
/// Windows ham volume erişiminde küçük/unaligned okumalar ERROR_INVALID_PARAMETER (87)
/// döndürebildiği için tüm okumalar mantıksal sektör sınırlarına hizalanır.
/// </summary>
public sealed class RawDeviceReader : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;

    private readonly FileStream _stream;
    private readonly object _sync = new();
    private readonly int _sectorSize;
    private readonly long _volumeLength;
    private readonly long _baseOffset;
    private readonly OperationPauseGate? _pauseGate;
    private readonly BadSectorHeatMap _heatMap;

    private RawDeviceReader(
        SafeFileHandle handle,
        int sectorSize,
        long volumeLength,
        long baseOffset,
        OperationPauseGate? pauseGate,
        int readerBufferSize)
    {
        _sectorSize = sectorSize;
        _volumeLength = volumeLength;
        _baseOffset = Math.Max(0, baseOffset);
        _pauseGate = pauseGate;
        _heatMap = new BadSectorHeatMap(sectorSize);
        _stream = new FileStream(handle, FileAccess.Read, Math.Clamp(readerBufferSize, 64 * 1024, 4 * 1024 * 1024), false);
    }

    public static RawDeviceReader OpenVolume(
        string rootPath,
        OperationPauseGate? pauseGate = null,
        RecoveryMediaProfile? mediaProfile = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Ham aygıt okuma yalnızca Windows'ta desteklenir.");

        string drive = rootPath.Trim().TrimEnd('\\');
        if (drive.Length < 2 || drive[1] != ':')
            throw new ArgumentException("Geçerli bir sürücü kökü bekleniyor.", nameof(rootPath));

        string root = drive + "\\";
        int sectorSize = GetLogicalSectorSize(root);

        string rawPath = $@"\\.\{drive}";
        SafeFileHandle handle = CreateFile(
            rawPath,
            GenericRead,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Aygıt salt-okunur açılamadı: {rawPath}");

        long volumeLength = TryGetRawVolumeLength(handle);
        if (volumeLength <= 0)
            volumeLength = GetVolumeLength(root);

        return new RawDeviceReader(
            handle,
            sectorSize,
            volumeLength,
            0,
            pauseGate,
            mediaProfile?.ReaderBufferSize ?? 256 * 1024);
    }


    public static RawDeviceReader OpenDevice(
        StorageDeviceInfo device,
        OperationPauseGate? pauseGate = null,
        RecoveryMediaProfile? mediaProfile = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        // StorageDeviceInfo tarama/listeme anındaki bir snapshot'tır. USB/SD çıkar-tak veya
        // sürücü harfi/PhysicalDrive yeniden kullanımı sonrası yanlış medyayı sessizce açmamak için
        // ham handle alınmadan hemen önce kimliği tekrar doğrula.
        SourceMediaIdentityService.EnsureCurrentSource(device);

        if (device.IsWholePhysicalDisk || device.IsPartitionSource)
        {
            if (device.PhysicalDriveNumber is not int physicalDriveNumber)
                throw new ArgumentException("Fiziksel disk kaynağında PhysicalDrive numarası bulunamadı.", nameof(device));

            long viewOffset = device.IsPartitionSource ? Math.Max(0, device.PartitionOffsetBytes) : 0;
            long viewLength = device.IsPartitionSource ? Math.Max(0, device.PartitionLengthBytes) : Math.Max(0, device.TotalBytes);
            return OpenPhysicalDrive(
                physicalDriveNumber,
                viewOffset,
                viewLength,
                pauseGate,
                mediaProfile);
        }

        return OpenVolume(device.RootPath, pauseGate, mediaProfile);
    }

    public static RawDeviceReader OpenPhysicalDrive(
        int physicalDriveNumber,
        long viewOffset = 0,
        long viewLength = 0,
        OperationPauseGate? pauseGate = null,
        RecoveryMediaProfile? mediaProfile = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Ham aygıt okuma yalnızca Windows'ta desteklenir.");
        if (physicalDriveNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(physicalDriveNumber));

        string rawPath = $@"\\.\PhysicalDrive{physicalDriveNumber}";
        SafeFileHandle handle = CreateFile(
            rawPath,
            GenericRead,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Fiziksel aygıt salt-okunur açılamadı: {rawPath}");

        long physicalLength = TryGetRawVolumeLength(handle);
        if (physicalLength <= 0)
        {
            handle.Dispose();
            throw new IOException($"Fiziksel aygıt uzunluğu okunamadı: {rawPath}");
        }

        long safeOffset = Math.Clamp(viewOffset, 0, physicalLength);
        long available = Math.Max(0, physicalLength - safeOffset);
        long safeLength = viewLength > 0 ? Math.Min(viewLength, available) : available;
        if (safeLength <= 0)
        {
            handle.Dispose();
            throw new IOException("Seçilen fiziksel disk/bölüm görünümü boş veya disk sınırlarının dışında.");
        }

        int sectorSize = PhysicalDriveAccessService.TryGetIdentityInfo(physicalDriveNumber, out PhysicalDriveIdentityInfo identity)
            ? identity.LogicalSectorSize
            : 512;

        return new RawDeviceReader(
            handle,
            sectorSize,
            safeLength,
            safeOffset,
            pauseGate,
            mediaProfile?.ReaderBufferSize ?? 256 * 1024);
    }

    public int SectorSize => _sectorSize;
    // Test/diagnostic disk images use the same bounded reader as physical media.
    internal static RawDeviceReader OpenImage(string path, int sectorSize = 512)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new RawDeviceReader(handle, sectorSize, RandomAccess.GetLength(handle), 0, null, 256 * 1024);
    }
    public long VolumeLength => _volumeLength;
    public BadSectorHeatMap HeatMap => _heatMap;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (buffer.Length == 0) return 0;
        if (_volumeLength > 0 && offset >= _volumeLength) return 0;

        _pauseGate?.Wait();

        lock (_sync)
        {
            if (IsAligned(offset, buffer.Length))
                return ReadStream(offset, buffer);

            long alignedOffset = offset - offset % _sectorSize;
            int prefix = checked((int)(offset - alignedOffset));
            long requestedEnd = checked(offset + buffer.Length);
            long alignedEnd = RoundUp(requestedEnd, _sectorSize);

            if (_volumeLength > 0 && alignedEnd > _volumeLength)
                alignedEnd = _volumeLength;

            long alignedLengthLong = alignedEnd - alignedOffset;
            if (alignedLengthLong <= prefix)
                return 0;

            if (alignedLengthLong > int.MaxValue)
                throw new IOException("Ham aygıt okuma bloğu desteklenen sınırı aşıyor.");

            int alignedLength = (int)alignedLengthLong;
            byte[] rented = ArrayPool<byte>.Shared.Rent(alignedLength);
            try
            {
                int totalRead = ReadStream(alignedOffset, rented.AsSpan(0, alignedLength));
                if (totalRead <= prefix)
                    return 0;

                int copyLength = Math.Min(buffer.Length, totalRead - prefix);
                rented.AsSpan(prefix, copyLength).CopyTo(buffer);
                return copyLength;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public int Read(long offset, byte[] buffer, int count)
    {
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        return Read(offset, buffer.AsSpan(0, count));
    }

    public int ReadBestEffort(long offset, Span<byte> buffer, out long unreadableBytes)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

        unreadableBytes = 0;
        if (buffer.Length == 0) return 0;
        if (_volumeLength > 0 && offset >= _volumeLength) return 0;

        int targetLength = buffer.Length;
        if (_volumeLength > 0)
            targetLength = (int)Math.Min(targetLength, _volumeLength - offset);

        if (targetLength <= 0) return 0;

        Span<byte> target = buffer[..targetLength];
        ReadRecovering(offset, target, ref unreadableBytes);
        return targetLength;
    }

    private void ReadRecovering(long offset, Span<byte> destination, ref long unreadableBytes)
    {
        if (destination.Length == 0)
            return;

        if (destination.Length <= _sectorSize && _heatMap.SafeModeRecommended &&
            _heatMap.IsKnownBad(offset, destination.Length))
        {
            destination.Clear();
            unreadableBytes += destination.Length;
            _heatMap.RecordSkippedKnownBadRead();
            return;
        }

        try
        {
            int read = Read(offset, destination);
            if (read == destination.Length)
                return;

            if (read > 0)
            {
                ReadRecovering(offset + read, destination[read..], ref unreadableBytes);
                return;
            }
        }
        catch (IOException)
        {
            // Hatalı sektör içeren geniş blokları sektör seviyesine kadar bölerek
            // okunabilen veriyi kaybetmeden devam et.
        }

        if (destination.Length <= _sectorSize)
        {
            destination.Clear();
            unreadableBytes += destination.Length;
            _heatMap.RecordFailure(offset, destination.Length);
            return;
        }

        int split = destination.Length / 2;
        split -= split % _sectorSize;
        if (split <= 0 || split >= destination.Length)
            split = Math.Min(_sectorSize, destination.Length - 1);

        ReadRecovering(offset, destination[..split], ref unreadableBytes);
        ReadRecovering(offset + split, destination[split..], ref unreadableBytes);
    }

    public BadSectorRetryResult RetryBadSectors(CancellationToken cancellationToken, int maxSectors = 4096)
    {
        int attempted = 0;
        int recovered = 0;
        long recoveredBytes = 0;
        foreach (BadSectorRange range in _heatMap.Snapshot())
        {
            long end = range.Offset + range.Length;
            for (long offset = range.Offset; offset < end && attempted < maxSectors; offset += _sectorSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempted++;
                byte[] sector = new byte[_sectorSize];
                try
                {
                    int read = Read(offset, sector);
                    if (read == sector.Length)
                    {
                        _heatMap.MarkRecovered(offset, _sectorSize);
                        recovered++;
                        recoveredBytes += _sectorSize;
                    }
                }
                catch (IOException)
                {
                }
            }
            if (attempted >= maxSectors)
                break;
        }

        return new BadSectorRetryResult(attempted, recovered, recoveredBytes);
    }

    public bool ReadExact(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = Read(offset + total, buffer[total..]);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    public byte[] ReadBytes(long offset, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        byte[] buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = Read(offset + total, buffer.AsSpan(total, count - total));
            if (read <= 0) break;
            total += read;
        }

        if (total == count) return buffer;
        Array.Resize(ref buffer, total);
        return buffer;
    }

    private int ReadStream(long offset, Span<byte> buffer)
    {
        if (offset >= _volumeLength) return 0;
        if (buffer.Length > _volumeLength - offset)
            buffer = buffer[..(int)(_volumeLength - offset)];
        _stream.Seek(checked(_baseOffset + offset), SeekOrigin.Begin);

        int total = 0;
        while (total < buffer.Length)
        {
            int read = _stream.Read(buffer[total..]);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    private bool IsAligned(long offset, int count) =>
        offset % _sectorSize == 0 && count % _sectorSize == 0;

    private static long RoundUp(long value, int alignment)
    {
        long remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static int GetLogicalSectorSize(string rootPath)
    {
        if (GetDiskFreeSpace(rootPath, out _, out uint bytesPerSector, out _, out _) &&
            bytesPerSector is >= 512 and <= 65536)
            return (int)bytesPerSector;

        return 512;
    }

    private static long TryGetRawVolumeLength(SafeFileHandle handle)
    {
        try
        {
            byte[] output = new byte[8];
            if (DeviceIoControl(
                    handle,
                    IoctlDiskGetLengthInfo,
                    IntPtr.Zero,
                    0,
                    output,
                    output.Length,
                    out int bytesReturned,
                    IntPtr.Zero) && bytesReturned >= 8)
            {
                long length = BitConverter.ToInt64(output, 0);
                return length > 0 ? length : 0;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static long GetVolumeLength(string rootPath)
    {
        try
        {
            return new DriveInfo(rootPath).TotalSize;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose() => _stream.Dispose();

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);
}
