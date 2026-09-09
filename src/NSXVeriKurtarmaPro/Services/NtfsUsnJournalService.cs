using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// NTFS USN Journal içindeki en yeni FILE_DELETE kayıtlarını bounded bir pencereyle okur.
/// Journal Windows tarafından tutulduğu sürece gerçek silinme zamanını öncelik kanıtı olarak verir.
/// Journal bulunamazsa tarama normal MFT yoluna devam eder.
/// </summary>
internal static class NtfsUsnJournalService
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;
    private const uint UsnPathReasonMask =
        UsnReasonFileCreate | UsnReasonFileDelete | UsnReasonRenameOldName | UsnReasonRenameNewName;
    private const int QueryBufferSize = 80;
    private const int ReadBufferSize = 1024 * 1024;
    private const int ReadRequestSize = 40;
    private const int UsnRecordV2Minimum = 60;
    private const long MaxPriorityJournalWindow = 16L * 1024 * 1024;

    internal sealed record JournalDeleteEvidence(
        ulong FileReferenceNumber,
        long RecordIndex,
        DateTimeOffset DeletedAt,
        ulong ParentReferenceNumber = 0,
        string FileName = "");

    internal sealed record JournalPathEvidence(
        ulong FileReferenceNumber,
        ulong ParentReferenceNumber,
        string FileName,
        DateTimeOffset Timestamp,
        uint Reason,
        uint FileAttributes = 0)
    {
        public bool IsDirectory => (FileAttributes & 0x00000010u) != 0;
    }

    public static IReadOnlyDictionary<ulong, DateTimeOffset> ReadDeleteTimes(
        string rootPath,
        CancellationToken cancellationToken,
        out IReadOnlyList<JournalDeleteEvidence> newestFirst)
    {
        newestFirst = Array.Empty<JournalDeleteEvidence>();
        string? volumePath = TryGetVolumeDevicePath(rootPath);
        if (string.IsNullOrWhiteSpace(volumePath) || !OperatingSystem.IsWindows())
            return new Dictionary<ulong, DateTimeOffset>();

        using SafeFileHandle handle = CreateFile(
            volumePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return new Dictionary<ulong, DateTimeOffset>();

        byte[] queryBuffer = new byte[QueryBufferSize];
        if (!DeviceIoControl(
                handle,
                FsctlQueryUsnJournal,
                null,
                0,
                queryBuffer,
                queryBuffer.Length,
                out int queryBytes,
                IntPtr.Zero) || queryBytes < 56)
        {
            return new Dictionary<ulong, DateTimeOffset>();
        }

        ulong journalId = BinaryPrimitives.ReadUInt64LittleEndian(queryBuffer.AsSpan(0, 8));
        long firstUsn = BinaryPrimitives.ReadInt64LittleEndian(queryBuffer.AsSpan(8, 8));
        long nextUsn = BinaryPrimitives.ReadInt64LittleEndian(queryBuffer.AsSpan(16, 8));
        if (journalId == 0 || firstUsn < 0 || nextUsn <= firstUsn)
            return new Dictionary<ulong, DateTimeOffset>();

        var deleteTimes = new Dictionary<ulong, DateTimeOffset>();
        var detailedEvidence = new Dictionary<ulong, JournalDeleteEvidence>();
        byte[] request = new byte[ReadRequestSize];
        byte[] output = new byte[ReadBufferSize];
        // Quick Scan must never wait for years of USN history before the MFT starts.
        // The journal is only a priority/date enrichment layer, so read a bounded tail
        // window and let the complete MFT scan remain the source of truth.
        long startUsn = Math.Max(firstUsn, nextUsn - MaxPriorityJournalWindow);
        int stagnantReads = 0;

        while (startUsn < nextUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(request, 0, request.Length);
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(0, 8), startUsn);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8, 4), UsnReasonFileDelete);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12, 4), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(32, 8), journalId);

            if (!DeviceIoControl(
                    handle,
                    FsctlReadUsnJournal,
                    request,
                    request.Length,
                    output,
                    output.Length,
                    out int bytesReturned,
                    IntPtr.Zero) || bytesReturned < 8)
            {
                // Journal değişmiş/silinmiş olabilir. Bu Pro öncelik katmanı opsiyoneldir;
                // ana MFT taramasını hiçbir durumda bloke etmez.
                _ = Marshal.GetLastWin32Error();
                break;
            }

            long returnedNextUsn = ParseReadBuffer(
                output.AsSpan(0, bytesReturned),
                deleteTimes,
                detailedEvidence);

            if (returnedNextUsn <= startUsn)
            {
                stagnantReads++;
                if (stagnantReads >= 2)
                    break;
            }
            else
            {
                stagnantReads = 0;
                startUsn = returnedNextUsn;
            }
        }

        newestFirst = detailedEvidence.Count > 0
            ? detailedEvidence.Values.OrderByDescending(item => item.DeletedAt).ToArray()
            : deleteTimes
                .Select(pair => new JournalDeleteEvidence(
                    pair.Key,
                    (long)(pair.Key & 0x0000FFFFFFFFFFFFUL),
                    pair.Value))
                .OrderByDescending(item => item.DeletedAt)
                .ToArray();

        return deleteTimes;
    }

    /// <summary>
    /// Reads a bounded tail of the live NTFS USN journal and keeps exact file-reference
    /// name/parent evidence. Unlike the quick delete-priority reader this is used only
    /// for path reconstruction after the MFT pass, so legacy rotational disks can recover
    /// stale/deleted folder chains without adding random seeks to the main scan.
    /// </summary>
    public static IReadOnlyDictionary<ulong, JournalPathEvidence> ReadPathHistory(
        string rootPath,
        CancellationToken cancellationToken,
        long maxJournalWindowBytes,
        out int parsedRecords)
    {
        parsedRecords = 0;
        string? volumePath = TryGetVolumeDevicePath(rootPath);
        if (string.IsNullOrWhiteSpace(volumePath) || !OperatingSystem.IsWindows())
            return new Dictionary<ulong, JournalPathEvidence>();

        using SafeFileHandle handle = CreateFile(
            volumePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return new Dictionary<ulong, JournalPathEvidence>();

        byte[] queryBuffer = new byte[QueryBufferSize];
        if (!DeviceIoControl(
                handle,
                FsctlQueryUsnJournal,
                null,
                0,
                queryBuffer,
                queryBuffer.Length,
                out int queryBytes,
                IntPtr.Zero) || queryBytes < 56)
        {
            return new Dictionary<ulong, JournalPathEvidence>();
        }

        ulong journalId = BinaryPrimitives.ReadUInt64LittleEndian(queryBuffer.AsSpan(0, 8));
        long firstUsn = BinaryPrimitives.ReadInt64LittleEndian(queryBuffer.AsSpan(8, 8));
        long nextUsn = BinaryPrimitives.ReadInt64LittleEndian(queryBuffer.AsSpan(16, 8));
        if (journalId == 0 || firstUsn < 0 || nextUsn <= firstUsn)
            return new Dictionary<ulong, JournalPathEvidence>();

        long boundedWindow = Math.Clamp(
            maxJournalWindowBytes,
            4L * 1024 * 1024,
            256L * 1024 * 1024);
        long startUsn = Math.Max(firstUsn, nextUsn - boundedWindow);
        var history = new Dictionary<ulong, JournalPathEvidence>();
        byte[] request = new byte[ReadRequestSize];
        byte[] output = new byte[ReadBufferSize];
        int stagnantReads = 0;

        while (startUsn < nextUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(request, 0, request.Length);
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(0, 8), startUsn);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8, 4), UsnPathReasonMask);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12, 4), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(32, 8), journalId);

            if (!DeviceIoControl(
                    handle,
                    FsctlReadUsnJournal,
                    request,
                    request.Length,
                    output,
                    output.Length,
                    out int bytesReturned,
                    IntPtr.Zero) || bytesReturned < 8)
            {
                _ = Marshal.GetLastWin32Error();
                break;
            }

            long returnedNextUsn = ParsePathHistoryBuffer(
                output.AsSpan(0, bytesReturned),
                history,
                ref parsedRecords);

            if (returnedNextUsn <= startUsn)
            {
                stagnantReads++;
                if (stagnantReads >= 2)
                    break;
            }
            else
            {
                stagnantReads = 0;
                startUsn = returnedNextUsn;
            }
        }

        return history;
    }

    internal static long ParsePathHistoryBuffer(
        ReadOnlySpan<byte> output,
        Dictionary<ulong, JournalPathEvidence> history,
        ref int parsedRecords)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (output.Length < 8)
            return 0;

        long returnedNextUsn = BinaryPrimitives.ReadInt64LittleEndian(output.Slice(0, 8));
        int position = 8;
        while (position + 8 <= output.Length)
        {
            uint recordLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(position, 4));
            if (recordLengthRaw < UsnRecordV2Minimum || recordLengthRaw > int.MaxValue)
                break;

            int recordLength = (int)recordLengthRaw;
            if (position + recordLength > output.Length)
                break;

            ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(output.Slice(position + 4, 2));
            if (majorVersion == 2 && recordLength >= UsnRecordV2Minimum)
            {
                ulong fileReference = BinaryPrimitives.ReadUInt64LittleEndian(output.Slice(position + 8, 8));
                ulong parentReference = BinaryPrimitives.ReadUInt64LittleEndian(output.Slice(position + 16, 8));
                long timestampRaw = BinaryPrimitives.ReadInt64LittleEndian(output.Slice(position + 32, 8));
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(position + 40, 4));
                uint fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(position + 52, 4));
                ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(output.Slice(position + 56, 2));
                ushort fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(output.Slice(position + 58, 2));

                if ((reason & UsnPathReasonMask) != 0 && fileReference != 0 && parentReference != 0 && timestampRaw > 0)
                {
                    try
                    {
                        DateTimeOffset timestamp = DateTimeOffset.FromFileTime(timestampRaw).ToUniversalTime();
                        int nameStart = position + fileNameOffset;
                        if (timestamp.Year >= 1970 && timestamp <= DateTimeOffset.UtcNow.AddYears(2) &&
                            fileNameLength > 0 && fileNameOffset >= UsnRecordV2Minimum &&
                            nameStart >= position && nameStart + fileNameLength <= position + recordLength)
                        {
                            string fileName = System.Text.Encoding.Unicode.GetString(
                                output.Slice(nameStart, fileNameLength)).TrimEnd('\0');
                            if (NtfsQuickScanService.IsUsableName(fileName))
                            {
                                var candidate = new JournalPathEvidence(
                                    fileReference,
                                    parentReference,
                                    fileName,
                                    timestamp,
                                    reason,
                                    fileAttributes);
                                parsedRecords++;

                                if (!history.TryGetValue(fileReference, out JournalPathEvidence? existing) ||
                                    existing is null || IsBetterPathEvidence(candidate, existing))
                                {
                                    history[fileReference] = candidate;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            position += recordLength;
        }

        return returnedNextUsn;
    }

    private static bool IsBetterPathEvidence(JournalPathEvidence candidate, JournalPathEvidence existing)
    {
        if (candidate.Timestamp != existing.Timestamp)
            return candidate.Timestamp > existing.Timestamp;

        return GetPathReasonPriority(candidate.Reason) > GetPathReasonPriority(existing.Reason);
    }

    private static int GetPathReasonPriority(uint reason)
    {
        if ((reason & UsnReasonFileDelete) != 0) return 4;
        if ((reason & UsnReasonRenameNewName) != 0) return 3;
        if ((reason & UsnReasonRenameOldName) != 0) return 2;
        if ((reason & UsnReasonFileCreate) != 0) return 1;
        return 0;
    }

    internal static long ParseReadBuffer(
        ReadOnlySpan<byte> output,
        Dictionary<ulong, DateTimeOffset> deleteTimes,
        Dictionary<ulong, JournalDeleteEvidence>? detailedEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(deleteTimes);
        if (output.Length < 8)
            return 0;

        long returnedNextUsn = BinaryPrimitives.ReadInt64LittleEndian(output.Slice(0, 8));
        int position = 8;
        while (position + 8 <= output.Length)
        {
            uint recordLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(position, 4));
            if (recordLengthRaw < UsnRecordV2Minimum || recordLengthRaw > int.MaxValue)
                break;

            int recordLength = (int)recordLengthRaw;
            if (position + recordLength > output.Length)
                break;

            ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(output.Slice(position + 4, 2));
            if (majorVersion == 2 && recordLength >= UsnRecordV2Minimum)
            {
                ulong fileReference = BinaryPrimitives.ReadUInt64LittleEndian(output.Slice(position + 8, 8));
                ulong parentReference = BinaryPrimitives.ReadUInt64LittleEndian(output.Slice(position + 16, 8));
                long timestampRaw = BinaryPrimitives.ReadInt64LittleEndian(output.Slice(position + 32, 8));
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(position + 40, 4));
                ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(output.Slice(position + 56, 2));
                ushort fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(output.Slice(position + 58, 2));

                if ((reason & UsnReasonFileDelete) != 0 && timestampRaw > 0)
                {
                    try
                    {
                        DateTimeOffset deletedAt = DateTimeOffset.FromFileTime(timestampRaw).ToUniversalTime();
                        if (deletedAt.Year >= 1970 && deletedAt <= DateTimeOffset.UtcNow.AddYears(2))
                        {
                            if (!deleteTimes.TryGetValue(fileReference, out DateTimeOffset existing) || deletedAt > existing)
                                deleteTimes[fileReference] = deletedAt;

                            string fileName = string.Empty;
                            int nameStart = position + fileNameOffset;
                            if (fileNameLength > 0 && fileNameOffset >= UsnRecordV2Minimum &&
                                nameStart >= position && nameStart + fileNameLength <= position + recordLength)
                            {
                                fileName = System.Text.Encoding.Unicode.GetString(
                                    output.Slice(nameStart, fileNameLength)).TrimEnd('\0');
                            }

                            if (detailedEvidence is not null &&
                                (!detailedEvidence.TryGetValue(fileReference, out JournalDeleteEvidence? existingEvidence) ||
                                 existingEvidence is null ||
                                 deletedAt > existingEvidence.DeletedAt))
                            {
                                detailedEvidence[fileReference] = new JournalDeleteEvidence(
                                    fileReference,
                                    (long)(fileReference & 0x0000FFFFFFFFFFFFUL),
                                    deletedAt,
                                    parentReference,
                                    fileName);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            position += recordLength;
        }

        return returnedNextUsn;
    }

    private static string? TryGetVolumeDevicePath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return null;

        string root = rootPath.Trim();
        if (root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':')
            return $@"\\.\{char.ToUpperInvariant(root[0])}:";

        return null;
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
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
