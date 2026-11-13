using System.Text;
using Serilog;
using Serilog.Core;

namespace PortableVoiceInput.Core;

/// <summary>Writes low-volume diagnostic errors beside the portable app.</summary>
public sealed class DiagnosticLog : IDisposable
{
    private readonly Logger _logger;

    public DiagnosticLog(string appFolder)
    {
        var folder = Path.Combine(appFolder, "logs");
        Directory.CreateDirectory(folder);
        _logger = new LoggerConfiguration()
            .MinimumLevel.Error()
            .WriteTo.File(Path.Combine(folder, "onetalktext-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger();
    }

    public void Error(string operation, Exception? exception = null)
    {
        try
        {
            // Exception messages can contain API keys, server responses or user
            // text. Types, error codes and stack traces are enough for diagnosis.
            if (exception is null) _logger.Error("{Operation}", operation);
            else _logger.Error("{Operation}{NewLine}{Details}", operation, Environment.NewLine, Describe(exception));
        }
        catch { /* Logging must never terminate the app or discard a recording. */ }
    }

    private static string Describe(Exception exception)
    {
        var result = new StringBuilder();
        Append(exception, 0);
        return result.ToString().TrimEnd();

        void Append(Exception error, int depth)
        {
            if (depth > 8) return;
            if (result.Length > 0) result.AppendLine("Caused by:");
            result.Append(error.GetType().FullName).Append(" (HRESULT 0x")
                .Append(unchecked((uint)error.HResult).ToString("X8")).Append(')');
            if (error is OpenAiException { Status: not null } api)
                result.Append(" HTTP ").Append((int)api.Status.Value);
            else if (error is HttpRequestException http)
            {
                result.Append(" NetworkError=").Append(http.HttpRequestError);
                if (http.StatusCode is not null) result.Append(" HTTP ").Append((int)http.StatusCode.Value);
            }
            result.AppendLine();
            if (!string.IsNullOrWhiteSpace(error.StackTrace)) result.AppendLine(error.StackTrace);
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) Append(inner, depth + 1);
            }
            else if (error.InnerException is not null) Append(error.InnerException, depth + 1);
        }
    }

    public void Dispose() => _logger.Dispose();
}
