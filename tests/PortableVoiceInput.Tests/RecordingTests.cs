using System.Net;
using System.Text;
using PortableVoiceInput.Core;

namespace PortableVoiceInput.Tests;

public sealed class RecordingTests
{
    [Fact]
    public void TabResetsPreserveTheApiKeyAndOtherTabs()
    {
        var settings = new AppSettings
        {
            ApiKey = "sk-test", Language = "ja", MicrophoneName = "chosen mic", HotkeyKey = 65,
            TriggerWords = "custom trigger", CleanupPrompt = "custom prompt", SilenceThresholdDb = -61,
            MinimumSilenceSeconds = 4, RemainingGapSeconds = 3, NoiseMarginDb = 8, TrimSilence = false, WaveformLeft = 200
        };
        settings.ResetGeneral();
        Assert.Equal("sk-test", settings.ApiKey);
        Assert.Equal("en", settings.Language);
        var generalDefaults = new AppSettings();
        Assert.Equal(generalDefaults.CleanupPrompt, settings.CleanupPrompt);
        Assert.Equal(generalDefaults.TriggerWords, settings.TriggerWords);
        Assert.Equal(generalDefaults.MicrophoneName, settings.MicrophoneName);
        Assert.Equal(generalDefaults.HotkeyKey, settings.HotkeyKey);
        Assert.Equal(-61, settings.SilenceThresholdDb);
        Assert.Equal(4, settings.MinimumSilenceSeconds);
        Assert.False(settings.TrimSilence);
        Assert.Equal(200, settings.WaveformLeft);

        settings.Language = "ja"; settings.CleanupPrompt = "custom prompt";
        settings.ResetAudio();
        var defaults = new AppSettings();
        Assert.Equal("sk-test", settings.ApiKey);
        Assert.Equal("ja", settings.Language);
        Assert.Equal("custom prompt", settings.CleanupPrompt);
        Assert.Equal(defaults.SilenceThresholdDb, settings.SilenceThresholdDb);
        Assert.Equal(defaults.MinimumSilenceSeconds, settings.MinimumSilenceSeconds);
        Assert.Equal(defaults.RemainingGapSeconds, settings.RemainingGapSeconds);
        Assert.Equal(defaults.NoiseMarginDb, settings.NoiseMarginDb);
        Assert.Equal(defaults.TrimSilence, settings.TrimSilence);
        Assert.Equal(200, settings.WaveformLeft);
    }

    [Fact]
    public void ExistingDefaultPromptIsUpdatedButACustomPromptIsPreserved()
    {
        using var workspace = new TestWorkspace();
        var store = new ConfigStore(workspace.File("settings"));
        store.Save(new AppSettings { ApiKey = "sk-test", CleanupPrompt = AppSettings.LegacyDefaultCleanupPrompt });
        var original = File.ReadAllBytes(store.Path);
        Assert.Equal(AppSettings.DefaultCleanupPrompt, store.Load().CleanupPrompt);
        Assert.Equal(original, File.ReadAllBytes(store.Path));
        store.Save(new AppSettings { ApiKey = "sk-test", CleanupPrompt = "custom prompt" });
        var custom = store.Load();
        Assert.Equal("custom prompt", custom.CleanupPrompt);
        Assert.Equal("sk-test", custom.ApiKey);
    }

