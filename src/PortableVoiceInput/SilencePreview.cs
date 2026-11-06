using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NAudio.Wave;
using PortableVoiceInput.Core;

namespace PortableVoiceInput;

internal sealed class MicrophonePreview : IDisposable
{
    private readonly WaveInEvent _capture;
    private double _db = -90;
    public double Decibels => Volatile.Read(ref _db);
    public MicrophonePreview(string name)
    {
        _capture = new WaveInEvent { DeviceNumber = AudioRecorder.ResolveDevice(name), WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50, NumberOfBuffers = 3 };
        _capture.DataAvailable += (_, e) =>
        {
            double sum = 0; var count = e.BytesRecorded / 2;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            { var v = (short)(e.Buffer[i] | e.Buffer[i + 1] << 8) / 32768.0; sum += v * v; }
            var rms = count == 0 ? 0 : Math.Sqrt(sum / count);
            Volatile.Write(ref _db, rms <= 1e-9 ? -90 : Math.Clamp(20 * Math.Log10(rms), -90, 0));
        };
        try { _capture.StartRecording(); } catch { _capture.Dispose(); throw; }
    }
    public void Dispose() { _capture.StopRecording(); _capture.Dispose(); }
}

/// <summary>A local, rolling approximation of silence trimming; no samples are saved.</summary>
public sealed class SilencePreviewGraph : FrameworkElement
{
    private const double Interval = 0.05;
    private const int Capacity = 240;
    private readonly List<double> _levels = [];
    private readonly Pen _thresholdPen = new(new SolidColorBrush(Color.FromRgb(222, 190, 100)), 1);
    private readonly Pen _voicePen = new(new SolidColorBrush(Color.FromRgb(98, 211, 151)), 2);
    private readonly Pen _keepPen = new(new SolidColorBrush(Color.FromRgb(133, 145, 165)), 2);
    private readonly Pen _cutPen = new(new SolidColorBrush(Color.FromRgb(229, 111, 116)), 2);
    private double _threshold = -45, _minimum = 2, _gap = 1;
    private bool _enabled = true;
    public void Reset() { _levels.Clear(); InvalidateVisual(); }
    public void Push(double db, double threshold, double minimum, double gap, bool enabled)
    {
        if (_levels.Count >= Capacity) _levels.RemoveAt(0);
        _levels.Add(Math.Clamp(db, -90, 0)); _threshold = threshold; _minimum = minimum; _gap = gap; _enabled = enabled;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var thresholdY = ActualHeight - (Math.Clamp(_threshold, -90, 0) + 90) / 90 * ActualHeight;
        dc.DrawLine(_thresholdPen, new Point(0, thresholdY), new Point(ActualWidth, thresholdY));
        var cut = SilenceProcessor.PreviewCuts(_levels, Interval, _threshold, _minimum, _gap, _enabled);
        var step = ActualWidth / Capacity; var startX = ActualWidth - _levels.Count * step;
        for (var i = 0; i < _levels.Count; i++)
        {
            var height = Math.Max(2, (_levels[i] + 90) / 90 * (ActualHeight - 2));
            dc.DrawLine(_levels[i] >= _threshold ? _voicePen : cut[i] ? _cutPen : _keepPen,
                new Point(startX + i * step, ActualHeight), new Point(startX + i * step, ActualHeight - height));
        }
    }
}
