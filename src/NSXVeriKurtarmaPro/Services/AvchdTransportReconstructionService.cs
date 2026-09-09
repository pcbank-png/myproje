using System.Buffers.Binary;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// AVCHD/MPEG-TS reconstruction engine. The service performs a full PSI/PID pass first,
/// then writes a clean 188 or 192-byte stream with regenerated PAT/PMT tables and
/// normalized continuity counters. Damaged byte ranges are treated as holes and the
/// reader resumes at the next independently verified packet run.
/// </summary>
internal static class AvchdTransportReconstructionService
{
    private const int TsPacketBytes = 188;
    private const int M2TsPacketBytes = 192;
    private const int IoBufferBytes = 4 * 1024 * 1024;
    private const int MinimumSyncPackets = 8;
    private const int PsiRepeatPackets = 4_000;

    private readonly record struct TransportGeometry(
        int PacketSize,
        int SyncOffset,
        long FirstPacketOffset,
        int ResyncPackets = MinimumSyncPackets);

    private sealed class ProgramMap
    {
        public ushort ProgramNumber { get; init; } = 1;
        public int PmtPid { get; set; } = 0x1000;
        public int PcrPid { get; set; } = -1;
        public Dictionary<int, byte> Streams { get; } = [];
        public bool CameFromPsi { get; set; }
        public bool CameFromReference { get; set; }
    }

    private sealed class PidEvidence
    {
        public long Packets;
        public long PayloadPackets;
        public long PesStarts;
        public long PcrCount;
        public long PtsCount;
        public long DtsCount;
        public long ContinuityErrors;
        public long FirstPcr = -1;
        public long LastPcr = -1;
        public long FirstPts = -1;
        public long LastPts = -1;
        public byte StreamType;
        public bool IsVideo;
        public bool IsAudio;
        public int LastContinuity = -1;
    }

    private sealed class Analysis
    {
        public Dictionary<int, PidEvidence> Pids { get; } = [];
        public Dictionary<ushort, int> PatPrograms { get; } = [];
        public Dictionary<int, ProgramMap> ProgramsByPmtPid { get; } = [];
        public long ValidPackets;
        public long RejectedPackets;
        public long HoleBytes;
    }

