using System.Security.Cryptography;
using System.Text;

namespace PortableVoiceInput.Core;

public sealed record AudioRange(long StartByte, long LengthBytes);
public sealed record AudioAnalysis(WaveInfo Wave, IReadOnlyList<AudioRange> Ranges, bool HasSignal, bool IsDigitalSilence, double PeakDb, double SentSeconds);

/// <summary>Finds long low-level spans without ever modifying the source recording.</summary>
public static class SilenceProcessor
{
    private const double FrameSeconds = 0.02;
    private const double BoundaryPaddingSeconds = 0.25;

    /// <summary>Approximate the final cuts for a rolling display of RMS levels.</summary>
    public static bool[] PreviewCuts(IReadOnlyList<double> levels, double frameSeconds, double thresholdDb, double minimumSeconds, double gapSeconds, bool enabled)
    {
        if (frameSeconds <= 0 || !double.IsFinite(frameSeconds)) throw new ArgumentOutOfRangeException(nameof(frameSeconds));
        var cut = new bool[levels.Count];
        if (!enabled || !levels.Any(x => x >= thresholdDb)) return cut;
        for (var start = 0; start < levels.Count;)
        {
            if (levels[start] >= thresholdDb) { start++; continue; }
            var end = start + 1; while (end < levels.Count && levels[end] < thresholdDb) end++;
            if ((end - start) * frameSeconds >= minimumSeconds)
            {
                var pad = (int)Math.Ceiling(BoundaryPaddingSeconds / frameSeconds);
                var retained = Math.Max((int)Math.Ceiling(gapSeconds / frameSeconds), pad * 2);
                var cutStart = start == 0 ? start : end == levels.Count ? Math.Min(end, start + pad) : Math.Min(end, start + retained / 2);
                var cutEnd = start == 0 ? Math.Max(cutStart, end - pad) : end == levels.Count ? end : Math.Max(cutStart, end - (retained - retained / 2));
                for (var i = cutStart; i < cutEnd; i++) cut[i] = true;
            }
            start = end;
        }
        return cut;
    }

    public static AudioAnalysis Analyze(string path, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var (wave, _) = WaveMetadata.Read(path);
        if (wave.Channels != 1 || wave.BitsPerSample != 16) throw new InvalidDataException("Only 16-bit mono PCM recordings can be processed.");
        var frameSamples = Math.Max(1, (int)Math.Round(wave.SampleRate * FrameSeconds));
        var buffer = new byte[frameSamples * 2];
        var totalSamples = wave.DataLength / 2;
        var minSilence = (long)(Math.Max(0.1, settings.MinimumSilenceSeconds) * wave.SampleRate);
        var gapSamples = (long)(Math.Max(0, settings.RemainingGapSeconds) * wave.SampleRate);
        var padding = (long)(BoundaryPaddingSeconds * wave.SampleRate);
        var cuts = new List<(long Start, long End)>();
        long samplePosition = 0, silentStart = -1;
        double peak = 0; bool hasSignal = false;

        void FinishSilence(long end)
        {
            if (silentStart < 0) return;
            var count = end - silentStart;
            if (settings.TrimSilence && count >= minSilence)
            {
                long cutStart, cutEnd;
                if (silentStart == 0) { cutStart = 0; cutEnd = Math.Max(0, end - padding); }
                else if (end == totalSamples) { cutStart = Math.Min(end, silentStart + padding); cutEnd = end; }
                else
                {
                    var retained = Math.Min(count, Math.Max(gapSamples, 2 * padding));
                    cutStart = silentStart + retained / 2;
                    cutEnd = end - (retained - retained / 2);
                }
                if (cutEnd > cutStart) cuts.Add((cutStart, cutEnd));
            }
            silentStart = -1;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        file.Position = wave.DataOffset;
        while (samplePosition < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = (int)Math.Min(buffer.Length, (totalSamples - samplePosition) * 2);
            var read = file.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            if (read != wanted) throw new EndOfStreamException("Recording audio ended unexpectedly.");
            double sum = 0;
            for (var i = 0; i < read; i += 2)
            {
                var sample = (short)(buffer[i] | buffer[i + 1] << 8);
                var normalized = sample / 32768.0;
                sum += normalized * normalized;
                peak = Math.Max(peak, Math.Abs(normalized));
            }
            var rms = Math.Sqrt(sum / (read / 2));
            var db = rms <= 1e-12 ? -120 : 20 * Math.Log10(rms);
            if (db < settings.SilenceThresholdDb) { if (silentStart < 0) silentStart = samplePosition; }
            else { hasSignal = true; FinishSilence(samplePosition); }
            samplePosition += read / 2;
        }
        FinishSilence(totalSamples);

        // If all frames are below threshold, keep the original intact; low volume is not proof of silence.
        var ranges = new List<AudioRange>();
        if (!hasSignal || !settings.TrimSilence) ranges.Add(new(0, wave.DataLength));
        else
        {
            long cursor = 0;
            foreach (var cut in cuts)
            {
                if (cut.Start > cursor) ranges.Add(new(cursor * 2, (cut.Start - cursor) * 2));
                cursor = Math.Max(cursor, cut.End);
            }
            if (cursor < totalSamples) ranges.Add(new(cursor * 2, (totalSamples - cursor) * 2));
        }
        var sent = ranges.Sum(r => r.LengthBytes) / (double)(wave.SampleRate * 2);
        return new(wave, ranges, hasSignal, peak <= 1.0 / 32768, peak <= 1e-12 ? -120 : 20 * Math.Log10(peak), sent);
    }

    public static string Signature(AppSettings s, WaveInfo wave)
    {
        var data = FormattableString.Invariant($"1|{wave.DataLength}|{wave.SampleRate}|{s.TrimSilence}|{s.SilenceThresholdDb:R}|{s.MinimumSilenceSeconds:R}|{s.RemainingGapSeconds:R}|{s.Language}|{s.TranscriptionModel}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    public static IReadOnlyList<string> CreateChunks(string source, AudioAnalysis analysis, string directory, long maximumBytes = 23_000_000, CancellationToken cancellationToken = default)
    {
        if (maximumBytes < 4096) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        maximumBytes &= ~1L;
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        DurableWaveWriter? writer = null;
        long currentBytes = 0;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
            var buffer = new byte[65536];
            void StartChunk()
            {
                var path = Path.Combine(directory, $"chunk-{paths.Count + 1:D4}.wav");
                writer = new DurableWaveWriter(path, analysis.Wave.SampleRate);
                paths.Add(path); currentBytes = 0;
            }
            foreach (var range in analysis.Ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (range.LengthBytes <= 0) continue;
                // Prefer a removed-silence boundary to cutting a sentence near the upload limit.
                if (writer is not null && currentBytes >= 4_000_000 && range.LengthBytes > maximumBytes - currentBytes)
                { writer.Dispose(); writer = null; }
                input.Position = analysis.Wave.DataOffset + range.StartByte;
                long remaining = range.LengthBytes;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (writer is null) StartChunk();
                    var count = (int)Math.Min(Math.Min(remaining, buffer.Length), maximumBytes - currentBytes);
                    count -= count % 2;
                    if (count == 0) { writer!.Dispose(); writer = null; continue; }
                    var read = input.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);
                    if (read != count) throw new EndOfStreamException("Recording audio ended unexpectedly.");
                    writer!.Write(buffer.AsSpan(0, read));
                    remaining -= read; currentBytes += read;
                    if (currentBytes >= maximumBytes) { writer.Dispose(); writer = null; }
                }
            }
        }
        finally { writer?.Dispose(); }
        return paths;
    }
}
