using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Kurtarma adaylarında mümkün olan en güvenilir gerçek çekim/oluşturma zamanını belirler.
/// Öncelik gömülü medya/doküman metadata'sıdır; dosya sistemi oluşturma zamanı yalnız fallback'tir.
/// Kaynak aygıt salt-okunur açılır, hiçbir veri değiştirilmez.
/// </summary>
public static partial class MediaCreationTimeService
{
    private const int MaxMetadataProbe = 8 * 1024 * 1024;
    private static readonly DateTimeOffset Mp4Epoch = new(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MatroskaEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string FileSystemFallbackSource = "Dosya sistemi oluşturma zamanı";

    public readonly record struct CaptureDateProbeResult(
        RecoveryFileItem Item,
        DateTimeOffset? CapturedAt,
        string? Source);

    public static void Populate(
        StorageDeviceInfo device,
        IReadOnlyList<RecoveryFileItem> items,
        CancellationToken cancellationToken = default)
    {
        foreach (CaptureDateProbeResult result in Probe(device, items, cancellationToken))
            Apply(result);
    }

    /// <summary>
    /// Tarih bilgisini arka planda çözmek isteyen canlı tarama akışları için sonucu
    /// nesnelere dokunmadan döndürür. Böylece WPF bağları yalnız UI thread üzerinde
    /// güncellenebilir. Dosya sistemi tarihi varsa fallback korunur; gömülü EXIF/container
    /// zamanı bulunduğunda daha güvenilir kaynak olarak onun üzerine yükseltilir.
    /// </summary>
    public static IReadOnlyList<CaptureDateProbeResult> Probe(
        StorageDeviceInfo device,
        IReadOnlyList<RecoveryFileItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(items);

        RawDeviceReader? raw = null;
        try
        {
            if (items.Any(item => NeedsMetadataProbe(item) && NeedsRawReader(item)))
                raw = RawDeviceReader.OpenDevice(device, mediaProfile: RecoveryMediaProfileService.Create(device));
        }
        catch
        {
            // Metadata okunamasa bile dosya sistemi timestamp fallback'i korunur.
        }

        try
        {
            var resolved = new List<CaptureDateProbeResult>(items.Count);
            foreach (RecoveryFileItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                resolved.Add(ProbeOne(raw, item));
            }
            return resolved;
        }
        finally
        {
            raw?.Dispose();
        }
    }

    /// <summary>
    /// Dosya sistemi kaydından gelen tarih canlı listeye anında düşsün diye yalnız hızlı
    /// fallback uygular. Sonraki Probe/Populate çağrısı gömülü metadata bulursa bu değeri
    /// gerçek kamera/dosya metadata tarihiyle yükseltir.
    /// </summary>
    public static void PopulateFileSystemFallback(
        IReadOnlyList<RecoveryFileItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (RecoveryFileItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.CapturedAt.HasValue &&
                item.FileSystemCreatedAt.HasValue &&
                IsPlausible(item.FileSystemCreatedAt.Value))
            {
                item.CapturedAt = item.FileSystemCreatedAt;
                item.CaptureDateSource = FileSystemFallbackSource;
            }
        }
    }

    public static bool NeedsMetadataProbe(RecoveryFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !item.CapturedAt.HasValue ||
               string.Equals(item.CaptureDateSource, FileSystemFallbackSource, StringComparison.Ordinal);
    }

