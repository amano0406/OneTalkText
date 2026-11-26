using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PortableVoiceInput;
using PortableVoiceInput.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new IntegrationApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Settings.PlaySounds = false;
        try
        {
            if (args.Length == 1 && args[0] == "--interactive") return ShowInteractive(app);
            CancelWithoutSending(app, normalStopAlreadyRequested: false);
            CancelWithoutSending(app, normalStopAlreadyRequested: true);
            CheckWaveform();
            CheckPreparation();
            CheckStatus();
            Console.WriteLine("Overlay bounds, cancel controls, and cancellation without transcription verified.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CancelWithoutSending(App app, bool normalStopAlreadyRequested)
    {
        var directory = Path.Combine(Path.GetTempPath(), "OneTalkText-overlay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.wav");
        var audio = Enumerable.Repeat(new byte[] { 0x12, 0x34 }, 1600).SelectMany(x => x).ToArray();
        using (var writer = new DurableWaveWriter(path)) writer.Write(audio);
        Set(app, "_recordingPending", true);
        Set(app, "_stopping", normalStopAlreadyRequested);
        Invoke(app, "CancelRecording");
        Invoke(app, "RecordingFinished", new RecordingFinished(path, false, ""));
        var (info, metadata) = WaveMetadata.Read(path);
        Assert(metadata.Status == RecordingStatus.Canceled, "Canceled recording was not marked.");
        Assert(!app.IsProcessing && string.IsNullOrEmpty(metadata.Transcript), "Cancellation started transcription.");
        using var file = File.OpenRead(path);
        file.Position = info.DataOffset;
        var actual = new byte[audio.Length];
        file.ReadExactly(actual);
        Assert(actual.SequenceEqual(audio), "Canceling changed the recorded audio.");
    }

    private static void CheckWaveform()
    {
        var settings = OffscreenSettings();
        var window = NewWindow("WaveformOverlay", settings, (Func<double>)(() => 0.4));
        var canceled = false;
        window.GetType().GetEvent("CancelRequested")!.AddEventHandler(window, (Action)(() => canceled = true));
        window.Show(); Pump();
        AssertWithinWorkArea(window);
        FindButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(canceled, "The waveform cancel button did not raise its action.");
        window.Close(); Pump();
    }

    private static void CheckPreparation()
    {
        var canceled = false;
        var window = NewWindow("ProcessingOverlay", OffscreenSettings(), "準備中…", (Action)(() => canceled = true));
        window.Show(); Pump();
        AssertWithinWorkArea(window);
        FindButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(canceled, "The preparation cancel button did not raise its action.");
        window.Close(); Pump();
    }

    private static void CheckStatus()
    {
        var window = NewWindow("ProcessingOverlay", OffscreenSettings(), "更新を確認中…", null);
        window.Show(); Pump();
        AssertWithinWorkArea(window);
        window.Close(); Pump();
    }

    private static int ShowInteractive(App app)
    {
        var settings = new AppSettings { Language = "ja", PlaySounds = false };
        var window = NewWindow("WaveformOverlay", settings, (Func<double>)(() => 0.4));
        window.Title = "OneTalk Text Overlay Test";
        // Make this isolated test window discoverable to Windows UI automation while
        // retaining the production overlay's no-activation behavior.
        window.ShowInTaskbar = true;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var flags = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, new IntPtr((flags & ~0x80L) | 0x40000L));
        };
        window.GetType().GetEvent("CancelRequested")!.AddEventHandler(window, (Action)(() =>
        {
            Console.WriteLine("Interactive cancel clicked.");
            app.Shutdown();
        }));
        window.GetType().GetMethod("Begin")!.Invoke(window, null);
        app.Run();
        return 0;
    }

    private static AppSettings OffscreenSettings() => new() { Language = "ja", WaveformLeft = 100000, WaveformTop = -100000 };
    private static Window NewWindow(string name, params object?[] arguments)
    {
        var type = typeof(App).Assembly.GetType("PortableVoiceInput." + name, throwOnError: true)!;
        return (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, arguments, null)!;
    }
    private static void Set(object target, string name, object value) => typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object[] args) => typeof(App).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static Button FindButton(DependencyObject root)
    {
        if (root is Button button) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return FindButton(VisualTreeHelper.GetChild(root, i)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("No cancel button was found.");
    }
    private static void AssertWithinWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Assert(GetWindowRect(handle, out var actual), "Could not read overlay bounds.");
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Assert(monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info), "Could not read monitor bounds.");
        Assert(actual.Left >= info.Work.Left && actual.Top >= info.Work.Top && actual.Right <= info.Work.Right && actual.Bottom <= info.Work.Bottom,
            $"Overlay outside work area: ({actual.Left},{actual.Top},{actual.Right},{actual.Bottom}) vs ({info.Work.Left},{info.Work.Top},{info.Work.Right},{info.Work.Bottom}).");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
}

internal sealed class IntegrationApp : App
{
    // Exercise overlay and cancellation behavior without starting microphones,
    // networking, the notification icon, or the real app's startup sequence.
    protected override void OnStartup(StartupEventArgs e) { }
}
