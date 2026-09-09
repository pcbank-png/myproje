using System.Text;

namespace NSXVeriKurtarmaPro.Services;

public enum FileHeaderValidationOutcome
{
    NotSupported,
    Mismatch,
    Match
}

public static class FileHeaderValidator
{
    private static readonly byte[] AsfHeaderGuid =
    [
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    ];

    private static readonly byte[] OleHeader =
    [
        0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
    ];

    public static bool LooksLike(string extension, ReadOnlySpan<byte> data, long totalLength = -1) =>
        Validate(extension, data, totalLength) == FileHeaderValidationOutcome.Match;

    public static FileHeaderValidationOutcome Validate(
        string extension,
        ReadOnlySpan<byte> data,
        long totalLength = -1)
    {
        string ext = (extension ?? string.Empty).Trim().TrimStart('.').ToUpperInvariant();

        bool? matches = ext switch
        {
            "JPG" or "JPEG" or "JPE" or "JFIF" => StartsWith(data, [0xFF, 0xD8, 0xFF]),
            "PNG" or "APNG" => StartsWith(data, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "GIF" => data.Length >= 6 && Encoding.ASCII.GetString(data[..6]) is "GIF87a" or "GIF89a",
            "BMP" => totalLength > 0
                ? BmpStructureValidator.TryValidate(data, totalLength, out _)
                : BmpStructureValidator.LooksLikePrefix(data),
            "TIF" or "TIFF" => LooksLikeTiff(data),
            "CR2" => LooksLikeTiff(data) && data.Length >= 12 && data[8] == (byte)'C' && data[9] == (byte)'R',
            "DNG" or "NEF" or "NRW" or "ARW" or "PEF" or "SRW" or "SR2" or "SRF" or
                "3FR" or "ERF" or "IIQ" or "KDC" or "MEF" or "MOS" or "RWL" => LooksLikeTiff(data),
            "ORF" => LooksLikeOlympusRaw(data),
            "RW2" => LooksLikePanasonicRaw(data),
            "RAF" => data.Length >= 16 && data[..16].SequenceEqual("FUJIFILMCCD-RAW "u8),
            "X3F" => data.Length >= 4 && data[..4].SequenceEqual("FOVb"u8),
            "CR3" => data.Length >= 12 && IsIsoBmffHeader(data) &&
                     data[8] == (byte)'c' && data[9] == (byte)'r' && data[10] == (byte)'x',
            "ICO" => LooksLikeIco(data, cursor: false),
            "CUR" => LooksLikeIco(data, cursor: true),
            "JP2" or "JPF" or "JPX" or "JPM" => StartsWith(data, [0x00, 0x00, 0x00, 0x0C, (byte)'j', (byte)'P', 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A]),
            "J2K" => StartsWith(data, [0xFF, 0x4F, 0xFF, 0x51]),
            "PSD" or "PSB" => LooksLikePsd(data),
            "DDS" => data.Length >= 128 && StartsWith(data, "DDS "u8) &&
                     System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) == 124,
            "EXR" => StartsWith(data, [0x76, 0x2F, 0x31, 0x01]),
            "WEBP" => data.Length >= 12 &&
                      Encoding.ASCII.GetString(data[..4]) == "RIFF" &&
                      Encoding.ASCII.GetString(data.Slice(8, 4)) == "WEBP",
            "AVI" or "DIVX" or "XVID" => data.Length >= 12 &&
                     Encoding.ASCII.GetString(data[..4]) == "RIFF" &&
                     Encoding.ASCII.GetString(data.Slice(8, 4)) == "AVI ",
            "WAV" => data.Length >= 12 &&
                     Encoding.ASCII.GetString(data[..4]) == "RIFF" &&
                     Encoding.ASCII.GetString(data.Slice(8, 4)) == "WAVE",
            "WMV" or "ASF" or "DVR-MS" or "WMA" => StartsWith(data, AsfHeaderGuid),
            "HEIC" or "HEIF" or "AVIF" => HeifRecoveryService.LooksLikeHeader(data, ext),
            "MP4" or "M4V" or "MOV" or "QT" or "3GP" or "3G2" or "F4V" or "M4A" or "M4B" =>
                data.Length >= 12 && IsIsoBmffHeader(data),
            "TS" or "M2T" or "TP" or "TRP" or "MOD" or "TOD" => HasTransportSync(data, 188, 0),
            "MTS" => HasTransportSync(data, 188, 0) || HasTransportSync(data, 192, 4),
            "M2TS" => HasTransportSync(data, 192, 4),
            "MPG" or "MPEG" or "MPE" or "MPV" or "M1V" or "M2V" or "VOB" or "EVO" =>
                StartsWith(data, [0x00, 0x00, 0x01, 0xBA]) || StartsWith(data, [0x00, 0x00, 0x01, 0xB3]),
            "MKV" or "WEBM" => StartsWith(data, [0x1A, 0x45, 0xDF, 0xA3]),
            "FLV" => data.Length >= 9 && StartsWith(data, [(byte)'F', (byte)'L', (byte)'V', 0x01]) && (data[4] & 0x01) != 0,
            "OGV" => LooksLikeOggVideo(data),
            "OGG" or "OPUS" => data.Length >= 27 && StartsWith(data, [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 0x00]),
            "MP3" => RawFileAnalyzer.LooksLikeMp3Candidate(data),
            "AAC" => RawFileAnalyzer.LooksLikeAacAdtsCandidate(data),
            "AIFF" or "AIF" => data.Length >= 12 && StartsWith(data, "FORM"u8) &&
                (data.Slice(8, 4).SequenceEqual("AIFF"u8) || data.Slice(8, 4).SequenceEqual("AIFC"u8)),
            "AU" => data.Length >= 24 && StartsWith(data, ".snd"u8),
            "MID" or "MIDI" => data.Length >= 14 && StartsWith(data, "MThd"u8),
            "FLAC" => data.Length >= 4 && StartsWith(data, "fLaC"u8),
            "RM" or "RMVB" => StartsWith(data, [(byte)'.', (byte)'R', (byte)'M', (byte)'F']),
            "MXF" => LooksLikeMxf(data),
            "DV" => GlobalVideoRawAnalyzer.LooksLikeDvStart(data),
            "WTV" => StartsWith(data, [0xB7, 0xD8, 0x00, 0x20, 0x37, 0x49, 0xDA, 0x11, 0xA6, 0x4E, 0x00, 0x07, 0xE9, 0x5E, 0xAD, 0x8D]),
            "NSV" => StartsWith(data, "NSVf"u8),
            "ROQ" => data.Length >= 8 && StartsWith(data, [0x84, 0x10, 0xFF, 0xFF, 0xFF, 0xFF]),
            "BIK" or "BINK" or "BK2" or "BIK2" => data.Length >= 4 &&
                ((data[0] == (byte)'B' && data[1] == (byte)'I' && data[2] == (byte)'K') ||
                 (data[0] == (byte)'K' && data[1] == (byte)'B' && data[2] == (byte)'2')),
            "SMK" => StartsWith(data, "SMK2"u8) || StartsWith(data, "SMK4"u8),
            "IVF" or "AV1" => data.Length >= 32 && StartsWith(data, "DKIF"u8) && data.Slice(8, 4).SequenceEqual("AV01"u8),
            "H264" or "AVC" => LooksLikeAnnexB(data, h265: false) || LooksLikeLengthPrefixedNal(data, h265: false, totalLength),
            "H265" or "HEVC" => LooksLikeAnnexB(data, h265: true) || LooksLikeLengthPrefixedNal(data, h265: true, totalLength),
            "PDF" => StartsWith(data, [(byte)'%', (byte)'P', (byte)'D', (byte)'F']),
            "ZIP" or "DOCX" or "XLSX" or "PPTX" or "EPUB" or "ODT" or "ODS" or "ODP" or "APK" or "JAR" =>
                StartsWith(data, [(byte)'P', (byte)'K', 0x03, 0x04]),
            "7Z" => StartsWith(data, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]),
            "CAB" => StartsWith(data, "MSCF"u8),
            "EXE" or "DLL" => LooksLikePortableExecutable(data),
            "PST" or "OST" => StartsWith(data, [(byte)'!', (byte)'B', (byte)'D', (byte)'N']),
            "SQLITE" or "DB" or "DB3" => SqliteRecoveryService.LooksLikeDatabaseHeader(data, totalLength > 0 ? totalLength : long.MaxValue),
            "WAL" => SqliteRecoveryService.LooksLikeWalHeader(data),
            "JOURNAL" => SqliteRecoveryService.LooksLikeJournalHeader(data),
            "DOC" or "XLS" or "PPT" or "MSG" or "MSI" => StartsWith(data, OleHeader),
            "RAR" => LooksLikeRar(data),
            _ => null
        };

