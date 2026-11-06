using System.Net;
using System.Text;
using PortableVoiceInput.Core;

namespace PortableVoiceInput.Tests;

public sealed class ModelSelectionTests
{
    [Fact]
    public async Task ModelListReadsUniqueIdsWithoutChangingTheKey()
    {
        using var http = new HttpClient(new ReplyHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return "{\"data\":[{\"id\":\"gpt-transcribe\"},{\"id\":\"gpt-4.1-mini\"},{\"id\":\"gpt-transcribe\"}]}";
        }));
        var ids = await new OpenAiClient(http).ListModelsAsync("sk-test", CancellationToken.None);
        Assert.Equal(new[] { "gpt-4.1-mini", "gpt-transcribe" }, ids);
    }

    [Fact]
    public async Task StringErrorBodyDoesNotCrashTheApplication()
    {
        using var http = new HttpClient(new ErrorHandler());
        var ex = await Assert.ThrowsAsync<OpenAiException>(() => new OpenAiClient(http).ListModelsAsync("sk-test", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
    }

    [Fact]
    public async Task RefinementIncludesVoiceContextAndCustomInstructions()
    {
        string? instructions = null;
        using var http = new HttpClient(new ReplyHandler(request =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            instructions = json.RootElement.GetProperty("instructions").GetString();
            return "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"edited\"}]}]}";
        }));
        var settings = new AppSettings { ApiKey = "sk-test", CleanupPrompt = "Keep all names unchanged." };
        var text = await new OpenAiClient(http).RefineAsync("hello, tidy this", "tidy this", settings, CancellationToken.None);
        Assert.Equal("edited", text);
        Assert.Contains("voice", instructions!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(settings.CleanupPrompt, instructions!);
    }

    [Theory]
    [InlineData("gpt-transcribe", "languages[]")]
    [InlineData("whisper-1", "language")]
    [InlineData("gpt-4o-mini-transcribe", "language")]
    public async Task TranscriptionUsesTheSelectedModelsLanguageParameter(string model, string expected)
    {
        string[] captured = [];
        using var http = new HttpClient(new ReplyHandler(request =>
        {
            captured = ((MultipartFormDataContent)request.Content!).Select(part => part.Headers.ContentDisposition!.Name!.Trim('"')).ToArray();
            return "{\"text\":\"sample\"}";
        }));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2]);
            await new OpenAiClient(http).TranscribeAsync(path, new AppSettings { ApiKey = "sk-test", Language = "ja", TranscriptionModel = model }, CancellationToken.None);
            Assert.Contains(expected, captured, StringComparer.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request), Encoding.UTF8, "application/json") });
    }
    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"not authorized\"}", Encoding.UTF8, "application/json") });
    }
}
