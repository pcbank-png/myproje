using System.Diagnostics;
using System.IO;
using System.Threading;
using LibVLCSharp.Shared;

namespace NSXVeriKurtarmaPro.Services;

internal sealed record VideoDecodeHealthResult(
    bool Success,
    int Score,
    int PassedSamples,
    int TotalSamples,
    long DurationMilliseconds,
    bool TimelineValidated,
    string Summary,
    long DecodedFrames = 0,
    long LostPictures = 0,
    long CorruptedBlocks = 0,
    double BrokenFrameRatio = 0,
    double DecoderErrorDensity = 0,
    bool AudioTrackPresent = false,
    bool AudioDecodeVerified = false,
    long DecodedAudioFrames = 0,
    long PlayedAudioBuffers = 0,
    long LostAudioBuffers = 0,
    double AudioLossRatio = 0);

internal static class VideoDecodeValidationService
{
    private const int StartTimeoutMilliseconds = 5_000;
    private const int SeekTimeoutMilliseconds = 4_500;
    private const int PollMilliseconds = 70;

    private const int RegionDecodeWindowMilliseconds = 1_500;

    public static VideoDecodeHealthResult Validate(
        string videoPath,
        Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return Failed("Onarılan video dosyası bulunamadı.");

        try
        {
            Core.Initialize();
            using var libVlc = new LibVLC(
                "--intf=dummy",
                "--vout=dummy",
                "--aout=dummy",
                "--no-video-title-show",
                "--no-osd",
                "--quiet",
                "--file-caching=150",
                "--avcodec-hw=none");

            progress?.Invoke("Gerçek video decoder başlatılıyor • başlangıç karesi doğrulanıyor...");
            DecodeProbe start = Probe(libVlc, videoPath, 0, requireSeek: false);
            if (!start.Success)
                return Failed($"Başlangıç bölgesinde gerçek video karesi çözümlenemedi: {start.Message}");

            long duration = start.DurationMilliseconds;
            bool timelineAvailable = duration >= 2_000 && start.Seekable;

            if (!timelineAvailable)
            {
                int singleScore = start.VideoOutputSeen && start.TimeAdvanced ? 65 : 55;
                double singleAudioLossRatio = start.LostAudioBuffers /
                                              (double)Math.Max(1, start.PlayedAudioBuffers + start.LostAudioBuffers);
                bool audioVerified = start.AudioTrackPresent &&
                                     (start.DecodedAudioFrames > 0 || start.PlayedAudioBuffers > 0);
                string singleAudioText = start.AudioTrackPresent
                    ? $" Ses decode {(audioVerified ? "doğrulandı" : "doğrulanamadı")}; buffer kaybı %{singleAudioLossRatio * 100:0.00}."
                    : " Ses track'i bulunmadı.";
                return new VideoDecodeHealthResult(
                    Success: true,
                    Score: singleScore,
                    PassedSamples: 1,
                    TotalSamples: 1,
                    DurationMilliseconds: Math.Max(0, duration),
                    TimelineValidated: false,
                    Summary: $"Gerçek decoder başlangıç videosunu doğruladı; bu ham/seek edilemeyen akışta zaman çizgisi örneklemesi kullanılamadı (sağlık %{singleScore}).{singleAudioText}",
                    DecodedFrames: start.DecodedFrames,
                    LostPictures: start.LostPictures,
                    CorruptedBlocks: start.CorruptedBlocks,
                    AudioTrackPresent: start.AudioTrackPresent,
                    AudioDecodeVerified: audioVerified,
                    DecodedAudioFrames: start.DecodedAudioFrames,
                    PlayedAudioBuffers: start.PlayedAudioBuffers,
                    LostAudioBuffers: start.LostAudioBuffers,
                    AudioLossRatio: singleAudioLossRatio);
            }

            int totalSamples = DetermineAdaptiveSampleCount(duration);
            int passed = 1;
            bool firstQuarterPassed = true;
            bool lastQuarterPassed = false;
            long decodedFrames = start.DecodedFrames;
            long lostPictures = start.LostPictures;
            long corruptedBlocks = start.CorruptedBlocks;
            long discontinuities = start.Discontinuities;
            bool audioTrackPresent = start.AudioTrackPresent;
            long decodedAudioFrames = start.DecodedAudioFrames;
            long playedAudioBuffers = start.PlayedAudioBuffers;
            long lostAudioBuffers = start.LostAudioBuffers;

            for (int sampleNumber = 2; sampleNumber <= totalSamples; sampleNumber++)
            {
                double fraction = sampleNumber / (double)(totalSamples + 1);
                long target = Math.Clamp((long)Math.Round(duration * fraction), 0, Math.Max(0, duration - 150));
                progress?.Invoke(
                    $"Adaptif forensic decode {sampleNumber}/{totalSamples} • %{fraction * 100:0.0} bölgesi • " +
                    $"{RegionDecodeWindowMilliseconds / 1000d:0.0} sn gerçek kare penceresi...");

                DecodeProbe probe = Probe(libVlc, videoPath, target, requireSeek: true);
                decodedFrames += probe.DecodedFrames;
                lostPictures += probe.LostPictures;
                corruptedBlocks += probe.CorruptedBlocks;
                discontinuities += probe.Discontinuities;
                audioTrackPresent |= probe.AudioTrackPresent;
                decodedAudioFrames += probe.DecodedAudioFrames;
                playedAudioBuffers += probe.PlayedAudioBuffers;
                lostAudioBuffers += probe.LostAudioBuffers;
                if (!probe.Success)
                    continue;

                passed++;
                if (fraction <= 0.25d)
                    firstQuarterPassed = true;
                if (fraction >= 0.75d)
                    lastQuarterPassed = true;
            }

            double regionRatio = passed / (double)totalSamples;
            double brokenRatio = lostPictures / (double)Math.Max(1, decodedFrames + lostPictures);
            double errorDensity = (lostPictures + corruptedBlocks + discontinuities) /
                                  (double)Math.Max(1, decodedFrames + lostPictures + corruptedBlocks + discontinuities);
            int regionScore = (int)Math.Round(regionRatio * 75);
            int frameScore = (int)Math.Round(Math.Max(0, 1d - Math.Min(1d, brokenRatio * 4d)) * 15);
            int errorScore = (int)Math.Round(Math.Max(0, 1d - Math.Min(1d, errorDensity * 3d)) * 10);
            int score = Math.Clamp(regionScore + frameScore + errorScore, 0, 100);
            bool success = regionRatio >= 0.70d && firstQuarterPassed && lastQuarterPassed && brokenRatio <= 0.20d;
            double audioLossRatio = lostAudioBuffers /
                                    (double)Math.Max(1, playedAudioBuffers + lostAudioBuffers);
            bool audioDecodeVerified = audioTrackPresent &&
                                       (decodedAudioFrames > 0 || playedAudioBuffers > 0);
            string audioText = audioTrackPresent
                ? $" Ses decode {(audioDecodeVerified ? "doğrulandı" : "doğrulanamadı")}; " +
                  $"decoded-audio {decodedAudioFrames:N0}, buffer {playedAudioBuffers:N0}, " +
                  $"kayıp %{audioLossRatio * 100:0.00}."
                : " Ses track'i bulunmadı.";

            string summary =
                $"{totalSamples} adaptif bölgenin {passed} tanesinde gerçek GOP/frame decode edildi; " +
                $"kare kayıp oranı %{brokenRatio * 100:0.00}, decoder hata yoğunluğu %{errorDensity * 100:0.00}, sağlık %{score}." +
                audioText;
            return new VideoDecodeHealthResult(
                success,
                score,
                passed,
                totalSamples,
                duration,
                TimelineValidated: true,
                summary,
                decodedFrames,
                lostPictures,
                corruptedBlocks,
                brokenRatio,
                errorDensity,
                audioTrackPresent,
                audioDecodeVerified,
                decodedAudioFrames,
                playedAudioBuffers,
                lostAudioBuffers,
                audioLossRatio);
        }
        catch (Exception ex)
        {
            return Failed($"Global video decode motoru doğrulama yapamadı: {ex.Message}");
        }
    }