        return matches switch
        {
            true => FileHeaderValidationOutcome.Match,
            false => FileHeaderValidationOutcome.Mismatch,
            null => FileHeaderValidationOutcome.NotSupported
        };
    }


    private static bool LooksLikePortableExecutable(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64 || data[0] != (byte)'M' || data[1] != (byte)'Z')
            return false;

        int peOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0x3C, 4));
        return peOffset >= 64 && peOffset + 24 <= data.Length &&
               data.Slice(peOffset, 4).SequenceEqual("PE\0\0"u8);
    }


    private static bool LooksLikeMxf(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> prefix =
        [
            0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
            0x0D, 0x01, 0x02, 0x01, 0x01, 0x02
        ];
        return data.Length >= 16 && data[..14].SequenceEqual(prefix) &&
               data[14] is >= 0x01 and <= 0x04 && data[15] == 0x00;
    }

    private static bool LooksLikeOggVideo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32 || !StartsWith(data, [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 0x00]))
            return false;

        int inspect = Math.Min(data.Length, 64 * 1024);
        return data[..inspect].IndexOf("theora"u8) >= 0;
    }

    private static bool LooksLikeIco(ReadOnlySpan<byte> data, bool cursor) =>
        IconContainerValidator.LooksLikePrefix(data, cursor);

    private static bool LooksLikePsd(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26 || !StartsWith(data, "8BPS"u8))
            return false;
        ushort version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        if (version is not (1 or 2))
            return false;
        for (int i = 6; i < 12; i++)
        {
            if (data[i] != 0) return false;
        }
        return true;
    }

    private static bool LooksLikeTiff(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;

        bool little = data[0] == (byte)'I' && data[1] == (byte)'I';
        bool big = data[0] == (byte)'M' && data[1] == (byte)'M';
        if (!little && !big) return false;

        ushort magic = little
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));

        return magic is 42 or 43;
    }

    private static bool LooksLikeOlympusRaw(ReadOnlySpan<byte> data)
    {
        return data.Length >= 8 && data[0] == (byte)'I' && data[1] == (byte)'I' &&
               ((data[2] == (byte)'R' && data[3] == (byte)'O') ||
                (data[2] == (byte)'R' && data[3] == (byte)'S'));
    }

    private static bool LooksLikePanasonicRaw(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[0] == (byte)'I' && data[1] == (byte)'I' &&
        data[2] == 0x55 && data[3] == 0x00;

    private static bool LooksLikeRar(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) return false;
        if (!StartsWith(data, [(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07]))
            return false;

        return data[6] == 0x00 ||
               (data[6] == 0x01 && data.Length >= 8 && data[7] == 0x00);
    }

    private static bool IsIsoBmffHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        ReadOnlySpan<byte> type = data.Slice(4, 4);
        return type.SequenceEqual("ftyp"u8) ||
               type.SequenceEqual("styp"u8) ||
               type.SequenceEqual("moof"u8) ||
               type.SequenceEqual("mdat"u8) ||
               type.SequenceEqual("moov"u8);
    }

    private static bool LooksLikeLengthPrefixedNal(ReadOnlySpan<byte> data, bool h265, long totalLength)
    {
        if (data.Length < 6)
            return false;

        uint firstLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        if (firstLength < 2)
            return false;

        if (totalLength > 0 && 4UL + firstLength > (ulong)totalLength)
            return false;

        return LooksLikeNalHeader(data.Slice(4), h265);
    }

    private static bool LooksLikeAnnexB(ReadOnlySpan<byte> data, bool h265)
    {
        if (data.Length < 6) return false;
        int prefix = StartsWith(data, [0x00, 0x00, 0x00, 0x01])
            ? 4
            : StartsWith(data, [0x00, 0x00, 0x01]) ? 3 : 0;

        if (prefix == 0 || data.Length <= prefix) return false;
        return LooksLikeNalHeader(data[prefix..], h265);
    }

    private static bool LooksLikeNalHeader(ReadOnlySpan<byte> data, bool h265)
    {
        if (data.Length == 0) return false;

        byte header = data[0];
        if ((header & 0x80) != 0) return false;

        if (!h265)
        {
            int nalType = header & 0x1F;
            return nalType is >= 1 and <= 12;
        }

        if (data.Length < 2 || (data[1] & 0x07) == 0)
            return false;

        int hevcType = (header >> 1) & 0x3F;
        return hevcType is >= 0 and <= 40;
    }

    private static bool HasTransportSync(ReadOnlySpan<byte> data, int packetSize, int syncOffset)
    {
        int required = syncOffset + packetSize * 3 + 1;
        if (data.Length < required) return false;

        for (int i = 0; i < 4; i++)
        {
            if (data[syncOffset + i * packetSize] != 0x47)
                return false;
        }

        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);
}