    private static CaptureDateProbeResult ProbeOne(RawDeviceReader? raw, RecoveryFileItem item)
    {
        DateTimeOffset? capturedAt = item.CapturedAt;
        string? source = item.CaptureDateSource;

        if (!NeedsMetadataProbe(item))
            return new CaptureDateProbeResult(item, capturedAt, source);

        try
        {
            var reader = new ItemReader(raw, item);
            if (TryReadEmbeddedTime(reader, item.Extension, out DateTimeOffset embedded, out string embeddedSource))
                return new CaptureDateProbeResult(item, embedded, embeddedSource);
        }
        catch
        {
            // Aşağıdaki güvenli fallback korunur.
        }

        if (!capturedAt.HasValue &&
            item.FileSystemCreatedAt.HasValue &&
            IsPlausible(item.FileSystemCreatedAt.Value))
        {
            capturedAt = item.FileSystemCreatedAt;
            source = FileSystemFallbackSource;
        }

        return new CaptureDateProbeResult(item, capturedAt, source);
    }

    private static void Apply(CaptureDateProbeResult result)
    {
        if (!result.CapturedAt.HasValue)
            return;

        result.Item.CapturedAt = result.CapturedAt;
        result.Item.CaptureDateSource = result.Source;
    }

    private static bool NeedsRawReader(RecoveryFileItem item) =>
        item.SourceKind != RecoverySourceKind.NtfsResident;

    private static bool TryReadEmbeddedTime(ItemReader reader, string extension, out DateTimeOffset value, out string source)
    {
        value = default;
        source = string.Empty;
        string ext = FileTypeHelper.Normalize(extension);

        if (TryReadExifFamily(reader, ext, out value))
        {
            source = "EXIF çekim zamanı";
            return true;
        }

        if (ext is "MP4" or "M4V" or "MOV" or "QT" or "3GP" or "3G2" or "F4V" or "HEIC" or "HEIF" or "AVIF")
        {
            if (TryReadIsoBmffCreationTime(reader, out value, out bool quickTimeMetadata))
            {
                source = quickTimeMetadata ? "QuickTime çekim metadata'sı" : "MP4/MOV oluşturma zamanı";
                return true;
            }
        }

        if (ext is "AVI" or "DIVX" or "XVID")
        {
            if (TryReadAviIdit(reader, out value))
            {
                source = "AVI kamera IDIT zamanı";
                return true;
            }
        }

        if (ext is "MKV" or "WEBM")
        {
            if (TryReadMatroskaDateUtc(reader, out value))
            {
                source = "Matroska DateUTC";
                return true;
            }
        }

        if (ext is "ASF" or "WMV" or "DVR-MS" or "WTV")
        {
            if (TryReadAsfCreationTime(reader, out value))
            {
                source = "ASF oluşturma zamanı";
                return true;
            }
        }

        if (ext == "PDF" && TryReadPdfCreationTime(reader, out value))
        {
            source = "PDF CreationDate";
            return true;
        }

        if (ext is "DOCX" or "XLSX" or "PPTX")
        {
            if (TryReadOpenXmlCreationTime(reader, out value))
            {
                source = "Office belge oluşturma zamanı";
                return true;
            }
        }

        if (ext is "DCM" or "DICOM")
        {
            if (TryReadDicomAcquisitionTime(reader, out value))
            {
                source = "DICOM çekim zamanı";
                return true;
            }
        }

        if (ext is "SVG" or "SVGZ" && TryReadTextMetadataDate(reader, out value))
        {
            source = "Belge metadata zamanı";
            return true;
        }

        return false;
    }

