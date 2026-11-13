using System.Globalization;
using System.Text;

namespace PortableVoiceInput.Core;

public sealed record ProcessResult(RecordingMetadata Metadata, bool HasOutput);

public static class TriggerDetector
{
    public static string? FindAtEnd(string text, string configuredTriggers)
    {
        var input = TrimEnding(text.Normalize(NormalizationForm.FormKC));
        foreach (var raw in configuredTriggers.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trigger = TrimEnding(raw.Normalize(NormalizationForm.FormKC));
            if (trigger.Length > 0 && input.EndsWith(trigger, StringComparison.OrdinalIgnoreCase)) return raw;
        }
        return null;
    }

    private static string TrimEnding(string value)
    {
        var end = value.Length;
        while (end > 0)
        {
            var c = value[end - 1];
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)) break;
            end--;
        }
        return value[..end];
    }
}

public sealed class RecordingProcessor(OpenAiClient api, string workingFolder)
{
    public async Task<ProcessResult> ProcessAsync(string path, AppSettings settings, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var (_, metadata) = WaveMetadata.Read(path);
        metadata.Status = RecordingStatus.Processing;
        metadata.Error = "";
        WaveMetadata.Write(path, metadata);
        var generatedPaths = new List<string>();
        string? jobFolder = null;
        try
        {
            progress?.Invoke("analyzing");
            var analysis = await Task.Run(() => SilenceProcessor.Analyze(path, settings, cancellationToken), cancellationToken);
            metadata.OriginalSeconds = analysis.Wave.DurationSeconds;
            metadata.SentSeconds = analysis.SentSeconds;
            metadata.Language = settings.Language;
            if (analysis.IsDigitalSilence)
            {
                // Digital silence is not an API failure. A quiet non-zero recording
                // may still contain speech and must be sent for transcription.
                metadata.Status = RecordingStatus.NoSpeech;
                metadata.Error = "";
                WaveMetadata.Write(path, metadata);
                return new(metadata, false);
            }

            if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new OpenAiException("OpenAI API key is not set.");

            var signature = SilenceProcessor.Signature(settings, analysis.Wave);
            if (metadata.ProcessingSignature != signature)
            {
                metadata.ProcessingSignature = signature;
                metadata.CompletedChunks.Clear();
                // Keep any earlier result accessible until the new attempt succeeds.
                WaveMetadata.Write(path, metadata);
            }
            jobFolder = Path.Combine(workingFolder, Guid.NewGuid().ToString("N"));
            progress?.Invoke("preparing");
            var chunks = await Task.Run(() => SilenceProcessor.CreateChunks(path, analysis, jobFolder, cancellationToken: cancellationToken), cancellationToken);
            generatedPaths.AddRange(chunks);
            if (metadata.CompletedChunks.Count > chunks.Count) throw new InvalidDataException("Saved transcription progress does not match the recording.");
            for (var i = metadata.CompletedChunks.Count; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke($"transcribing:{i + 1}:{chunks.Count}");
                var text = await api.TranscribeAsync(chunks[i], settings, cancellationToken);
                metadata.CompletedChunks.Add(text.Trim());
                metadata.Transcript = JoinChunks(metadata.CompletedChunks, settings.Language);
                // Persist each successful piece so a retry does not pay for it again.
                WaveMetadata.Write(path, metadata);
            }
            metadata.Transcript = JoinChunks(metadata.CompletedChunks, settings.Language);
            if (string.IsNullOrWhiteSpace(metadata.Transcript))
            {
                metadata.Status = RecordingStatus.NoSpeech;
                metadata.FinalText = "";
                WaveMetadata.Write(path, metadata);
                return new(metadata, false);
            }
            var trigger = TriggerDetector.FindAtEnd(metadata.Transcript, settings.TriggerWords);
            if (trigger is not null)
            {
                progress?.Invoke("refining");
                metadata.FinalText = await api.RefineAsync(metadata.Transcript, trigger, settings, cancellationToken);
                metadata.Refined = true;
            }
            else { metadata.FinalText = metadata.Transcript; metadata.Refined = false; }
            metadata.Status = RecordingStatus.Complete;
            metadata.Error = "";
            WaveMetadata.Write(path, metadata);
            return new(metadata, true);
        }
        catch (Exception ex)
        {
            metadata.Status = cancellationToken.IsCancellationRequested ? RecordingStatus.Interrupted : RecordingStatus.Failed;
            metadata.Error = SafeError(ex, cancellationToken.IsCancellationRequested);
            try { WaveMetadata.Write(path, metadata); } catch (IOException) { /* the original audio remains on disk */ }
            throw;
        }
        finally
        {
            // Delete only the exact temporary chunk files this job created, never the source recording.
            foreach (var chunk in generatedPaths) { try { File.Delete(chunk); } catch (IOException) { } }
            if (jobFolder is not null) { try { Directory.Delete(jobFolder, false); } catch (IOException) { } }
        }
    }

    private static string JoinChunks(IEnumerable<string> chunks, string language) =>
        string.Join(language == "ja" ? "" : " ", chunks.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

    private static string SafeError(Exception ex, bool canceled)
    {
        if (canceled) return "Processing was interrupted. The recording is available for retry.";
        if (ex is OpenAiException) return ex.Message;
        if (ex is HttpRequestException or TaskCanceledException) return "A network request failed. The recording is available for retry.";
        if (ex is IOException or UnauthorizedAccessException) return "The recording could not be read or saved. Check the folder and disk space.";
        return "Processing failed. The recording is available for retry.";
    }
}