    private static DecodeProbe Probe(
        LibVLC libVlc,
        string path,
        long targetMilliseconds,
        bool requireSeek)
    {
        using var media = new Media(libVlc, path, FromType.FromPath);
        using var player = new MediaPlayer(libVlc);

        int encounteredError = 0;
        int ended = 0;
        player.EncounteredError += (_, _) => Interlocked.Exchange(ref encounteredError, 1);
        player.EndReached += (_, _) => Interlocked.Exchange(ref ended, 1);

        try
        {
            player.Mute = true;
            player.Play(media);

            if (!WaitForVideoOutput(player, ref encounteredError, ref ended, StartTimeoutMilliseconds))
                return new DecodeProbe(false, player.Length, player.IsSeekable, false, false, "Video decoder çıktı üretmedi.", 0, 0, 0, 0);

            long duration = player.Length;
            bool seekable = player.IsSeekable;
            bool audioTrackPresent = HasAudioTrack(media, player);

            if (requireSeek)
            {
                if (!seekable)
                    return new DecodeProbe(false, duration, false, player.VoutCount > 0, false, "Akış zaman içinde seek edilemiyor.", 0, 0, 0, 0);

                Interlocked.Exchange(ref ended, 0);
                long beforeSeek = player.Time;
                player.Time = targetMilliseconds;

                if (!WaitForTargetDecode(player, targetMilliseconds, duration, ref encounteredError, ref ended))
                    return new DecodeProbe(false, duration, seekable, player.VoutCount > 0, false, "Hedef zaman bölgesinde kare decode edilemedi.", 0, 0, 0, 0);

                long afterSeek = player.Time;
                bool advanced = afterSeek >= Math.Max(0, targetMilliseconds - SeekTolerance(targetMilliseconds)) &&
                                Math.Abs(afterSeek - beforeSeek) >= 80;
                DecodeWindowMetrics metrics = MeasureDecodeWindow(player, media, RegionDecodeWindowMilliseconds);
                bool regionHealthy = advanced && metrics.DecodedFrames > 0 &&
                                     metrics.LostPictures <= Math.Max(3, metrics.DecodedFrames / 4);
                return new DecodeProbe(regionHealthy, duration, seekable, player.VoutCount > 0, advanced,
                    regionHealthy ? "GOP/kare penceresi çözümlendi." : "Decoder bölgesinde kayıp/hata yoğunluğu yüksek.",
                    metrics.DecodedFrames,
                    metrics.LostPictures,
                    metrics.CorruptedBlocks,
                    metrics.Discontinuities,
                    audioTrackPresent,
                    metrics.DecodedAudioFrames,
                    metrics.PlayedAudioBuffers,
                    metrics.LostAudioBuffers);
            }

            long firstTime = Math.Max(0, player.Time);
            var clock = Stopwatch.StartNew();
            bool timeAdvanced = false;
            while (clock.ElapsedMilliseconds < 1_500 && Volatile.Read(ref encounteredError) == 0)
            {
                long now = player.Time;
                if (player.VoutCount > 0 && now >= firstTime + 80)
                {
                    timeAdvanced = true;
                    break;
                }
                if (Volatile.Read(ref ended) != 0 && player.VoutCount > 0)
                {
                    timeAdvanced = true;
                    break;
                }
                Thread.Sleep(PollMilliseconds);
            }

            DecodeWindowMetrics startMetrics = ReadMetrics(media);
            return new DecodeProbe(
                player.VoutCount > 0 && timeAdvanced,
                duration,
                seekable,
                player.VoutCount > 0,
                timeAdvanced,
                timeAdvanced ? "Başlangıç karesi çözümlendi." : "Başlangıç decoder zamanı ilerlemedi.",
                startMetrics.DecodedFrames,
                startMetrics.LostPictures,
                startMetrics.CorruptedBlocks,
                startMetrics.Discontinuities,
                audioTrackPresent,
                startMetrics.DecodedAudioFrames,
                startMetrics.PlayedAudioBuffers,
                startMetrics.LostAudioBuffers);
        }
        finally
        {
            try { player.Stop(); } catch { }
        }
    }