    private static bool TryReadExifFamily(ItemReader reader, string ext, out DateTimeOffset value)
    {
        value = default;
        if (!FileTypeHelper.IsPhoto(ext))
            return false;

        int probeLimit = ext is "3FR" or "ARW" or "CR2" or "CR3" or "DNG" or "ERF" or "IIQ" or "KDC" or "MEF" or "MOS" or "MRW" or "NEF" or "NRW" or "ORF" or "PEF" or "RAF" or "RAW" or "RW2" or "RWL" or "SR2" or "SRF" or "SRW" or "X3F"
            ? 4 * 1024 * 1024
            : 1024 * 1024;
        byte[] probe = reader.ReadPrefix((int)Math.Min(reader.Length, (long)probeLimit));
        if (probe.Length < 8)
            return false;

        if (probe[0] == 0xFF && probe[1] == 0xD8)
        {
            int p = 2;
            while (p + 4 <= probe.Length)
            {
                if (probe[p] != 0xFF) { p++; continue; }
                byte marker = probe[p + 1];
                p += 2;
                if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
                    continue;
                if (p + 2 > probe.Length) break;
                int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(probe.AsSpan(p, 2));
                if (segmentLength < 2 || p + segmentLength > probe.Length) break;
                if (marker == 0xE1 && segmentLength >= 8 &&
                    probe.AsSpan(p + 2, 6).SequenceEqual("Exif\0\0"u8))
                {
                    return TryReadTiffExif(probe.AsSpan(p + 8, segmentLength - 8), out value);
                }
                p += segmentLength;
            }
        }

        if (probe.AsSpan(0, 4).SequenceEqual("RIFF"u8) && probe.Length >= 12 &&
            probe.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            int p = 12;
            while (p + 8 <= probe.Length)
            {
                ReadOnlySpan<byte> type = probe.AsSpan(p, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(p + 4, 4));
                int payload = p + 8;
                if (size > int.MaxValue || payload + (long)size > probe.Length) break;
                if (type.SequenceEqual("EXIF"u8))
                {
                    ReadOnlySpan<byte> exif = probe.AsSpan(payload, (int)size);
                    if (exif.Length >= 6 && exif[..6].SequenceEqual("Exif\0\0"u8))
                        exif = exif[6..];
                    return TryReadTiffExif(exif, out value);
                }
                p = payload + (int)size + ((int)size & 1);
            }
        }

        if (probe.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            int p = 8;
            while (p + 12 <= probe.Length)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(probe.AsSpan(p, 4));
                if (length > int.MaxValue || p + 12L + length > probe.Length) break;
                if (probe.AsSpan(p + 4, 4).SequenceEqual("eXIf"u8))
                    return TryReadTiffExif(probe.AsSpan(p + 8, (int)length), out value);
                p += 12 + (int)length;
            }
        }

        if ((probe[0] == (byte)'I' && probe[1] == (byte)'I' && probe[2] == 42 && probe[3] == 0) ||
            (probe[0] == (byte)'M' && probe[1] == (byte)'M' && probe[2] == 0 && probe[3] == 42))
            return TryReadTiffExif(probe, out value);

        int exifIndex = IndexOf(probe, "Exif\0\0"u8);
        if (exifIndex >= 0 && exifIndex + 14 <= probe.Length)
            return TryReadTiffExif(probe.AsSpan(exifIndex + 6), out value);