    [Fact]
    public void MetadataCheckpointsLeaveOriginalAudioIntactAndRecoverFromPartialMetadata()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("audio.wav");
        var samples = Signal(16000, 0.5);
        using (var writer = new DurableWaveWriter(path)) writer.Write(samples);
        var before = ReadAudio(path);
        var metadata = new RecordingMetadata { Status = RecordingStatus.Failed, Transcript = "こんにちは", Error = "network" };
        WaveMetadata.Write(path, metadata);
        metadata.Status = RecordingStatus.Complete;
        metadata.FinalText = "Hello, 日本語。";
        WaveMetadata.Write(path, metadata);
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write)) file.Write([0x70, 0x76, 0x6d]);
        var (_, read) = WaveMetadata.Read(path);
        Assert.Equal(RecordingStatus.Complete, read.Status);
        Assert.Equal("Hello, 日本語。", read.FinalText);
        Assert.Equal(before, ReadAudio(path));
        read.FinalText = "recovered";
        WaveMetadata.Write(path, read);
        Assert.Equal("recovered", WaveMetadata.Read(path).Metadata.FinalText);
        Assert.Equal(before, ReadAudio(path));
    }

    [Fact]
    public void InterruptedRecordingRecoveryRepairsHeaderWithoutDroppingAudio()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("unfinished.recording.wav");
        var samples = Signal(8000, 0.4);
        using (var writer = new DurableWaveWriter(path)) writer.Write(samples);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.Position = 4; file.Write(BitConverter.GetBytes((uint)36));
            file.Position = 40; file.Write(BitConverter.GetBytes((uint)0));
        }
        DurableWaveWriter.Recover(path);
        Assert.Equal(samples, ReadAudio(path));
        Assert.Equal(0.5, WaveMetadata.Read(path).Info.DurationSeconds, 3);
    }

    [Fact]
    public void LongQuietGapIsShortenedButOriginalAndSpeechRemain()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("gaps.wav");
        using (var writer = new DurableWaveWriter(path))
        {
            writer.Write(Signal(16000, 0.4));
            writer.Write(new byte[16000 * 2 * 5]);
            writer.Write(Signal(16000, 0.4));
        }
        var original = ReadAudio(path);
        var settings = new AppSettings { SilenceThresholdDb = -45, MinimumSilenceSeconds = 2, RemainingGapSeconds = 1 };
        var analysis = SilenceProcessor.Analyze(path, settings);
        Assert.True(analysis.HasSignal);
        Assert.InRange(analysis.SentSeconds, 2.95, 3.05);
        var chunks = SilenceProcessor.CreateChunks(path, analysis, workspace.File("chunks"));
        var processed = ReadAudio(Assert.Single(chunks));
        Assert.Equal(Signal(16000, 0.4), processed[..32000]);
        Assert.Equal(Signal(16000, 0.4), processed[^32000..]);
        Assert.Equal(original, ReadAudio(path));
    }

    [Fact]
    public void SilencePreviewRespectsDurationAndRetainedGap()
    {
        var levels = Enumerable.Repeat(-20d, 20).Concat(Enumerable.Repeat(-70d, 100)).Concat(Enumerable.Repeat(-20d, 20)).ToArray();
        var cuts = SilenceProcessor.PreviewCuts(levels, .05, -45, 2, 1, true);
        Assert.Equal(80, cuts.Count(x => x));
        Assert.All(cuts.Take(30), x => Assert.False(x));
        Assert.All(cuts.Skip(110), x => Assert.False(x));
        Assert.DoesNotContain(true, SilenceProcessor.PreviewCuts(levels, .05, -45, 6, 1, true));
        Assert.DoesNotContain(true, SilenceProcessor.PreviewCuts(levels, .05, -45, 2, 1, false));
        Assert.DoesNotContain(true, SilenceProcessor.PreviewCuts(Enumerable.Repeat(-70d, 100).ToArray(), .05, -45, 2, 1, true));
    }

    [Fact]
    public void QuietAudioIsRetainedRatherThanDiscarded()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("quiet.wav");
        using (var writer = new DurableWaveWriter(path)) writer.Write(Signal(16000, 0.0005));
        var result = SilenceProcessor.Analyze(path, new AppSettings { SilenceThresholdDb = -40 });
        Assert.False(result.HasSignal);
        Assert.False(result.IsDigitalSilence);
        Assert.Equal(result.Wave.DataLength, Assert.Single(result.Ranges).LengthBytes);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TwoHourRecordingCanBeAnalyzedWithoutLosingTheOriginal()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("two-hours.wav");
        using (new DurableWaveWriter(path)) { }
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(44 + 7200L * 16000 * 2);
            file.Position = 44 + 3600L * 16000 * 2;
            file.Write(Signal(16000, 0.4));
        }
        DurableWaveWriter.Recover(path);
        var analysis = SilenceProcessor.Analyze(path, new AppSettings());
        Assert.Equal(7200, analysis.Wave.DurationSeconds, 3);
        Assert.InRange(analysis.SentSeconds, 1.45, 1.55);
        Assert.Equal(44 + 7200L * 16000 * 2, new FileInfo(path).Length);
    }

    [Fact]
    public async Task DigitalSilenceDoesNotNeedAKeyAndIsNotAFailure()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("silent.wav");
        using (var writer = new DurableWaveWriter(path)) writer.Write(new byte[32000]);
        var client = new OpenAiClient(new HttpClient(new ScriptedHandler([])));
        var result = await new RecordingProcessor(client, workspace.File("work")).ProcessAsync(path, new AppSettings());
        Assert.Equal(RecordingStatus.NoSpeech, result.Metadata.Status);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("会議の資料です。整理して。", "整理して\nきれいにして", "整理して")]
    [InlineData("This is my message, CLEAN THIS UP!", "clean this up", "clean this up")]
    [InlineData("整理して、資料を作って", "整理して", null)]
    public void OnlyFinalTriggerActivatesRefinement(string text, string triggers, string? expected) =>
        Assert.Equal(expected, TriggerDetector.FindAtEnd(text, triggers));

    [Fact]
    public async Task FailedUploadKeepsAudioAndRetryReusesCompletedChunks()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("long.wav");
        using (var writer = new DurableWaveWriter(path))
        {
            var oneSecond = Signal(16000, 0.4);
            for (var i = 0; i < 730; i++) writer.Write(oneSecond); // over the 23 MB upload boundary
        }
        var original = ReadAudio(path);
        var handler = new ScriptedHandler([HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable]);
        var settings = new AppSettings { ApiKey = "sk-test-key", TrimSilence = false, Language = "en", TriggerWords = "polish this" };
        var processor = new RecordingProcessor(new OpenAiClient(new HttpClient(handler), (_, _) => Task.CompletedTask), workspace.File("work"));
        await Assert.ThrowsAsync<OpenAiException>(() => processor.ProcessAsync(path, settings));
        var failed = WaveMetadata.Read(path).Metadata;
        Assert.Equal(RecordingStatus.Failed, failed.Status);
        Assert.Single(failed.CompletedChunks);
        Assert.Equal(original, ReadAudio(path));

        var retryHandler = new ScriptedHandler([HttpStatusCode.OK]);
        var retry = new RecordingProcessor(new OpenAiClient(new HttpClient(retryHandler), (_, _) => Task.CompletedTask), workspace.File("work"));
        var result = await retry.ProcessAsync(path, settings);
        Assert.Equal(RecordingStatus.Complete, result.Metadata.Status);
        Assert.Equal(1, retryHandler.CallCount);
        Assert.Equal(2, result.Metadata.CompletedChunks.Count);
        Assert.Equal(original, ReadAudio(path));
    }

    [Fact]
    public async Task EmptyApiResultIsNotAProcessingFailureAndOriginalIsKept()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.File("empty-result.wav");
        using (var writer = new DurableWaveWriter(path)) writer.Write(Signal(16000, 0.4));
        var handler = new ScriptedHandler([HttpStatusCode.OK], "");
        var processor = new RecordingProcessor(new OpenAiClient(new HttpClient(handler), (_, _) => Task.CompletedTask), workspace.File("work"));
        var result = await processor.ProcessAsync(path, new AppSettings { ApiKey = "sk-test-key", TrimSilence = false });
        Assert.Equal(RecordingStatus.NoSpeech, result.Metadata.Status);
        Assert.True(File.Exists(path));
    }

    private static byte[] Signal(int count, double amplitude)
    {
        var result = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var sample = (short)(Math.Sin(i * Math.PI * 2 * 440 / 16000) * amplitude * short.MaxValue);
            result[i * 2] = (byte)sample;
            result[i * 2 + 1] = (byte)(sample >> 8);
        }
        return result;
    }

    private static byte[] ReadAudio(string path)
    {
        var info = WaveMetadata.Read(path).Info;
        using var file = File.OpenRead(path);
        file.Position = info.DataOffset;
        var audio = new byte[checked((int)info.DataLength)];
        file.ReadExactly(audio);
        return audio;
    }

    private sealed class ScriptedHandler(IEnumerable<HttpStatusCode> responses, string text = "piece") : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _responses = new(responses);
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = _responses.Dequeue();
            var body = status == HttpStatusCode.OK ? System.Text.Json.JsonSerializer.Serialize(new { text }) : "{\"error\":{\"message\":\"temporary outage\"}}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-work"));
        private readonly string _path;
        public TestWorkspace() { _path = Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_path); }
        public string File(string name) => Path.Combine(_path, name);
        public void Dispose()
        {
            var full = Path.GetFullPath(_path);
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