    public static MediaRepairResult Reconstruct(
        string sourcePath,
        string destinationPath,
        Action<string>? progress,
        ReferenceVideoProfile? referenceProfile = null)
    {
        try
        {
            progress?.Invoke("AVCHD motoru • 188/192 bayt paket geometrisi aranıyor...");
            using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                IoBufferBytes,
                FileOptions.SequentialScan);

            referenceProfile = NormalizeTransportReference(referenceProfile);
            TransportGeometry geometry = DetectGeometry(input)
                ?? DetectGeometryFromReference(input, referenceProfile)
                ?? throw new InvalidDataException("Doğrulanmış 188/192 bayt MPEG transport-stream paket dizisi bulunamadı.");

            progress?.Invoke(referenceProfile is null
                ? "AVCHD motoru • PAT/PMT, PID, PES, PCR ve PTS/DTS haritası çıkarılıyor..."
                : "AVCHD motoru • PAT/PMT/PID haritası çıkarılıyor; eksik metadata için aynı cihaz referansı hazır...");
            Analysis analysis = Analyze(input, geometry, progress);
            ProgramMap program = SelectOrInferProgram(analysis, referenceProfile);
            ValidateProgram(program, analysis);

            progress?.Invoke(
                $"AVCHD motoru • Program {program.ProgramNumber}, PMT PID 0x{program.PmtPid:X4}, " +
                $"PCR PID 0x{program.PcrPid:X4}, {program.Streams.Count:N0} akış yeniden yazılıyor...");

            WriteReconstructed(input, destinationPath, geometry, analysis, program, progress, out long writtenPackets);
            ValidateOutput(destinationPath, geometry, program, out long verifiedPackets);

            PidEvidence? clock = analysis.Pids.GetValueOrDefault(program.PcrPid);
            long ptsCount = program.Streams.Keys.Sum(pid => analysis.Pids.GetValueOrDefault(pid)?.PtsCount ?? 0);
            long dtsCount = program.Streams.Keys.Sum(pid => analysis.Pids.GetValueOrDefault(pid)?.DtsCount ?? 0);
            long continuityErrors = program.Streams.Keys
                .Append(program.PmtPid)
                .Append(0)
                .Distinct()
                .Sum(pid => analysis.Pids.GetValueOrDefault(pid)?.ContinuityErrors ?? 0);

            string geometryText = geometry.PacketSize == M2TsPacketBytes
                ? "192 bayt M2TS/AVCHD"
                : "188 bayt TS";
            string psiText = program.CameFromPsi
                ? "PAT/PMT doğrulandı ve yenilendi"
                : program.CameFromReference
                    ? "PAT/PMT kayıptı; aynı cihaz referansındaki PID/codec karakteristiğiyle yeniden üretildi"
                    : "PAT/PMT kayıptı; PID/PES kanıtından yeniden üretildi";
            string holeText = analysis.HoleBytes > 0
                ? $" {RecoveryFileItem.FormatBytes(analysis.HoleBytes)} bozuk/uyumsuz alan hole olarak atlandı."
                : string.Empty;

            return new MediaRepairResult(
                true,
                $"{geometryText} gerçek reconstruction tamamlandı; {psiText}. " +
                $"{writtenPackets:N0} paket yazıldı, {verifiedPackets:N0} paket doğrulandı; " +
                $"PCR {clock?.PcrCount ?? 0:N0}, PTS {ptsCount:N0}, DTS {dtsCount:N0}, " +
                $"düzeltilen continuity kopuğu {continuityErrors:N0}.{holeText}",
                new FileInfo(destinationPath).Length);
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(false, $"AVCHD/TS yeniden inşası başarısız: {ex.Message}");
        }
    }

    private static Analysis Analyze(FileStream input, TransportGeometry geometry, Action<string>? progress)
    {
        var analysis = new Analysis();
        var packet = new byte[geometry.PacketSize];
        long position = geometry.FirstPacketOffset;
        long nextProgress = 256L * 1024 * 1024;

        while (position + geometry.PacketSize <= input.Length)
        {
            input.Position = position;
            if (!ReadExactly(input, packet))
                break;

            if (!TryReadPacket(packet, geometry.SyncOffset, out PacketInfo info))
            {
                long next = FindNextPacketRun(input, position + 1, geometry, input.Length);
                if (next < 0)
                    break;

                analysis.RejectedPackets++;
                analysis.HoleBytes += next - position;
                position = next;
                continue;
            }

            analysis.ValidPackets++;
            PidEvidence evidence = GetEvidence(analysis, info.Pid);
            evidence.Packets++;
            if (info.HasPayload)
                evidence.PayloadPackets++;

            if (info.HasPayload)
            {
                if (evidence.LastContinuity >= 0 && info.Continuity != ((evidence.LastContinuity + 1) & 0x0F))
                    evidence.ContinuityErrors++;
                evidence.LastContinuity = info.Continuity;
            }

            ReadOnlySpan<byte> ts = packet.AsSpan(geometry.SyncOffset, TsPacketBytes);
            if (TryReadPcr(ts, info, out long pcr))
            {
                evidence.PcrCount++;
                if (evidence.FirstPcr < 0)
                    evidence.FirstPcr = pcr;
                evidence.LastPcr = pcr;
            }

            if (info.PayloadUnitStart && info.HasPayload && info.PayloadOffset < TsPacketBytes)
            {
                ReadOnlySpan<byte> payload = ts[info.PayloadOffset..];
                if (info.Pid == 0)
                    ParsePat(payload, analysis.PatPrograms);
                else if (analysis.PatPrograms.Values.Contains(info.Pid))
                    ParsePmt(payload, info.Pid, analysis.ProgramsByPmtPid);

                AnalyzePes(payload, evidence);
            }

            position += geometry.PacketSize;
            if (position >= nextProgress)
            {
                progress?.Invoke(
                    $"AVCHD analiz • {RecoveryFileItem.FormatBytes(position)} • " +
                    $"{analysis.ValidPackets:N0} paket, {analysis.Pids.Count:N0} PID");
                nextProgress += 256L * 1024 * 1024;
            }
        }

        return analysis;
    }

    private static ProgramMap SelectOrInferProgram(Analysis analysis, ReferenceVideoProfile? referenceProfile)
    {
        foreach ((ushort programNumber, int pmtPid) in analysis.PatPrograms.OrderBy(p => p.Key))
        {
            if (!analysis.ProgramsByPmtPid.TryGetValue(pmtPid, out ProgramMap? parsed) || parsed.Streams.Count == 0)
                continue;

            var result = new ProgramMap
            {
                ProgramNumber = programNumber,
                PmtPid = pmtPid,
                PcrPid = parsed.PcrPid,
                CameFromPsi = true
            };
            foreach ((int pid, byte streamType) in parsed.Streams)
                result.Streams[pid] = streamType;
            return result;
        }

        var inferred = new ProgramMap();
        foreach ((int pid, PidEvidence evidence) in analysis.Pids
                     .Where(p => p.Key is > 0 and < 0x1FFF && p.Value.PesStarts > 0)
                     .OrderByDescending(p => p.Value.IsVideo)
                     .ThenByDescending(p => p.Value.IsAudio)
                     .ThenByDescending(p => p.Value.PesStarts))
        {
            if (!evidence.IsVideo && !evidence.IsAudio)
                continue;

            inferred.Streams[pid] = evidence.StreamType != 0
                ? evidence.StreamType
                : evidence.IsVideo ? (byte)0x1B : (byte)0x0F;
        }

        if (referenceProfile is not null)
            EnrichProgramFromReference(inferred, analysis, referenceProfile);

        int pcrPid = inferred.PcrPid >= 0 && inferred.Streams.ContainsKey(inferred.PcrPid)
            ? inferred.PcrPid
            : analysis.Pids
                .Where(p => p.Value.PcrCount > 0 && inferred.Streams.ContainsKey(p.Key))
                .OrderByDescending(p => p.Value.PcrCount)
                .Select(p => p.Key)
                .FirstOrDefault(-1);
        if (pcrPid < 0)
            pcrPid = inferred.Streams.Keys.FirstOrDefault(pid => analysis.Pids[pid].IsVideo, -1);
        inferred.PcrPid = pcrPid;
        return inferred;
    }

    private static ReferenceVideoProfile? NormalizeTransportReference(ReferenceVideoProfile? reference)
    {
        if (reference is null || reference.ContainerKind != "MPEG-TS")
            return null;
        if (reference.TransportPacketSize is not (TsPacketBytes or M2TsPacketBytes))
            return null;
        if (reference.TransportSyncOffset != (reference.TransportPacketSize == M2TsPacketBytes ? 4 : 0))
            return null;
        if (reference.VideoCodec is not (ReferenceVideoCodecKind.H264 or ReferenceVideoCodecKind.H265))
            return null;
        return reference;
    }

    private static void EnrichProgramFromReference(ProgramMap program, Analysis analysis, ReferenceVideoProfile reference)
    {
        bool used = false;
        if (reference.PmtPid is int pmtPid && pmtPid is > 0 and < 0x1FFF && !program.Streams.ContainsKey(pmtPid))
        {
            program.PmtPid = pmtPid;
            used = true;
        }

        if (reference.VideoPid is int videoPid && videoPid is > 0 and < 0x1FFF &&
            analysis.Pids.TryGetValue(videoPid, out PidEvidence? videoEvidence) && videoEvidence.PayloadPackets >= 4 &&
            !program.Streams.ContainsKey(videoPid))
        {
            program.Streams[videoPid] = reference.VideoCodec == ReferenceVideoCodecKind.H265 ? (byte)0x24 : (byte)0x1B;
            used = true;
        }

        if (reference.AudioPid is int audioPid && audioPid is > 0 and < 0x1FFF &&
            analysis.Pids.TryGetValue(audioPid, out PidEvidence? audioEvidence) && audioEvidence.PayloadPackets >= 4 &&
            !program.Streams.ContainsKey(audioPid))
        {
            program.Streams[audioPid] = MapReferenceAudioType(reference.AudioCodec);
            used = true;
        }

        if (reference.PcrPid is int pcrPid && program.Streams.ContainsKey(pcrPid))
        {
            program.PcrPid = pcrPid;
            used = true;
        }
        else if (reference.VideoPid is int referenceVideoPid && program.Streams.ContainsKey(referenceVideoPid))
        {
            program.PcrPid = referenceVideoPid;
            used = true;
        }

        program.CameFromReference |= used;
    }

    private static byte MapReferenceAudioType(string codec)
    {
        if (codec.Contains("E-AC-3", StringComparison.OrdinalIgnoreCase)) return 0x87;
        if (codec.Contains("AC-3", StringComparison.OrdinalIgnoreCase)) return 0x81;
        if (codec.Contains("LPCM", StringComparison.OrdinalIgnoreCase) || codec.Contains("PCM", StringComparison.OrdinalIgnoreCase)) return 0x83;
        if (codec.Contains("LATM", StringComparison.OrdinalIgnoreCase)) return 0x11;
        return 0x0F;
    }

    private static void ValidateProgram(ProgramMap program, Analysis analysis)
    {
        if (analysis.ValidPackets < 20)
            throw new InvalidDataException("Yeterli sayıda sağlam TS paketi bulunamadı.");
        if (program.Streams.Count == 0)
            throw new InvalidDataException("PAT/PMT veya PES kanıtından ses/video PID haritası çıkarılamadı.");
        if (!program.Streams.Any(s => IsVideoStreamType(s.Value) || analysis.Pids.GetValueOrDefault(s.Key)?.IsVideo == true))
            throw new InvalidDataException("Program haritasında doğrulanmış video akışı bulunamadı.");
        if (program.PcrPid < 0 || !program.Streams.ContainsKey(program.PcrPid))
            program.PcrPid = program.Streams.First(s => IsVideoStreamType(s.Value) || analysis.Pids[s.Key].IsVideo).Key;
    }

    private static void WriteReconstructed(
        FileStream input,
        string destinationPath,
        TransportGeometry geometry,
        Analysis analysis,
        ProgramMap program,
        Action<string>? progress,
        out long writtenPackets)
    {
        string? parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            IoBufferBytes,
            FileOptions.SequentialScan);

        var continuity = new Dictionary<int, int>();
        var packet = new byte[geometry.PacketSize];
        var allowedPids = new HashSet<int>(program.Streams.Keys) { 0, program.PmtPid };
        long position = geometry.FirstPacketOffset;
        long outputPackets = 0;
        long sourceClock = 0;
        long nextProgress = 256L * 1024 * 1024;

        WritePsiPair(output, geometry, program, continuity, ref sourceClock);
        outputPackets += 2;

        while (position + geometry.PacketSize <= input.Length)
        {
            input.Position = position;
            if (!ReadExactly(input, packet))
                break;

            if (!TryReadPacket(packet, geometry.SyncOffset, out PacketInfo info))
            {
                long next = FindNextPacketRun(input, position + 1, geometry, input.Length);
                if (next < 0)
                    break;
                position = next;
                continue;
            }

            // Original PSI may be partial/corrupt. A fresh, internally consistent pair is emitted instead.
            if (info.Pid == 0 || analysis.PatPrograms.Values.Contains(info.Pid))
            {
                position += geometry.PacketSize;
                continue;
            }

            if (!allowedPids.Contains(info.Pid))
            {
                position += geometry.PacketSize;
                continue;
            }

            NormalizeContinuity(packet.AsSpan(geometry.SyncOffset, TsPacketBytes), info, continuity);
            output.Write(packet);
            outputPackets++;
            sourceClock = ReadM2TsClock(packet, geometry, sourceClock);

            if (outputPackets % PsiRepeatPackets == 0)
            {
                WritePsiPair(output, geometry, program, continuity, ref sourceClock);
                outputPackets += 2;
            }

            position += geometry.PacketSize;
            if (output.Length >= nextProgress)
            {
                progress?.Invoke(
                    $"AVCHD reconstruction • {RecoveryFileItem.FormatBytes(output.Length)} • " +
                    $"{outputPackets:N0} paket");
                nextProgress += 256L * 1024 * 1024;
            }
        }

        output.Flush(true);
        writtenPackets = outputPackets;
    }

    private static void WritePsiPair(
        FileStream output,
        TransportGeometry geometry,
        ProgramMap program,
        Dictionary<int, int> continuity,
        ref long sourceClock)
    {
        byte[] pat = BuildPatSection(program.ProgramNumber, program.PmtPid);
        byte[] pmt = BuildPmtSection(program);
        WritePsiPacket(output, geometry, 0, pat, continuity, ref sourceClock);
        WritePsiPacket(output, geometry, program.PmtPid, pmt, continuity, ref sourceClock);
    }

    private static void WritePsiPacket(
        Stream output,
        TransportGeometry geometry,
        int pid,
        byte[] section,
        Dictionary<int, int> continuity,
        ref long sourceClock)
    {
        if (section.Length + 1 > 184)
            throw new InvalidDataException("Üretilen PSI tablosu tek TS paketine sığmıyor.");

        byte[] packet = new byte[geometry.PacketSize];
        packet.AsSpan().Fill(0xFF);
        if (geometry.SyncOffset == 4)
        {
            uint prefix = (uint)(sourceClock & 0x3FFFFFFF);
            BinaryPrimitives.WriteUInt32BigEndian(packet, prefix);
            sourceClock = (sourceClock + 3003) & 0x3FFFFFFF;
        }

        int ts = geometry.SyncOffset;
        int cc = continuity.GetValueOrDefault(pid, 0) & 0x0F;
        packet[ts] = 0x47;
        packet[ts + 1] = (byte)(0x40 | ((pid >> 8) & 0x1F));
        packet[ts + 2] = (byte)pid;
        packet[ts + 3] = (byte)(0x10 | cc);
        packet[ts + 4] = 0;
        section.CopyTo(packet, ts + 5);
        continuity[pid] = (cc + 1) & 0x0F;
        output.Write(packet);
    }

    private static byte[] BuildPatSection(ushort programNumber, int pmtPid)
    {
        byte[] section = new byte[16];
        section[0] = 0x00;
        section[1] = 0xB0;
        section[2] = 13;
        section[3] = 0x00;
        section[4] = 0x01;
        section[5] = 0xC1;
        section[6] = 0;
        section[7] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(8, 2), programNumber);
        section[10] = (byte)(0xE0 | ((pmtPid >> 8) & 0x1F));
        section[11] = (byte)pmtPid;
        WritePsiCrc(section);
        return section;
    }

    private static byte[] BuildPmtSection(ProgramMap program)
    {
        int sectionLength = 13 + program.Streams.Count * 5;
        byte[] section = new byte[3 + sectionLength];
        section[0] = 0x02;
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)sectionLength;
        BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(3, 2), program.ProgramNumber);
        section[5] = 0xC1;
        section[6] = 0;
        section[7] = 0;
        section[8] = (byte)(0xE0 | ((program.PcrPid >> 8) & 0x1F));
        section[9] = (byte)program.PcrPid;
        section[10] = 0xF0;
        section[11] = 0;

        int cursor = 12;
        foreach ((int pid, byte streamType) in program.Streams.OrderBy(s => s.Key))
        {
            section[cursor++] = streamType;
            section[cursor++] = (byte)(0xE0 | ((pid >> 8) & 0x1F));
            section[cursor++] = (byte)pid;
            section[cursor++] = 0xF0;
            section[cursor++] = 0;
        }

        WritePsiCrc(section);
        return section;
    }

    private static void WritePsiCrc(Span<byte> section)
    {
        uint crc = MpegCrc32(section[..^4]);
        BinaryPrimitives.WriteUInt32BigEndian(section[^4..], crc);
    }

    private static uint MpegCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    private static void ParsePat(ReadOnlySpan<byte> payload, Dictionary<ushort, int> programs)
    {
        if (!TryGetPsiSection(payload, 0x00, out ReadOnlySpan<byte> section) || section.Length < 12)
            return;

        int end = section.Length - 4;
        for (int cursor = 8; cursor + 4 <= end; cursor += 4)
        {
            ushort program = BinaryPrimitives.ReadUInt16BigEndian(section.Slice(cursor, 2));
            int pid = ((section[cursor + 2] & 0x1F) << 8) | section[cursor + 3];
            if (program != 0 && pid is > 0 and < 0x1FFF)
                programs[program] = pid;
        }
    }

    private static void ParsePmt(ReadOnlySpan<byte> payload, int pmtPid, Dictionary<int, ProgramMap> programs)
    {
        if (!TryGetPsiSection(payload, 0x02, out ReadOnlySpan<byte> section) || section.Length < 16)
            return;

        ushort programNumber = BinaryPrimitives.ReadUInt16BigEndian(section.Slice(3, 2));
        int pcrPid = ((section[8] & 0x1F) << 8) | section[9];
        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        int cursor = 12 + programInfoLength;
        int end = section.Length - 4;

        var map = new ProgramMap
        {
            ProgramNumber = programNumber,
            PmtPid = pmtPid,
            PcrPid = pcrPid,
            CameFromPsi = true
        };

        while (cursor + 5 <= end)
        {
            byte type = section[cursor];
            int pid = ((section[cursor + 1] & 0x1F) << 8) | section[cursor + 2];
            int infoLength = ((section[cursor + 3] & 0x0F) << 8) | section[cursor + 4];
            if (pid is > 0 and < 0x1FFF)
                map.Streams[pid] = type;
            cursor += 5 + infoLength;
        }

        if (map.Streams.Count > 0)
            programs[pmtPid] = map;
    }

    private static bool TryGetPsiSection(ReadOnlySpan<byte> payload, byte tableId, out ReadOnlySpan<byte> section)
    {
        section = default;
        if (payload.Length < 4)
            return false;

        int pointer = payload[0];
        int start = 1 + pointer;
        if (start + 3 > payload.Length || payload[start] != tableId)
            return false;

        int sectionLength = ((payload[start + 1] & 0x0F) << 8) | payload[start + 2];
        int total = 3 + sectionLength;
        if (sectionLength < 4 || start + total > payload.Length)
            return false;

        ReadOnlySpan<byte> candidate = payload.Slice(start, total);
        if (MpegCrc32(candidate) != 0)
            return false;

        section = candidate;
        return true;
    }

    private static void AnalyzePes(ReadOnlySpan<byte> payload, PidEvidence evidence)
    {
        if (payload.Length < 6 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return;

        byte streamId = payload[3];
        if (streamId is >= 0xE0 and <= 0xEF)
        {
            evidence.IsVideo = true;
            evidence.PesStarts++;
            evidence.StreamType = DetectVideoStreamType(payload);
        }
        else if (streamId is >= 0xC0 and <= 0xDF or 0xBD)
        {
            evidence.IsAudio = true;
            evidence.PesStarts++;
            evidence.StreamType = DetectAudioStreamType(payload);
        }
        else
        {
            return;
        }

        if (payload.Length < 14 || (payload[6] & 0xC0) != 0x80)
            return;

        int flags = (payload[7] >> 6) & 0x03;
        int headerLength = payload[8];
        if (headerLength + 9 > payload.Length)
            return;

        if (flags is 2 or 3 && TryDecodeTimestamp(payload.Slice(9, 5), out long pts))
        {
            evidence.PtsCount++;
            if (evidence.FirstPts < 0)
                evidence.FirstPts = pts;
            evidence.LastPts = pts;
        }

        if (flags == 3 && headerLength >= 10 && TryDecodeTimestamp(payload.Slice(14, 5), out _))
            evidence.DtsCount++;
    }

    private static byte DetectVideoStreamType(ReadOnlySpan<byte> pes)
    {
        int payloadStart = pes.Length >= 9 ? Math.Min(pes.Length, 9 + pes[8]) : 6;
        ReadOnlySpan<byte> data = pes[payloadStart..];
        for (int i = 0; i + 5 < data.Length; i++)
        {
            int header;
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                header = i + 3;
            else if (i + 6 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                header = i + 4;
            else
                continue;

            int h264 = data[header] & 0x1F;
            int h265 = (data[header] >> 1) & 0x3F;
            if (h265 is 19 or 20 or 21 or 32 or 33 or 34)
                return 0x24;
            if (h264 is 5 or 7 or 8)
                return 0x1B;
            if (data[header] == 0xB3 || data[header] == 0x00)
                return 0x02;
        }
        return 0x1B;
    }

    private static byte DetectAudioStreamType(ReadOnlySpan<byte> pes)
    {
        int payloadStart = pes.Length >= 9 ? Math.Min(pes.Length, 9 + pes[8]) : 6;
        ReadOnlySpan<byte> data = pes[payloadStart..];
        for (int i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == 0x0B && data[i + 1] == 0x77)
                return 0x81;
            if (data[i] == 0xFF && (data[i + 1] & 0xF6) == 0xF0)
                return 0x0F;
        }
        return 0x0F;
    }

    private static bool IsVideoStreamType(byte type) => type is 0x01 or 0x02 or 0x10 or 0x1B or 0x24 or 0x42;

    private static bool TryDecodeTimestamp(ReadOnlySpan<byte> bytes, out long value)
    {
        value = -1;
        if (bytes.Length < 5 || (bytes[0] & 1) == 0 || (bytes[2] & 1) == 0 || (bytes[4] & 1) == 0)
            return false;

        value = ((long)((bytes[0] >> 1) & 7) << 30) |
                ((long)bytes[1] << 22) |
                ((long)((bytes[2] >> 1) & 0x7F) << 15) |
                ((long)bytes[3] << 7) |
                (long)((bytes[4] >> 1) & 0x7F);
        return true;
    }

    private static bool TryReadPcr(ReadOnlySpan<byte> ts, PacketInfo info, out long pcr)
    {
        pcr = -1;
        if (!info.HasAdaptation || ts.Length < 12)
            return false;

        int length = ts[4];
        if (length < 7 || 5 + length > ts.Length || (ts[5] & 0x10) == 0)
            return false;

        long baseValue = ((long)ts[6] << 25) |
                         ((long)ts[7] << 17) |
                         ((long)ts[8] << 9) |
                         ((long)ts[9] << 1) |
                         (long)(ts[10] >> 7);
        int extension = ((ts[10] & 1) << 8) | ts[11];
        pcr = baseValue * 300 + extension;
        return true;
    }

    private readonly record struct PacketInfo(
        int Pid,
        int Continuity,
        bool PayloadUnitStart,
        bool HasPayload,
        bool HasAdaptation,
        int PayloadOffset);

    private static bool TryReadPacket(ReadOnlySpan<byte> packet, int syncOffset, out PacketInfo info)
    {
        info = default;
        if (syncOffset < 0 || syncOffset + TsPacketBytes > packet.Length || packet[syncOffset] != 0x47)
            return false;

        ReadOnlySpan<byte> ts = packet.Slice(syncOffset, TsPacketBytes);
        if ((ts[1] & 0x80) != 0)
            return false;

        int control = (ts[3] >> 4) & 0x03;
        if (control == 0)
            return false;

        bool hasPayload = control is 1 or 3;
        bool hasAdaptation = control is 2 or 3;
        int payloadOffset = 4;
        if (hasAdaptation)
        {
            int length = ts[4];
            if (length > 183 || 5 + length > TsPacketBytes)
                return false;
            payloadOffset = 5 + length;
        }

        if (!hasPayload)
            payloadOffset = TsPacketBytes;

        info = new PacketInfo(
            ((ts[1] & 0x1F) << 8) | ts[2],
            ts[3] & 0x0F,
            (ts[1] & 0x40) != 0,
            hasPayload,
            hasAdaptation,
            payloadOffset);
        return true;
    }

    private static void NormalizeContinuity(Span<byte> ts, PacketInfo info, Dictionary<int, int> counters)
    {
        if (!info.HasPayload)
            return;

        int next = counters.TryGetValue(info.Pid, out int expected) ? expected : info.Continuity;
        ts[3] = (byte)((ts[3] & 0xF0) | (next & 0x0F));
        counters[info.Pid] = (next + 1) & 0x0F;
    }

    private static PidEvidence GetEvidence(Analysis analysis, int pid)
    {
        if (!analysis.Pids.TryGetValue(pid, out PidEvidence? evidence))
        {
            evidence = new PidEvidence();
            analysis.Pids[pid] = evidence;
        }
        return evidence;
    }

    private static TransportGeometry? DetectGeometry(FileStream input)
    {
        if (input.Length < TsPacketBytes * MinimumSyncPackets)
            return null;

        byte[] buffer = new byte[IoBufferBytes + M2TsPacketBytes * MinimumSyncPackets];
        int carry = 0;
        long position = 0;
        while (position < input.Length)
        {
            int request = (int)Math.Min(IoBufferBytes, input.Length - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;

            int count = carry + read;
            ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                if (HasPacketRun(data, i, TsPacketBytes, 0, MinimumSyncPackets))
                    return new TransportGeometry(TsPacketBytes, 0, position - carry + i);
                if (HasPacketRun(data, i, M2TsPacketBytes, 4, MinimumSyncPackets))
                    return new TransportGeometry(M2TsPacketBytes, 4, position - carry + i);
            }

            carry = Math.Min(M2TsPacketBytes * MinimumSyncPackets, count);
            buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return null;
    }

    private static TransportGeometry? DetectGeometryFromReference(FileStream input, ReferenceVideoProfile? reference)
    {
        if (reference is null)
            return null;
        int packetSize = reference.TransportPacketSize;
        int syncOffset = reference.TransportSyncOffset;
        const int requiredPackets = 4;
        if (input.Length < packetSize * requiredPackets)
            return null;

        byte[] buffer = new byte[IoBufferBytes + M2TsPacketBytes * requiredPackets];
        int carry = 0;
        long position = 0;
        while (position < input.Length)
        {
            int request = (int)Math.Min(IoBufferBytes, input.Length - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;
            int count = carry + read;
            ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                if (HasPacketRun(data, i, packetSize, syncOffset, requiredPackets))
                    return new TransportGeometry(packetSize, syncOffset, position - carry + i, requiredPackets);
            }
            carry = Math.Min(M2TsPacketBytes * requiredPackets, count);
            buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return null;
    }

    private static bool HasPacketRun(ReadOnlySpan<byte> data, int start, int packetSize, int syncOffset, int count)
    {
        if (start < 0 || start + syncOffset + packetSize * (count - 1) >= data.Length)
            return false;
        for (int i = 0; i < count; i++)
        {
            int at = start + syncOffset + i * packetSize;
            if (data[at] != 0x47 || at + 4 > data.Length || (data[at + 3] & 0x30) == 0)
                return false;
        }
        return true;
    }

    private static long FindNextPacketRun(FileStream input, long start, TransportGeometry geometry, long end)
    {
        int requiredPackets = Math.Clamp(geometry.ResyncPackets, 4, MinimumSyncPackets);
        int overlap = geometry.PacketSize * requiredPackets + geometry.SyncOffset;
        byte[] buffer = new byte[IoBufferBytes + overlap];
        int carry = 0;
        long position = start;

        while (position < end)
        {
            int request = (int)Math.Min(IoBufferBytes, end - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;

            int count = carry + read;
            ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                if (HasPacketRun(data, i, geometry.PacketSize, geometry.SyncOffset, requiredPackets))
                    return position - carry + i;
            }

            carry = Math.Min(overlap, count);
            buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return -1;
    }

    private static long ReadM2TsClock(ReadOnlySpan<byte> packet, TransportGeometry geometry, long fallback)
    {
        if (geometry.SyncOffset != 4 || packet.Length < 4)
            return fallback;
        return BinaryPrimitives.ReadUInt32BigEndian(packet[..4]) & 0x3FFFFFFF;
    }

    private static void ValidateOutput(
        string destinationPath,
        TransportGeometry geometry,
        ProgramMap expected,
        out long verifiedPackets)
    {
        using var input = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < geometry.PacketSize * 20L || input.Length % geometry.PacketSize != 0)
            throw new InvalidDataException("Yeniden kurulan TS paket uzunluğu tutarsız.");

        var packet = new byte[geometry.PacketSize];
        var programs = new Dictionary<ushort, int>();
        var maps = new Dictionary<int, ProgramMap>();
        var continuity = new Dictionary<int, int>();
        long checkedPackets = 0;
        long videoPes = 0;

        while (checkedPackets < 100_000 && ReadExactly(input, packet))
        {
            if (!TryReadPacket(packet, geometry.SyncOffset, out PacketInfo info))
                throw new InvalidDataException("Yeniden kurulan akışta geçersiz TS paketi bulundu.");

            if (info.HasPayload)
            {
                if (continuity.TryGetValue(info.Pid, out int previous) && info.Continuity != ((previous + 1) & 0x0F))
                    throw new InvalidDataException($"PID 0x{info.Pid:X4} continuity doğrulaması geçilemedi.");
                continuity[info.Pid] = info.Continuity;
            }

            ReadOnlySpan<byte> ts = packet.AsSpan(geometry.SyncOffset, TsPacketBytes);
            if (info.PayloadUnitStart && info.HasPayload && info.PayloadOffset < TsPacketBytes)
            {
                ReadOnlySpan<byte> payload = ts[info.PayloadOffset..];
                if (info.Pid == 0)
                    ParsePat(payload, programs);
                if (info.Pid == expected.PmtPid)
                    ParsePmt(payload, info.Pid, maps);
                if (payload.Length >= 4 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1 && payload[3] is >= 0xE0 and <= 0xEF)
                    videoPes++;
            }
            checkedPackets++;
        }

        if (!programs.TryGetValue(expected.ProgramNumber, out int pmtPid) || pmtPid != expected.PmtPid)
            throw new InvalidDataException("Üretilen PAT program haritası doğrulanamadı.");
        if (!maps.TryGetValue(expected.PmtPid, out ProgramMap? map) || map.PcrPid != expected.PcrPid || map.Streams.Count == 0)
            throw new InvalidDataException("Üretilen PMT/PCR/stream PID haritası doğrulanamadı.");
        if (videoPes == 0)
            throw new InvalidDataException("Yeniden kurulan TS içinde video PES başlangıcı doğrulanamadı.");

        verifiedPackets = checkedPackets;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
                return false;
            total += read;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
