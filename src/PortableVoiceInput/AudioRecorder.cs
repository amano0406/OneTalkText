using NAudio.Wave;
using PortableVoiceInput.Core;

namespace PortableVoiceInput;

public sealed record MicrophoneOption(string Name, int DeviceNumber)
{
    public override string ToString() => Name;
}

public sealed record RecordingFinished(string Path, bool Interrupted, string Error, Exception? Exception = null);

public sealed class AudioRecorder : IDisposable
{
    private readonly object _gate = new();
    private WaveInEvent? _capture;
    private DurableWaveWriter? _writer;
    private string _partialPath = "";
    private string _language = "en";
    private DateTimeOffset _started;
    private DateTime _lastCheckpoint;
    private Exception? _writeError;
    private bool _requestedStop;
    private bool _captureEnding;
    private double _level;
    private double _noiseMarginDb;
    private NoiseCalibrationAccumulator? _calibrator;
    private NoiseCalibration? _calibration;
    private TaskCompletionSource<NoiseCalibration?> _preparation = NewPreparation();
    public bool IsRecording { get; private set; }
    public bool IsEnding => Volatile.Read(ref _captureEnding);
    public double Level => Volatile.Read(ref _level);
    public Task<NoiseCalibration?> Preparation => _preparation.Task;
    public event Action<RecordingFinished>? Finished;