    private static int DetermineAdaptiveSampleCount(long durationMilliseconds)
    {
        if (durationMilliseconds < 30_000)
            return 8;
        if (durationMilliseconds < 60_000)
            return 12;

        // Long recordings use 20-100 distributed forensic regions. Two regions per
        // minute reaches the cap at fifty minutes while remaining deterministic.
        return Math.Clamp((int)Math.Ceiling(durationMilliseconds / 60_000d * 2d), 20, 100);
    }

    private static DecodeWindowMetrics MeasureDecodeWindow(
        MediaPlayer player,
        Media media,
        int milliseconds)
    {
        DecodeWindowMetrics before = ReadMetrics(media);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds && player.State is not VLCState.Error and not VLCState.Stopped)
            Thread.Sleep(PollMilliseconds);
        DecodeWindowMetrics after = ReadMetrics(media);
        return new DecodeWindowMetrics(
            Math.Max(0, after.DecodedFrames - before.DecodedFrames),
            Math.Max(0, after.LostPictures - before.LostPictures),
            Math.Max(0, after.CorruptedBlocks - before.CorruptedBlocks),
            Math.Max(0, after.Discontinuities - before.Discontinuities),
            Math.Max(0, after.DecodedAudioFrames - before.DecodedAudioFrames),
            Math.Max(0, after.PlayedAudioBuffers - before.PlayedAudioBuffers),
            Math.Max(0, after.LostAudioBuffers - before.LostAudioBuffers));
    }

    private static DecodeWindowMetrics ReadMetrics(Media media)
    {
        MediaStats stats = media.Statistics;
        return new DecodeWindowMetrics(
            stats.DecodedVideo,
            stats.LostPictures,
            stats.DemuxCorrupted,
            stats.DemuxDiscontinuity,
            stats.DecodedAudio,
            stats.PlayedAudioBuffers,
            stats.LostAudioBuffers);
    }

    private static bool HasAudioTrack(Media media, MediaPlayer player)
    {
        try
        {
            if (player.AudioTrackCount > 0 || player.AudioTrack >= 0)
                return true;
        }
        catch
        {
            // Some raw/damaged streams do not expose the player track list.
        }

        try
        {
            return media.Tracks.Any(track => track.TrackType == TrackType.Audio);
        }
        catch
        {
            return false;
        }
    }

    private static bool WaitForVideoOutput(
        MediaPlayer player,
        ref int encounteredError,
        ref int ended,
        int timeoutMilliseconds)
    {
        var clock = Stopwatch.StartNew();
        long initialTime = -1;

        while (clock.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (Volatile.Read(ref encounteredError) != 0)
                return false;

            long current = player.Time;
            if (initialTime < 0 && current >= 0)
                initialTime = current;

            if (player.VoutCount > 0 && current >= 0)
                return true;

            if (Volatile.Read(ref ended) != 0)
                return player.VoutCount > 0;

            Thread.Sleep(PollMilliseconds);
        }

        return false;
    }

    private static bool WaitForTargetDecode(
        MediaPlayer player,
        long targetMilliseconds,
        long durationMilliseconds,
        ref int encounteredError,
        ref int ended)
    {
        var clock = Stopwatch.StartNew();
        long tolerance = SeekTolerance(targetMilliseconds);
        long lower = Math.Max(0, targetMilliseconds - tolerance);
        long upper = durationMilliseconds > 0
            ? Math.Min(durationMilliseconds, targetMilliseconds + Math.Max(3_000, tolerance * 3))
            : targetMilliseconds + Math.Max(3_000, tolerance * 3);
        long firstInRange = -1;

        while (clock.ElapsedMilliseconds < SeekTimeoutMilliseconds)
        {
            if (Volatile.Read(ref encounteredError) != 0)
                return false;

            long current = player.Time;
            bool inRange = current >= lower && current <= upper;
            if (player.VoutCount > 0 && inRange)
            {
                if (firstInRange < 0)
                    firstInRange = current;
                else if (current >= firstInRange + 80 || Volatile.Read(ref ended) != 0)
                    return true;
            }

            if (Volatile.Read(ref ended) != 0)
                return player.VoutCount > 0 && current >= lower;

            Thread.Sleep(PollMilliseconds);
        }

        return false;
    }

    private static long SeekTolerance(long targetMilliseconds) =>
        Math.Clamp(targetMilliseconds / 50, 750, 2_500);

    private static VideoDecodeHealthResult Failed(string message) =>
        new(false, 0, 0, 0, 0, false, message);

    private readonly record struct DecodeWindowMetrics(
        long DecodedFrames,
        long LostPictures,
        long CorruptedBlocks,
        long Discontinuities,
        long DecodedAudioFrames,
        long PlayedAudioBuffers,
        long LostAudioBuffers);

    private sealed record DecodeProbe(
        bool Success,
        long DurationMilliseconds,
        bool Seekable,
        bool VideoOutputSeen,
        bool TimeAdvanced,
        string Message,
        long DecodedFrames,
        long LostPictures,
        long CorruptedBlocks,
        long Discontinuities,
        bool AudioTrackPresent = false,
        long DecodedAudioFrames = 0,
        long PlayedAudioBuffers = 0,
        long LostAudioBuffers = 0);
}
