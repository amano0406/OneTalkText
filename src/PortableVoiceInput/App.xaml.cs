using System.Media;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using PortableVoiceInput.Core;
using Forms = System.Windows.Forms;

namespace PortableVoiceInput;

public partial class App : System.Windows.Application
{
    private ConfigStore _config = null!;
    private GlobalHotkey _hotkey = null!;
    private readonly AudioRecorder _recorder = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private DimOverlay? _dim;
    private WaveformOverlay? _wave;
    private ProcessingOverlay? _processingOverlay;
    private CancellationTokenSource? _processingCancellation;
    private Mutex? _instance;
    private EventWaitHandle? _openSettingsSignal;
    private RegisteredWaitHandle? _openSettingsRegistration;
    private bool _stopping, _quitRequested;
    public AppSettings Settings { get; private set; } = new();
    public bool IsRecording => _recorder.IsRecording;
    public bool IsProcessing { get; private set; }
    public string AppFolder { get; private set; } = "";
    public string RecordingsFolder => Path.Combine(AppFolder, "recordings");
    private string WorkingFolder => Path.Combine(AppFolder, "working");
    private string T(string key) => Loc.Text(key, Settings.Language);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppFolder = AppContext.BaseDirectory;
        var instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppFolder.ToUpperInvariant())))[..16];
        _instance = new Mutex(true, "Local\\PortableVoiceInput-" + instanceId, out var isNew);
        if (!isNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting("Local\\PortableVoiceInput-Settings-" + instanceId);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                System.Windows.MessageBox.Show("OneTalk Text is already starting or running in the notification area.");
            }
            Shutdown(); return;
        }
        try
        {
            _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PortableVoiceInput-Settings-" + instanceId);
            _openSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(_openSettingsSignal,
                (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, false);
            Directory.CreateDirectory(RecordingsFolder);
            Directory.CreateDirectory(WorkingFolder);
            _config = new ConfigStore(AppFolder);
            Settings = _config.Load();
            Loc.Apply(Settings.Language);
            var recovered = RecoverInterruptedRecordings();
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += ToggleRecording;
            var registered = _hotkey.Register(Settings.HotkeyModifiers, Settings.HotkeyKey);
            _recorder.Finished += result => Dispatcher.BeginInvoke(() => RecordingFinished(result));
            CreateTray();
            _window = new MainWindow(this);
            MainWindow = _window;
            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase) || !registered) ShowSettings();
            if (!registered) Notify(T("HotkeyUnavailable"), Forms.ToolTipIcon.Warning);
            else if (recovered > 0) Notify(T("RecoverMessage"), Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Windows.MessageBox.Show("OneTalk Text could not access its app folder. " + ex.Message);
            Shutdown();
        }
    }

    private int RecoverInterruptedRecordings()
    {
        var count = 0;
        foreach (var partial in Directory.EnumerateFiles(RecordingsFolder, "*.recording.wav"))
        {
            try
            {
                DurableWaveWriter.Recover(partial);
                var destination = partial[..^".recording.wav".Length] + ".wav";
                if (File.Exists(destination)) destination = partial[..^".recording.wav".Length] + "-recovered.wav";
                File.Move(partial, destination);
                var info = WaveMetadata.Read(destination).Info;
                WaveMetadata.Write(destination, new RecordingMetadata { Status = RecordingStatus.Interrupted, OriginalSeconds = info.DurationSeconds, Error = "The recording was interrupted and can be retried." });
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        foreach (var path in Directory.EnumerateFiles(RecordingsFolder, "*.wav").Where(p => !p.EndsWith(".recording.wav", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var (_, meta) = WaveMetadata.Read(path);
                if (meta.Status != RecordingStatus.Processing) continue;
                meta.Status = RecordingStatus.Interrupted;
                meta.Error = "Processing was interrupted. The original audio and completed segments were kept.";
                WaveMetadata.Write(path, meta); count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return count;
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream), Text = "OneTalk Text", Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        UpdateTrayMenu();
    }
    private void UpdateTrayMenu()
    {
        if (_tray is null) return;
        var old = _tray.ContextMenuStrip;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(T("SettingsMenu"), null, (_, _) => ShowSettings());
        menu.Items.Add(T(IsRecording ? "StopNow" : "RecordNow"), null, (_, _) => ToggleRecording());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("QuitMenu"), null, (_, _) => RequestQuit());
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void ShowSettings()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.RefreshRecordings(force: true);
    }

    public bool ApplySettings(AppSettings next)
    {
        var old = Settings;
        if ((!_hotkey.IsRegistered || old.HotkeyModifiers != next.HotkeyModifiers || old.HotkeyKey != next.HotkeyKey) && !_hotkey.Register(next.HotkeyModifiers, next.HotkeyKey))
        {
            System.Windows.MessageBox.Show(Loc.Text("HotkeyUnavailable", next.Language), Loc.Text("ErrorTitle", next.Language), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try { _config.Save(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _hotkey.Register(old.HotkeyModifiers, old.HotkeyKey);
            System.Windows.MessageBox.Show(Loc.Text("SaveFailed", next.Language), Loc.Text("ErrorTitle", next.Language), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        Settings = next; Loc.Apply(Settings.Language); UpdateTrayMenu(); return true;
    }

    public void SuspendShortcut() => _hotkey.Suspend();
    public void ResumeShortcut()
    {
        if (!_hotkey.Register(Settings.HotkeyModifiers, Settings.HotkeyKey))
            Notify(T("HotkeyUnavailable"), Forms.ToolTipIcon.Warning);
    }

    public void ToggleRecording()
    {
        if (_quitRequested || _stopping) return;
        if (IsRecording)
        {
            _stopping = true;
            try { _recorder.Stop(); }
            catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException) { _stopping = false; Notify(T("RecordingError"), Forms.ToolTipIcon.Error); }
            return;
        }
        if (IsProcessing) { Notify(T("BusyMessage"), Forms.ToolTipIcon.Info); return; }
        try
        {
            _window?.SuspendAudioPreview();
            if (Settings.PlaySounds) CuePlayer.Play(true);
            _recorder.Start(RecordingsFolder, Settings.MicrophoneName, Settings.Language);
            if (Settings.DimScreen) { _dim = new DimOverlay(); _dim.FadeIn(); }
            _wave = new WaveformOverlay(Settings, () => _recorder.Level);
            _wave.PositionSaved += SaveWaveformPosition;
            _wave.Begin();
            _window?.SetState(true, false, null); _window?.RefreshRecordings(force: true); UpdateTrayMenu();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NAudio.MmException)
        {
            if (_recorder.IsRecording)
            {
                _stopping = true;
                try { _recorder.Stop(); } catch (NAudio.MmException) { _stopping = false; }
            }
            Notify(ex is InvalidOperationException ? T("MissingMicrophone") : T("RecordingError"), Forms.ToolTipIcon.Error);
            _window?.ResumeAudioPreview();
        }
    }

    private void SaveWaveformPosition(double left, double top)
    {
        Settings.WaveformLeft = left; Settings.WaveformTop = top;
        try { _config.Save(Settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify(T("SaveFailed"), Forms.ToolTipIcon.Warning); }
    }

    private void RecordingFinished(RecordingFinished result)
    {
        _stopping = false;
        _wave?.Finish(); _wave = null;
        _dim?.FadeOut(); _dim = null;
        _window?.ResumeAudioPreview();
        if (Settings.PlaySounds) CuePlayer.Play(false);
        UpdateTrayMenu(); _window?.SetState(false, false, null); _window?.RefreshRecordings(force: true);
        if (_quitRequested) { FinishQuit(); return; }
        if (result.Interrupted) { Notify(T("RecordingError"), Forms.ToolTipIcon.Error); return; }
        _ = ProcessAndDeliverAsync(result.Path);
    }

    public void Retry(string path)
    {
        if (IsRecording || IsProcessing || _stopping) { Notify(T("BusyMessage"), Forms.ToolTipIcon.Info); return; }
        if (!File.Exists(path)) { _window?.RefreshRecordings(force: true); return; }
        _ = ProcessAndDeliverAsync(path);
    }

    private async Task ProcessAndDeliverAsync(string path)
    {
        IsProcessing = true; _processingCancellation = new CancellationTokenSource();
        _window?.SetState(false, true, T("RetryStatus"));
        _processingOverlay = new ProcessingOverlay(Settings, T("RetryStatus"));
        _processingOverlay.Show();
        var settings = Settings.Copy();
        try
        {
            var processor = new RecordingProcessor(new OpenAiClient(_http), WorkingFolder);
            var result = await processor.ProcessAsync(path, settings, message => Dispatcher.BeginInvoke(() => ShowProgress(message)), _processingCancellation.Token);
            if (result.HasOutput)
            {
                var pasted = Native.CopyAndPaste(result.Metadata.FinalText);
                Notify(T(pasted ? "Pasted" : "CopiedOnly"), Forms.ToolTipIcon.Info);
            }
            else Notify(T("NoTextMessage"), Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is OpenAiException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or InvalidOperationException)
        {
            if (!_quitRequested) Notify(string.IsNullOrWhiteSpace(settings.ApiKey) ? T("MissingKey") : T("ApiError"), Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _processingOverlay?.Close(); _processingOverlay = null;
            IsProcessing = false; _processingCancellation.Dispose(); _processingCancellation = null;
            _window?.SetState(false, false, null); _window?.RefreshRecordings(force: true);
            if (_quitRequested) FinishQuit();
        }
    }

    private void ShowProgress(string progress)
    {
        string text;
        var parts = progress.Split(':');
        if (parts[0] == "transcribing" && parts.Length == 3)
            text = Settings.Language == "ja" ? $"文字起こし中 {parts[1]}/{parts[2]}" : $"Transcribing {parts[1]}/{parts[2]}";
        else if (parts[0] == "refining") text = Settings.Language == "ja" ? "文章を整えています…" : "Refining text…";
        else text = Settings.Language == "ja" ? "音声を準備しています…" : "Preparing audio…";
        _window?.SetState(false, true, text);
        if (_processingOverlay is not null) _processingOverlay.Message = text;
    }

    private void Notify(string message, Forms.ToolTipIcon icon)
    {
        if (_tray is null) return;
        _tray.BalloonTipTitle = T("AppTitle"); _tray.BalloonTipText = message; _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(3000);
    }

    private void RequestQuit()
    {
        _quitRequested = true;
        if (IsRecording) { _recorder.Stop(); return; }
        if (IsProcessing) { _processingCancellation?.Cancel(); return; }
        FinishQuit();
    }
    private void FinishQuit() { _window?.AllowClose(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        _openSettingsRegistration?.Unregister(null); _openSettingsSignal?.Dispose();
        _tray?.Dispose(); _hotkey?.Dispose(); _recorder.Dispose(); _http.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }
}

internal static class CuePlayer
{
    public static void Play(bool starting)
    {
        try
        {
            const int rate = 16000, samples = 1600;
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (var i = 0; i < samples; i++)
                {
                    var frequency = starting ? 900d : 650d;
                    var envelope = Math.Min(1, Math.Min(i / 150d, (samples - i) / 250d));
                    writer.Write((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * envelope * 7000));
                }
            }
            memory.Position = 0;
            using var player = new SoundPlayer(memory); player.PlaySync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
