namespace PortableVoiceInput.Core;

public sealed record NoiseCalibration(double NoiseDb, double ThresholdDb, bool IsReliable);

/// <summary>Estimates the initial noise floor without changing captured samples.</summary>
public sealed class NoiseCalibrationAccumulator
{
    public const int SampleRate = 16000;
    public const double DurationSeconds = 0.5;
    private const int FrameSamples = 320; // 20 ms; the median resists brief clicks.
    private const int RequiredSamples = SampleRate / 2;
    private readonly List<double> _frames = [];
    private int _frameSamples, _totalSamples;
    private double _energy;
    private NoiseCalibration? _result;

    public NoiseCalibration? Add(ReadOnlySpan<byte> pcm16, double marginDb)
    {
        if (_result is not null) return _result;
        for (var i = 0; i + 1 < pcm16.Length && _totalSamples < RequiredSamples; i += 2)
        {
            var sample = (short)(pcm16[i] | pcm16[i + 1] << 8) / 32768.0;
            _energy += sample * sample;
            _frameSamples++; _totalSamples++;
            if (_frameSamples == FrameSamples)
            {
                var rms = Math.Sqrt(_energy / _frameSamples);
                _frames.Add(Math.Clamp(rms <= 1e-12 ? -90 : 20 * Math.Log10(rms), -90, 0));
                _frameSamples = 0; _energy = 0;
            }
        }
        if (_totalSamples < RequiredSamples) return null;
        _frames.Sort();
        var noise = _frames[_frames.Count / 2];
        var spread = _frames[3 * _frames.Count / 4] - _frames[_frames.Count / 4];
        var margin = double.IsFinite(marginDb) ? Math.Clamp(marginDb, 0, 30) : 6;
        // A loud or highly variable sample may contain speech or a device glitch.
        // In that case the processor preserves the whole recording on upload.
        var reliable = noise <= -20 && spread <= 18;
        _result = new NoiseCalibration(noise, Math.Clamp(noise + margin, -90, -5), reliable);
        return _result;
    }
}

public static class WaveformLevel
{
    /// <summary>Scale only the display; no samples in the recording are modified.</summary>
    public static double FromDecibels(double inputDb, double? noiseDb)
    {
        if (!double.IsFinite(inputDb)) return 0;
        if (noiseDb is not double floor || !double.IsFinite(floor))
            return Math.Clamp((inputDb + 65) / 60, 0, 1);
        var displayFloor = Math.Max(-85, floor + 2);
        return Math.Clamp((inputDb - displayFloor) / 32, 0, 1);
    }
}
