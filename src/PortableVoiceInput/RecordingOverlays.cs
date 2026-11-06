using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PortableVoiceInput.Core;

namespace PortableVoiceInput;

internal sealed class DimOverlay : Window
{
    public DimOverlay()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Black; Opacity = 0; ShowInTaskbar = false; ShowActivated = false;
        Focusable = false; IsHitTestVisible = false; Topmost = true;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        SourceInitialized += (_, _) => Native.SetOverlayStyles(this, true);
    }
    public void FadeIn() { Show(); BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.32, TimeSpan.FromMilliseconds(300))); }
    public void FadeOut()
    {
        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(300));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }
}

internal sealed class WaveformOverlay : Window
{
    private readonly WaveformDrawing _wave = new();
    private readonly TextBlock _caption = new() { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Func<double> _level;
    private DateTimeOffset _started;
    public event Action<double, double>? PositionSaved;

    public WaveformOverlay(AppSettings settings, Func<double> level)
    {
        _level = level;
        Width = 440; Height = 56;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        Left = double.IsFinite(settings.WaveformLeft) ? settings.WaveformLeft : SystemParameters.WorkArea.Right - Width - 20;
        Top = double.IsFinite(settings.WaveformTop) ? settings.WaveformTop : SystemParameters.WorkArea.Top + 18;
        SourceInitialized += (_, _) => Native.SetOverlayStyles(this, false);
        var panel = new Grid { Margin = new Thickness(5, 0, 5, 0) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _caption.Margin = new Thickness(16, 0, 13, 0);
        var dot = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(247, 100, 105)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        panel.Children.Add(_wave); Grid.SetColumn(_caption, 1); panel.Children.Add(_caption); Grid.SetColumn(dot, 2); panel.Children.Add(dot);
        Content = panel;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); PositionSaved?.Invoke(Left, Top); } catch (InvalidOperationException) { }
        };
        _timer.Tick += (_, _) =>
        {
            _wave.Push(_level());
            var elapsed = DateTimeOffset.Now - _started;
            _caption.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        };
        Closed += (_, _) => _timer.Stop();
    }
    public void Begin() { _started = DateTimeOffset.Now; Show(); _timer.Start(); }
    public void Finish() { _timer.Stop(); Close(); }
}

internal sealed class ProcessingOverlay : Window
{
    private readonly TextBlock _message = new() { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    public string Message { set => _message.Text = value; }
    public ProcessingOverlay(AppSettings settings, string message)
    {
        Width = 250; Height = 42; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        Left = double.IsFinite(settings.WaveformLeft) ? settings.WaveformLeft + 190 : SystemParameters.WorkArea.Right - Width - 20;
        Top = double.IsFinite(settings.WaveformTop) ? settings.WaveformTop : SystemParameters.WorkArea.Top + 18;
        SourceInitialized += (_, _) => Native.SetOverlayStyles(this, true);
        var ring = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Stroke = new SolidColorBrush(Color.FromRgb(173, 173, 255)), StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 1, 1 }, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform() };
        ring.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(ring); panel.Children.Add(_message); Message = message;
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 31, 35, 48)), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5, 12, 5), Child = panel };
    }
}

internal sealed class WaveformDrawing : FrameworkElement
{
    private const int Capacity = 105;
    private readonly Queue<double> _levels = new(Enumerable.Repeat(0d, Capacity));
    private readonly Pen _pen = new(new SolidColorBrush(Color.FromArgb(205, 225, 226, 232)), 2);
    public WaveformDrawing() { _pen.StartLineCap = PenLineCap.Round; _pen.EndLineCap = PenLineCap.Round; }
    public void Push(double level) { if (_levels.Count >= Capacity) _levels.Dequeue(); _levels.Enqueue(Math.Clamp(level, 0, 1)); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var x = 0d; var step = Math.Max(2.5, ActualWidth / Capacity); var center = ActualHeight / 2;
        foreach (var level in _levels)
        {
            var height = Math.Max(2, level * Math.Min(32, ActualHeight - 8));
            dc.DrawLine(_pen, new Point(x, center - height / 2), new Point(x, center + height / 2));
            x += step;
        }
    }
}
