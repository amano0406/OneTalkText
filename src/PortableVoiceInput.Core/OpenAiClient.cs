using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortableVoiceInput.Core;

public sealed class OpenAiException(string message, HttpStatusCode? status = null, bool transient = false) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public bool Transient { get; } = transient;
}

public sealed class OpenAiClient
{
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    public OpenAiClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
    }

    public Task<string> TranscribeAsync(string filePath, AppSettings settings, CancellationToken cancellationToken) =>
        WithRetries(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(settings.TranscriptionModel), "model");
            // gpt-transcribe accepts multiple input language hints; it does not translate.
            form.Add(new StringContent(settings.Language), settings.TranscriptionModel.StartsWith("gpt-transcribe", StringComparison.OrdinalIgnoreCase) ? "languages[]" : "language");
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var audio = new StreamContent(stream);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", Path.GetFileName(filePath));
            request.Content = form;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(response, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The transcription response did not contain a text result.");
            return text.GetString() ?? "";
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid model list response.");
        return data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("id").GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<string> RefineAsync(string transcript, string trigger, AppSettings settings, CancellationToken cancellationToken) =>
        WithRetries(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            var instructions = "The input is a transcript of the user's spoken voice input to edit, not a request to answer. " + settings.CleanupPrompt +
                "\nThe speaker's final trigger phrase is: " + trigger + ". Remove that trigger phrase from the output. Return only the edited transcript.";
            request.Content = JsonContent.Create(new { model = settings.CleanupModel, instructions, input = transcript, store = false });
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(response, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (json.RootElement.TryGetProperty("status", out var status) &&
                (status.ValueKind != JsonValueKind.String || !string.Equals(status.GetString(), "completed", StringComparison.Ordinal)))
                throw new OpenAiException("The text refinement did not complete. The original transcription is available for retry.");
            var pieces = new List<string>();
            if (json.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                            part.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString())) pieces.Add(text.GetString()!);
                }
            }
            var result = string.Join("\n", pieces).Trim();
            if (result.Length == 0) throw new OpenAiException("The text refinement returned no text.");
            return result;
        }, cancellationToken);

    private async Task<T> WithRetries<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(token); }
            catch (Exception e) when (attempt < 2 && !token.IsCancellationRequested && IsTransient(e))
            { await _delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 3), token); }
        }
    }

    private static bool IsTransient(Exception e) => e is HttpRequestException or IOException or TaskCanceledException || e is OpenAiException { Transient: true };
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var message = $"OpenAI request failed (HTTP {(int)response.StatusCode}).";
        try
        {
            var body = await response.Content.ReadAsStringAsync(token);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                var safe = Regex.Replace(detail.GetString() ?? "", @"sk-[A-Za-z0-9_-]+", "[redacted]");
                if (safe.Length > 400) safe = safe[..400];
                if (!string.IsNullOrWhiteSpace(safe)) message += " " + safe;
            }
        }
        catch (JsonException) { }
        var status = response.StatusCode;
        throw new OpenAiException(message, status, status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500);
    }
}