    private static TaskCompletionSource<NoiseCalibration?> NewPreparation() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static List<MicrophoneOption> GetMicrophones(string defaultLabel)
    {
        var result = new List<MicrophoneOption> { new(defaultLabel, -1) };
        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            try { result.Add(new(WaveIn.GetCapabilities(i).ProductName, i)); }
            catch (MmException) { }
        }
        return result;
    }

    internal static int ResolveDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        for (var i = 0; i < WaveIn.DeviceCount; i++)
            if (string.Equals(WaveIn.GetCapabilities(i).ProductName, name, StringComparison.Ordinal)) return i;
        throw new InvalidOperationException("The selected microphone is not available.");
    }

    public void Start(string folder, string microphoneName, string language, double noiseMarginDb)
    {
        if (IsRecording) throw new InvalidOperationException("Recording is already in progress.");
        var device = ResolveDevice(microphoneName);
        Directory.CreateDirectory(folder);
        _started = DateTimeOffset.Now;
        _partialPath = Path.Combine(folder, $"{_started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.recording.wav");
        _language = language;
        _writeError = null;
        _requestedStop = false;
        _captureEnding = false;
        _noiseMarginDb = noiseMarginDb;
        _calibration = null;
        _calibrator = new NoiseCalibrationAccumulator();
        _preparation = NewPreparation();
        _lastCheckpoint = DateTime.UtcNow;
        _writer = new DurableWaveWriter(_partialPath);
        _capture = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50, NumberOfBuffers = 3 };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        IsRecording = true;
        try { _capture.StartRecording(); }
        catch
        {
            IsRecording = false;
            _capture.Dispose(); _capture = null;
            _writer.Dispose(); _writer = null;
            // Retain the file if it contains any audio, even if starting failed.
            if (new FileInfo(_partialPath).Length == 44) File.Delete(_partialPath);
            throw;
        }
    }

    public void Stop()
    {
        if (!IsRecording || _capture is null) return;
        _requestedStop = true;
        _capture.StopRecording();
    }

    public void AbandonPreparation()
    {
        lock (_gate)
        {
            if (_preparation.Task.IsCompleted) return;
            _calibrator = null;
            _preparation.TrySetResult(null);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        try
        {
            lock (_gate)
            {
                _writer?.Write(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (_calibrator is not null)
                {
                    var result = _calibrator.Add(e.Buffer.AsSpan(0, e.BytesRecorded), _noiseMarginDb);
                    if (result is not null)
                    {
                        Volatile.Write(ref _calibration, result);
                        _calibrator = null;
                        _preparation.TrySetResult(result);
                    }
                }
                if (DateTime.UtcNow - _lastCheckpoint >= TimeSpan.FromSeconds(1))
                { _writer?.Checkpoint(); _lastCheckpoint = DateTime.UtcNow; }
            }
            double sum = 0; var count = e.BytesRecorded / 2;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            { var v = (short)(e.Buffer[i] | e.Buffer[i + 1] << 8) / 32768.0; sum += v * v; }
            var rms = count == 0 ? 0 : Math.Sqrt(sum / count);
            var db = rms <= 1e-9 ? -90 : 20 * Math.Log10(rms);
            var calibration = Volatile.Read(ref _calibration);
            Volatile.Write(ref _level, WaveformLevel.FromDecibels(db, calibration is { IsReliable: true } ? calibration.NoiseDb : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _writeError = ex;
            try { _capture?.StopRecording(); } catch (MmException) { }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Volatile.Write(ref _captureEnding, true);
        var interrupted = !_requestedStop || _writeError is not null || e.Exception is not null;
        Exception? failure = _writeError ?? e.Exception;
        var error = interrupted ? "Recording stopped unexpectedly. The saved audio can be retried." : "";
        var finalPath = _partialPath;
        NoiseCalibration? calibration = null;
        double protectedStart = 0;
        try
        {
            lock (_gate)
            {
                calibration = _calibration;
                protectedStart = Math.Min(NoiseCalibrationAccumulator.DurationSeconds, (_writer?.AudioBytes ?? 0) / (double)(NoiseCalibrationAccumulator.SampleRate * 2));
                _calibrator = null;
                _preparation.TrySetResult(calibration);
                _writer?.Dispose(); _writer = null;
            }
            finalPath = _partialPath.Replace(".recording.wav", ".wav", StringComparison.OrdinalIgnoreCase);
            File.Move(_partialPath, finalPath);
            var info = WaveMetadata.Read(finalPath).Info;
            WaveMetadata.Write(finalPath, new RecordingMetadata
            {
                CreatedAt = _started, Language = _language,
                Status = interrupted ? RecordingStatus.Interrupted : RecordingStatus.Pending,
                Error = error, OriginalSeconds = info.DurationSeconds,
                CalibrationReliable = calibration is { IsReliable: true },
                CalibratedNoiseDb = calibration?.NoiseDb,
                CalibratedThresholdDb = calibration?.ThresholdDb,
                ProtectedStartSeconds = protectedStart
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            interrupted = true;
            failure = ex;
            if (File.Exists(_partialPath)) finalPath = _partialPath;
            error = "The recording could not be finalized. Its file was kept for recovery.";
        }
        finally
        {
            _preparation.TrySetResult(null);
            IsRecording = false;
            Volatile.Write(ref _level, 0);
            _capture?.Dispose(); _capture = null;
        }
        Finished?.Invoke(new RecordingFinished(finalPath, interrupted, error, failure));
    }

    public static async Task<double> MeasureBackgroundAsync(string microphoneName, CancellationToken cancellationToken)
    {
        var device = ResolveDevice(microphoneName);
        using var capture = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object(); double energy = 0; long count = 0;
        capture.DataAvailable += (_, e) =>
        {
            lock (gate) for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            { var sample = (short)(e.Buffer[i] | e.Buffer[i + 1] << 8) / 32768.0; energy += sample * sample; count++; }
        };
        capture.RecordingStopped += (_, e) => done.TrySetResult(e.Exception);
        capture.StartRecording();
        try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken); }
        finally { capture.StopRecording(); }
        var error = await done.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (error is not null) throw new InvalidOperationException("Could not measure microphone input.", error);
        lock (gate) { var rms = count == 0 ? 0 : Math.Sqrt(energy / count); return rms <= 1e-9 ? -90 : 20 * Math.Log10(rms); }
    }

    public void Dispose()
    {
        if (IsRecording) { _requestedStop = true; try { _capture?.StopRecording(); } catch (MmException) { } }
    }
}
