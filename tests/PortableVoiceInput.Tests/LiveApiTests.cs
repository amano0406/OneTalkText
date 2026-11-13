using PortableVoiceInput.Core;
using Xunit.Abstractions;

namespace PortableVoiceInput.Tests;

public sealed class LiveApiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CalibratedSyntheticEnglishAndJapaneseRemainTranscribableWhenExplicitlyEnabled()
    {
        var configFolder = Environment.GetEnvironmentVariable("VOICE_LIVE_CONFIG_FOLDER");
        var sampleFolder = Environment.GetEnvironmentVariable("VOICE_LIVE_SAMPLE_FOLDER");
        if (string.IsNullOrEmpty(configFolder) || string.IsNullOrEmpty(sampleFolder)) return;
        var settings = new ConfigStore(configFolder).Load();
        Assert.False(string.IsNullOrWhiteSpace(settings.ApiKey));
        settings.TrimSilence = true; settings.AutoSilenceThreshold = true; settings.NoiseMarginDb = 6; settings.TriggerWords = "";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OneTalkText-live-samples"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var processor = new RecordingProcessor(new OpenAiClient(http), Path.Combine(directory, "work"));
            foreach (var (name, language, expected) in new[] { ("english", "en", "meeting"), ("japanese", "ja", "会議") })
            {
                var samplePath = Path.Combine(sampleFolder, name + ".wav");
                var info = WaveMetadata.Read(samplePath).Info;
                Assert.Equal(16000, info.SampleRate);
                Assert.Equal(1, info.Channels);
                Assert.Equal(16, info.BitsPerSample);
                var speech = new byte[checked((int)info.DataLength)];
                using (var file = File.OpenRead(samplePath)) { file.Position = info.DataOffset; file.ReadExactly(speech); }
                var background = new byte[16000];
                for (var i = 0; i < 8000; i++)
                {
                    var sample = (short)(Math.Sin(i * Math.PI * 2 * 440 / 16000) * 0.001 * short.MaxValue);
                    background[i * 2] = (byte)sample; background[i * 2 + 1] = (byte)(sample >> 8);
                }
                var calibration = new NoiseCalibrationAccumulator().Add(background, settings.NoiseMarginDb);
                Assert.NotNull(calibration); Assert.True(calibration.IsReliable);
                var path = Path.Combine(directory, name + ".wav");
                using (var writer = new DurableWaveWriter(path)) { writer.Write(background); writer.Write(speech); writer.Write(new byte[32000]); }
                WaveMetadata.Write(path, new RecordingMetadata
                {
                    CalibrationReliable = true, CalibratedNoiseDb = calibration.NoiseDb,
                    CalibratedThresholdDb = calibration.ThresholdDb, ProtectedStartSeconds = 0.5
                });
                settings.Language = language;
                var result = await processor.ProcessAsync(path, settings);
                Assert.True(result.HasOutput);
                Assert.Contains(expected, result.Metadata.FinalText, StringComparison.OrdinalIgnoreCase);
                output.WriteLine($"{name} synthetic calibration and transcription passed.");
            }
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedEnglishAndJapaneseAudioCanBeTranscribedWhenExplicitlyEnabled()
    {
        var configFolder = Environment.GetEnvironmentVariable("VOICE_LIVE_CONFIG_FOLDER");
        var sampleFolder = Environment.GetEnvironmentVariable("VOICE_LIVE_SAMPLE_FOLDER");
        if (string.IsNullOrEmpty(configFolder) || string.IsNullOrEmpty(sampleFolder)) return;
        var settings = new ConfigStore(configFolder).Load();
        Assert.False(string.IsNullOrWhiteSpace(settings.ApiKey));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var client = new OpenAiClient(http);
        settings.Language = "en";
        var english = await client.TranscribeAsync(Path.Combine(sampleFolder, "english.wav"), settings, CancellationToken.None);
        settings.Language = "ja";
        var japanese = await client.TranscribeAsync(Path.Combine(sampleFolder, "japanese.wav"), settings, CancellationToken.None);
        output.WriteLine("English sample: " + english);
        output.WriteLine("Japanese sample: " + japanese);
        Assert.Contains("meeting", english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("会議", japanese);

        var refined = await client.RefineAsync("えっと、明日の会議は午後二時です。整理して。", "整理して", settings, CancellationToken.None);
        output.WriteLine("Refined Japanese sample: " + refined);
        Assert.Contains("会議", refined);
        Assert.DoesNotContain("整理して", refined);
    }
}