        return false;
    }

    private static bool TryReadTiffExif(ReadOnlySpan<byte> tiff, out DateTimeOffset value)
    {
        value = default;
        if (tiff.Length < 8)
            return false;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return false;

        ushort magic = ReadU16(tiff.Slice(2, 2), little);
        if (magic != 42) return false;
        uint ifd0 = ReadU32(tiff.Slice(4, 4), little);
        if (ifd0 >= tiff.Length) return false;

        string? fallback = null;
        uint exifIfd = 0;
        if (!TryReadIfd(tiff, ifd0, little, ref fallback, ref exifIfd, out string? original, out string? offset))
            return false;
        if (TryParseExifDate(original, offset, out value)) return true;

        if (exifIfd > 0 && exifIfd < tiff.Length)
        {
            string? ignoredFallback = fallback;
            uint ignored = 0;
            if (TryReadIfd(tiff, exifIfd, little, ref ignoredFallback, ref ignored, out original, out offset) &&
                TryParseExifDate(original, offset, out value))
                return true;
            fallback = ignoredFallback;
        }

        return TryParseExifDate(fallback, null, out value);
    }

    private static bool TryReadIfd(
        ReadOnlySpan<byte> tiff,
        uint ifdOffset,
        bool little,
        ref string? fallback,
        ref uint exifIfd,
        out string? original,
        out string? offsetTime)
    {
        original = null;
        offsetTime = null;
        if (ifdOffset + 2 > tiff.Length) return false;
        int p = checked((int)ifdOffset);
        ushort count = ReadU16(tiff.Slice(p, 2), little);
        p += 2;
        if (count > 4096 || p + count * 12L > tiff.Length) return false;

        for (int i = 0; i < count; i++, p += 12)
        {
            ushort tag = ReadU16(tiff.Slice(p, 2), little);
            ushort type = ReadU16(tiff.Slice(p + 2, 2), little);
            uint components = ReadU32(tiff.Slice(p + 4, 4), little);
            uint rawValue = ReadU32(tiff.Slice(p + 8, 4), little);

            if (tag == 0x8769 && type == 4 && components == 1)
                exifIfd = rawValue;

            if (tag is 0x0132 or 0x9003 or 0x9004 or 0x9010 or 0x9011 or 0x9012)
            {
                string? text = ReadTiffAscii(tiff, p + 8, type, components, rawValue, little);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (tag == 0x9003) original = text;
                else if (tag == 0x9011) offsetTime = text;
                else if (tag is 0x0132 or 0x9004) fallback ??= text;
            }
        }
        return true;
    }

    private static string? ReadTiffAscii(ReadOnlySpan<byte> tiff, int valueFieldOffset, ushort type, uint count, uint offset, bool little)
    {
        if (type != 2 || count == 0 || count > 1024) return null;
        ReadOnlySpan<byte> bytes;
        if (count <= 4)
            bytes = tiff.Slice(valueFieldOffset, checked((int)count));
        else
        {
            if (offset + count > tiff.Length) return null;
            bytes = tiff.Slice(checked((int)offset), checked((int)count));
        }
        return Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
    }

    private static bool TryParseExifDate(string? text, string? offsetText, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] formats = ["yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss"];
        if (!DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            return false;
        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(date);
        if (!string.IsNullOrWhiteSpace(offsetText) && TimeSpan.TryParse(offsetText.Trim(), CultureInfo.InvariantCulture, out TimeSpan parsed))
            offset = parsed;
        value = new DateTimeOffset(date, offset);
        return IsPlausible(value);
    }

    private static bool TryReadIsoBmffCreationTime(ItemReader reader, out DateTimeOffset value, out bool quickTimeMetadata)
    {
        value = default;
        quickTimeMetadata = false;
        long position = 0;
        int boxes = 0;
        byte[] headerBuffer = new byte[16];
        while (position + 8 <= reader.Length && boxes++ < 100000)
        {
            Span<byte> header = headerBuffer;
            int read = reader.ReadAt(position, header);
            if (read < 8) break;
            ulong boxSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            int headerSize = 8;
            if (boxSize == 1)
            {
                if (read < 16) break;
                boxSize = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                headerSize = 16;
            }
            else if (boxSize == 0)
                boxSize = (ulong)(reader.Length - position);

            if (boxSize < (ulong)headerSize || boxSize > (ulong)(reader.Length - position)) break;
            if (type == "moov")
            {
                int probeLength = (int)Math.Min((ulong)MaxMetadataProbe, boxSize);
                byte[] moov = reader.ReadBytes(position, probeLength);
                if (TryReadQuickTimeTextDate(moov, out value))
                {
                    quickTimeMetadata = true;
                    return true;
                }
                if (TryReadMvhdDate(moov, out value))
                    return true;
            }
            position += checked((long)boxSize);
        }

        // Hasarlı atom zincirinde moov sona taşınmış olabilir. Son bölümü doğrudan taramak,
        // yanlış boyutlu önceki atom yüzünden gerçek kamera zamanını kaçırmamayı sağlar.
        if (reader.Length > 0)
        {
            int tailLength = (int)Math.Min(reader.Length, (long)MaxMetadataProbe);
            byte[] tail = reader.ReadBytes(Math.Max(0, reader.Length - tailLength), tailLength);
            if (TryReadQuickTimeTextDate(tail, out value))
            {
                quickTimeMetadata = true;
                return true;
            }
            if (TryReadMvhdDate(tail, out value))
                return true;
        }
        return false;
    }

    private static bool TryReadMvhdDate(byte[] bytes, out DateTimeOffset value)
    {
        value = default;
        ReadOnlySpan<byte> needle = "mvhd"u8;
        int start = 0;
        while (start + 12 <= bytes.Length)
        {
            int rel = IndexOf(bytes.AsSpan(start), needle);
            if (rel < 0) break;
            int p = start + rel + 4;
            if (p + 8 <= bytes.Length)
            {
                byte version = bytes[p];
                ulong seconds;
                if (version == 1 && p + 12 <= bytes.Length)
                    seconds = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(p + 4, 8));
                else if (version == 0)
                    seconds = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p + 4, 4));
                else { start = p; continue; }
                try
                {
                    DateTimeOffset candidate = Mp4Epoch.AddSeconds(seconds);
                    if (IsPlausible(candidate)) { value = candidate; return true; }
                }
                catch { }
            }
            start = p;
        }
        return false;
    }

    private static bool TryReadQuickTimeTextDate(byte[] bytes, out DateTimeOffset value)
    {
        value = default;
        string text = Encoding.UTF8.GetString(bytes);
        foreach (Match match in IsoDateRegex().Matches(text))
        {
            if (DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out DateTimeOffset candidate) &&
                IsPlausible(candidate))
            {
                int begin = Math.Max(0, match.Index - 256);
                string context = text.Substring(begin, match.Index - begin);
                if (context.Contains("creationdate", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("day", StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryReadAviIdit(ItemReader reader, out DateTimeOffset value)
    {
        value = default;
        byte[] bytes = reader.ReadPrefix((int)Math.Min(reader.Length, 4L * 1024 * 1024));
        int index = IndexOf(bytes, "IDIT"u8);
        if (index < 0 || index + 8 > bytes.Length) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index + 4, 4));
        if (length <= 0 || length > 256 || index + 8L + length > bytes.Length) return false;
        string text = Encoding.ASCII.GetString(bytes, index + 8, length).Trim('\0', ' ');
        string[] formats = ["ddd MMM dd HH:mm:ss yyyy", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss"];
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime dt) &&
            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
            return false;
        dt = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        value = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        return IsPlausible(value);
    }

    private static bool TryReadMatroskaDateUtc(ItemReader reader, out DateTimeOffset value)
    {
        value = default;
        byte[] bytes = reader.ReadPrefix((int)Math.Min(reader.Length, 4L * 1024 * 1024));
        for (int i = 0; i + 11 <= bytes.Length; i++)
        {
            if (bytes[i] != 0x44 || bytes[i + 1] != 0x61) continue;
            if (!TryReadEbmlSize(bytes.AsSpan(i + 2), out ulong size, out int sizeBytes) || size != 8) continue;
            int p = i + 2 + sizeBytes;
            if (p + 8 > bytes.Length) continue;
            long nanos = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(p, 8));
            try
            {
                DateTimeOffset candidate = MatroskaEpoch.AddTicks(nanos / 100);
                if (IsPlausible(candidate)) { value = candidate; return true; }
            }
            catch { }
        }
        return false;
    }

    private static bool TryReadEbmlSize(ReadOnlySpan<byte> span, out ulong value, out int bytes)
    {
        value = 0; bytes = 0;
        if (span.IsEmpty || span[0] == 0) return false;
        byte mask = 0x80;
        int length = 1;
        while (length <= 8 && (span[0] & mask) == 0) { mask >>= 1; length++; }
        if (length > 8 || span.Length < length) return false;
        value = (ulong)(span[0] & (mask - 1));
        for (int i = 1; i < length; i++) value = (value << 8) | span[i];
        bytes = length;
        return true;
    }

    private static bool TryReadAsfCreationTime(ItemReader reader, out DateTimeOffset value)
    {
        value = default;
        byte[] bytes = reader.ReadPrefix((int)Math.Min(reader.Length, 2L * 1024 * 1024));
        ReadOnlySpan<byte> guid = new byte[] { 0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65 };
        int index = IndexOf(bytes, guid);
        if (index < 0 || index + 56 > bytes.Length) return false;
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(index + 48, 8));
        if (fileTime <= 0) return false;
        try
        {
            DateTimeOffset candidate = DateTimeOffset.FromFileTime(fileTime);
            if (IsPlausible(candidate)) { value = candidate; return true; }
        }
        catch { }
        return false;
    }

    private static bool TryReadPdfCreationTime(ItemReader reader, out DateTimeOffset value)
    {
        value = default;
        byte[] first = reader.ReadPrefix((int)Math.Min(reader.Length, 1024L * 1024));
        string text = Encoding.Latin1.GetString(first);
        Match match = PdfCreationRegex().Match(text);
        if (!match.Success && reader.Length > first.Length)
        {
            int tailLen = (int)Math.Min(reader.Length, 1024L * 1024);
            text = Encoding.Latin1.GetString(reader.ReadBytes(reader.Length - tailLen, tailLen));
            match = PdfCreationRegex().Match(text);
        }
        return match.Success && TryParsePdfDate(match.Groups[1].Value, out value);
    }

    private static bool TryParsePdfDate(string text, out DateTimeOffset value)
    {
        value = default;
        string s = text.Trim();
        if (s.StartsWith("D:", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        Match m = Regex.Match(s, @"^(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:([+\-Z])(\d{2})'?((?:\d{2})?)'?)?");
        if (!m.Success) return false;
        int year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int month = ParseOr(m.Groups[2].Value, 1), day = ParseOr(m.Groups[3].Value, 1);
        int hour = ParseOr(m.Groups[4].Value, 0), minute = ParseOr(m.Groups[5].Value, 0), second = ParseOr(m.Groups[6].Value, 0);
        try
        {
            DateTime dt = new(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(dt);
            string sign = m.Groups[7].Value;
            if (sign == "Z") offset = TimeSpan.Zero;
            else if (sign is "+" or "-")
            {
                int oh = ParseOr(m.Groups[8].Value, 0), om = ParseOr(m.Groups[9].Value, 0);
                offset = new TimeSpan(oh, om, 0) * (sign == "-" ? -1 : 1);
            }
            value = new DateTimeOffset(dt, offset);
            return IsPlausible(value);
        }
        catch { return false; }
    }

    private static bool TryReadOpenXmlCreationTime(ItemReader reader, out DateTimeOffset value)
    {
        value = default;
        try
        {
            using var stream = new ItemLogicalStream(reader);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? entry = archive.GetEntry("docProps/core.xml");
            if (entry is null) return false;
            using Stream xmlStream = entry.Open();
            XDocument doc = XDocument.Load(xmlStream, LoadOptions.None);
            XNamespace dcterms = "http://purl.org/dc/terms/";
            string? created = doc.Descendants(dcterms + "created").FirstOrDefault()?.Value;
            if (DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset candidate) &&
                IsPlausible(candidate))
            {
                value = candidate;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryReadDicomAcquisitionTime(ItemReader reader, out DateTimeOffset value)
    {
        value = default;
        byte[] bytes = reader.ReadPrefix((int)Math.Min(reader.Length, 2L * 1024 * 1024));
        string? date = null, time = null;
        // Explicit VR Little Endian: (0008,0022) Acquisition Date, (0008,0032) Acquisition Time,
        // fallback (0008,0012)/(0008,0013) Instance Creation.
        foreach ((ushort element, bool isDate) in new[] { ((ushort)0x0022, true), ((ushort)0x0032, false), ((ushort)0x0012, true), ((ushort)0x0013, false) })
        {
            for (int i = 0; i + 12 <= bytes.Length; i++)
            {
                if (bytes[i] != 0x08 || bytes[i + 1] != 0x00 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 2, 2)) != element)
                    continue;
                string vr = Encoding.ASCII.GetString(bytes, i + 4, 2);
                int lenPos = i + 6;
                int valuePos;
                int len;
                if (vr is "OB" or "OW" or "OF" or "SQ" or "UT" or "UN")
                {
                    if (i + 12 > bytes.Length) continue;
                    len = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 8, 4)), 64u);
                    valuePos = i + 12;
                }
                else
                {
                    len = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lenPos, 2)), (ushort)64);
                    valuePos = i + 8;
                }
                if (len <= 0 || valuePos + len > bytes.Length) continue;
                string text = Encoding.ASCII.GetString(bytes, valuePos, len).Trim('\0', ' ');
                if (isDate && date is null) date = text;
                if (!isDate && time is null) time = text;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(date)) return false;
        string compactTime = string.IsNullOrWhiteSpace(time) ? "000000" : new string(time.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        string hhmmss = compactTime.Split('.')[0].PadRight(6, '0');
        if (!DateTime.TryParseExact(date + hhmmss, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            return false;
        dt = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        value = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        return IsPlausible(value);
    }

    private static bool TryReadTextMetadataDate(ItemReader reader, out DateTimeOffset value)
    {
        value = default;
        byte[] bytes = reader.ReadPrefix((int)Math.Min(reader.Length, 1024L * 1024));
        string text = Encoding.UTF8.GetString(bytes);
        Match match = IsoDateRegex().Match(text);
        return match.Success && DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out value) && IsPlausible(value);
    }

    private static bool IsPlausible(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return utc.Year >= 1970 && utc <= DateTimeOffset.UtcNow.AddYears(2);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadU32(ReadOnlySpan<byte> bytes, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty) return 0;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    [GeneratedRegex(@"/CreationDate\s*\(\s*([^\)]{4,80})\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PdfCreationRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+\-]\d{2}:?\d{2})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateRegex();

    private sealed class ItemReader
    {
        private readonly RawDeviceReader? _raw;
        private readonly RecoveryFileItem _item;
        private readonly byte[] _prefix;
        private readonly long _contentLength;

        public ItemReader(RawDeviceReader? raw, RecoveryFileItem item)
        {
            _raw = raw;
            _item = item;
            _prefix = item.PrefixData is { Length: > 0 } prefix ? prefix : Array.Empty<byte>();
            _contentLength = Math.Max(0, item.SizeBytes);
        }

        public long Length => _prefix.LongLength + _contentLength;

        public byte[] ReadPrefix(int count) => ReadBytes(0, count);

        public byte[] ReadBytes(long offset, int count)
        {
            if (count <= 0 || offset < 0 || offset >= Length) return Array.Empty<byte>();
            int wanted = (int)Math.Min(count, Length - offset);
            byte[] buffer = new byte[wanted];
            int read = ReadAt(offset, buffer);
            if (read == wanted) return buffer;
            Array.Resize(ref buffer, Math.Max(0, read));
            return buffer;
        }

        public int ReadAt(long logicalOffset, Span<byte> destination)
        {
            if (logicalOffset < 0 || logicalOffset >= Length || destination.IsEmpty) return 0;
            int total = 0;
            if (logicalOffset < _prefix.LongLength)
            {
                int start = checked((int)logicalOffset);
                int n = Math.Min(destination.Length, _prefix.Length - start);
                _prefix.AsSpan(start, n).CopyTo(destination);
                total += n;
                logicalOffset += n;
                if (total == destination.Length) return total;
            }

            long contentOffset = logicalOffset - _prefix.LongLength;
            if (contentOffset < 0 || contentOffset >= _contentLength) return total;
            Span<byte> target = destination[total..];
            int requested = (int)Math.Min(target.Length, _contentLength - contentOffset);
            if (requested <= 0) return total;
            return total + ReadContent(contentOffset, target[..requested]);
        }

        private int ReadContent(long offset, Span<byte> destination)
        {
            if (_item.SourceKind == RecoverySourceKind.NtfsResident)
            {
                if (_item.ResidentData is not { Length: > 0 } data || offset >= data.LongLength) return 0;
                int n = (int)Math.Min(destination.Length, data.LongLength - offset);
                data.AsSpan(checked((int)offset), n).CopyTo(destination);
                return n;
            }
            if (_raw is null) return 0;

            return _item.SourceKind switch
            {
                RecoverySourceKind.RawContiguous or RecoverySourceKind.FatContiguous or RecoverySourceKind.ExFatContiguous
                    => ReadPhysical(_item.SourceOffset + offset, destination),
                RecoverySourceKind.Extents => ReadExtents(offset, destination),
                RecoverySourceKind.NtfsRunList => ReadRuns(offset, destination),
                _ => 0
            };
        }

        private int ReadPhysical(long physicalOffset, Span<byte> destination)
        {
            destination.Clear();
            return _raw!.ReadBestEffort(physicalOffset, destination, out _);
        }

        private int ReadExtents(long contentOffset, Span<byte> destination)
        {
            if (_item.SourceExtents is not { Count: > 0 } extents) return 0;
            int total = 0;
            long logicalStart = 0;
            foreach (SourceExtent extent in extents)
            {
                long length = Math.Max(0, extent.Length);
                long logicalEnd;
                try { logicalEnd = checked(logicalStart + length); } catch { break; }
                if (contentOffset >= logicalEnd) { logicalStart = logicalEnd; continue; }
                long within = Math.Max(0, contentOffset - logicalStart);
                int n = (int)Math.Min(destination.Length - total, length - within);
                if (n <= 0) { logicalStart = logicalEnd; continue; }
                int read = ReadPhysical(extent.Offset + within, destination.Slice(total, n));
                total += read;
                contentOffset += read;
                if (read < n || total == destination.Length) break;
                logicalStart = logicalEnd;
            }
            return total;
        }

        private int ReadRuns(long contentOffset, Span<byte> destination)
        {
            if (_item.DataRuns is not { Count: > 0 } runs || _item.ClusterSize <= 0) return 0;
            int total = 0;
            long logicalStart = 0;
            foreach (DataRun run in runs)
            {
                long length;
                try { length = checked(run.ClusterCount * (long)_item.ClusterSize); } catch { break; }
                if (length <= 0) continue;
                long logicalEnd;
                try { logicalEnd = checked(logicalStart + length); } catch { break; }
                if (contentOffset >= logicalEnd) { logicalStart = logicalEnd; continue; }
                long within = Math.Max(0, contentOffset - logicalStart);
                int n = (int)Math.Min(destination.Length - total, length - within);
                if (n <= 0) { logicalStart = logicalEnd; continue; }
                Span<byte> target = destination.Slice(total, n);
                if (run.IsSparse) target.Clear();
                else ReadPhysical(checked(run.LogicalClusterNumber * (long)_item.ClusterSize + within), target);
                total += n;
                contentOffset += n;
                if (total == destination.Length) break;
                logicalStart = logicalEnd;
            }
            return total;
        }
    }

    private sealed class ItemLogicalStream : Stream
    {
        private readonly ItemReader _reader;
        private long _position;
        public ItemLogicalStream(ItemReader reader) => _reader = reader;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _reader.Length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, Length); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _reader.ReadAt(_position, buffer.AsSpan(offset, count));
            _position += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            int read = _reader.ReadAt(_position, buffer);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => _position
            };
            _position = Math.Clamp(target, 0, Length);
            return _position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
