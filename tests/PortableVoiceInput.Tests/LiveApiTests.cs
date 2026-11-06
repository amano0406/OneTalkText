using PortableVoiceInput.Core;
using Xunit.Abstractions;

namespace PortableVoiceInput.Tests;

public sealed class LiveApiTests(ITestOutputHelper output)
{
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
