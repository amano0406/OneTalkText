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
    private DiagnosticLog? _log;
    private GlobalHotkey _hotkey = null!;
    private readonly AudioRecorder _recorder = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private DimOverlay? _dim;
    private WaveformOverlay? _wave;
    private ProcessingOverlay? _preparationOverlay;
    private ProcessingOverlay? _processingOverlay;
    private ProcessingOverlay? _startupOverlay;
    private CancellationTokenSource? _processingCancellation;
    private readonly CancellationTokenSource _startupCancellation = new();
    private Mutex? _instance;
    private EventWaitHandle? _openSettingsSignal;
    private RegisteredWaitHandle? _openSettingsRegistration;
    private bool _stopping, _quitRequested, _preparing, _openSettingsRequested, _shuttingDown;
    private int _recordingGeneration;
    public AppSettings Settings { get; private set; } = new();
    public bool IsRecording => _recorder.IsRecording;
    public bool IsPreparing => _preparing;
    public bool IsProcessing { get; private set; }
    public string AppFolder { get; private set; } = "";
    public string RecordingsFolder => Path.Combine(AppFolder, "recordings");
    private string WorkingFolder => Path.Combine(AppFolder, "working");
    private string T(string key) => Loc.Text(key, Settings.Language);

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == UpdateHandoff.HelperSwitch)
        {
            base.OnStartup(e);
            await RunUpdateHelperAsync(e.Args);
            return;
        }
        AppFolder = AppContext.BaseDirectory;
        InitializeLogging();
        base.OnStartup(e);
        var instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppFolder.ToUpperInvariant())))[..16];
        _instance = new Mutex(true, UpdatePaths.InstanceMutexName(AppFolder), out var isNew);
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
                (_, _) => Dispatcher.BeginInvoke(() => { if (_window is null) _openSettingsRequested = true; else ShowSettings(); }), null, Timeout.Infinite, false);
            Directory.CreateDirectory(RecordingsFolder);
            Directory.CreateDirectory(WorkingFolder);
            _config = new ConfigStore(AppFolder);
            Settings = _config.Load();
            Loc.Apply(Settings.Language);
            var completedId = FindUpdateId(e.Args, UpdateHandoff.CompletedSwitch);
            var failedId = FindUpdateId(e.Args, UpdateHandoff.FailedSwitch);
            var updateFailed = false;
            // Self-contained single-file releases can update their own executable.
            // Framework-dependent development builds keep their separate DLLs and skip this step.
            if (completedId is null && !e.Args.Contains(UpdateHandoff.SkipSwitch, StringComparer.Ordinal) &&
                IsSingleFileRelease())
            {
                _startupOverlay = new ProcessingOverlay(Settings, T("CheckingUpdates"));
                _startupOverlay.Show();
                try
                {
                    var result = await PrepareUpdateAsync(e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase));
                    updateFailed = result.Failed;
                    if (result.HandedOff) { Shutdown(); return; }
                }
                finally { _startupOverlay?.Close(); _startupOverlay = null; }
            }
            if (_shuttingDown) return;
            var recovered = RecoverInterruptedRecordings();
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += ToggleRecording;
            var registered = _hotkey.Register(Settings.HotkeyModifiers, Settings.HotkeyKey);
            _recorder.Finished += result => Dispatcher.BeginInvoke(() => RecordingFinished(result));
            CreateTray();
            _window = new MainWindow(this);
            MainWindow = _window;
            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase) || _openSettingsRequested || !registered) ShowSettings();
            if (!registered) Notify(T("HotkeyUnavailable"), Forms.ToolTipIcon.Warning);
            else if (recovered > 0) Notify(T("RecoverMessage"), Forms.ToolTipIcon.Info);
            else if (failedId is not null || updateFailed) Notify(T("UpdateFailed"), Forms.ToolTipIcon.Warning);
            if (completedId is Guid completed)
            {
                UpdateHandoff.SignalReady(completed);
                ScheduleUpdateCleanup(completed);
            }
            else if (failedId is Guid failed) ScheduleUpdateCleanup(failed);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log?.Error("Application startup failed", ex);
            System.Windows.MessageBox.Show("OneTalk Text could not access its app folder. " + ex.Message);
            Shutdown();
        }
    }

    private void InitializeLogging()
    {
        try { _log = new DiagnosticLog(AppFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        DispatcherUnhandledException += (_, args) => _log?.Error("Unhandled UI exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log?.Error("Unhandled application exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private static bool IsSingleFileRelease()
    {
#pragma warning disable IL3000 // An empty Location is the intended single-file detection here.
        return string.IsNullOrEmpty(typeof(App).Assembly.Location);
#pragma warning restore IL3000
    }

    private async Task<(bool HandedOff, bool Failed)> PrepareUpdateAsync(bool openSettings)
    {
        var updateAvailable = false;
        UpdatePaths? paths = null;
        var downloaded = false; var handedOff = false;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var client = new GitHubUpdateClient(http);
            using var check = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
            check.CancelAfter(TimeSpan.FromSeconds(6));
            var current = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
            var release = await client.CheckAsync(current, check.Token);
            if (release is null) return (false, false);
            updateAvailable = true;
            var executable = Environment.ProcessPath ?? throw new IOException("The app executable path is unavailable.");
            paths = new UpdatePaths(AppFolder, Path.GetFileName(executable), Guid.NewGuid());
            var progress = new Progress<int>(percent => { if (_startupOverlay is not null) _startupOverlay.Message = T("DownloadingUpdate") + " " + percent + "%"; });
            if (_startupOverlay is not null) _startupOverlay.Message = T("DownloadingUpdate");
            using var download = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
            download.CancelAfter(TimeSpan.FromMinutes(15));
            await client.DownloadAsync(release, paths.Candidate, progress, download.Token);
            downloaded = true;
            GitHubUpdateClient.VerifyExecutableVersion(paths.Candidate, release);
            if (_startupOverlay is not null) _startupOverlay.Message = T("ApplyingUpdate");
            await UpdateHandoff.LaunchAsync(paths, release, Settings.Language, openSettings || _openSettingsRequested, _startupCancellation.Token);
            handedOff = true;
            return (true, false);
        }
        catch (OperationCanceledException) when (_shuttingDown) { throw; }
        catch (Exception ex)
        {
            _log?.Error("Automatic update could not be prepared", ex);
            return (false, updateAvailable);
        }
        finally
        {
            if (downloaded && !handedOff && paths is not null)
            {
                try { File.Delete(paths.Candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task RunUpdateHelperAsync(string[] args)
    {
        if (!UpdateHandoff.TryParseHelper(args, Environment.ProcessPath, out var invocation) || invocation is null) { Shutdown(2); return; }
        AppFolder = invocation.Paths.AppFolder;
        Settings.Language = invocation.Language;
        InitializeLogging();
        Loc.Apply(Settings.Language);
        var overlay = new ProcessingOverlay(Settings, T("ApplyingUpdate"));
        try
        {
            overlay.Show();
            await Task.Run(() => UpdateHandoff.Run(invocation, (operation, ex) => _log?.Error(operation, ex)));
        }
        catch (Exception ex) { _log?.Error("Update helper failed", ex); }
        finally { overlay.Close(); Shutdown(); }
    }

    private static Guid? FindUpdateId(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length && Guid.TryParseExact(args[index + 1], "N", out var id) && id != Guid.Empty ? id : null;
    }

    private void ScheduleUpdateCleanup(Guid id)
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executable)) UpdateHandoff.ScheduleCleanup(new UpdatePaths(AppFolder, Path.GetFileName(executable), id));
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
                WaveMetadata.Write(destination, new RecordingMetadata
                {
                    Status = RecordingStatus.Interrupted, OriginalSeconds = info.DurationSeconds,
                    Error = "The recording was interrupted and can be retried.", CalibrationReliable = false,
                    ProtectedStartSeconds = Math.Min(NoiseCalibrationAccumulator.DurationSeconds, info.DurationSeconds)
                });
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _log?.Error("Recording recovery failed", ex); }
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _log?.Error("Processing recovery failed", ex); }
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
            _log?.Error("Saving settings failed", ex);
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
            StopRecordingAfterTail();
            return;
        }
        if (IsProcessing) { Notify(T("BusyMessage"), Forms.ToolTipIcon.Info); return; }
        try
        {
            _window?.SuspendAudioPreview();
            var settings = Settings.Copy();
            _recorder.Start(RecordingsFolder, settings.MicrophoneName, settings.Language, settings.NoiseMarginDb);
            var generation = ++_recordingGeneration;
            _preparing = true;
            _preparationOverlay = new ProcessingOverlay(settings, T("PreparingRecording"));
            _preparationOverlay.Show();
            _window?.SetState(true, false, T("PreparingRecording")); _window?.RefreshRecordings(force: true); UpdateTrayMenu();
            _ = CompletePreparationAsync(generation, settings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NAudio.MmException)
        {
            _log?.Error("Starting recording failed", ex);
            if (_recorder.IsRecording)
            {
                _stopping = true;
                try { _recorder.Stop(); } catch (NAudio.MmException) { _stopping = false; }
            }
            Notify(ex is InvalidOperationException ? T("MissingMicrophone") : T("RecordingError"), Forms.ToolTipIcon.Error);
            _window?.ResumeAudioPreview();
        }
    }

    private async Task CompletePreparationAsync(int generation, AppSettings settings)
    {
        try
        {
            try { await _recorder.Preparation.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException ex)
            {
                _log?.Error("Microphone preparation timed out", ex);
                if (generation == _recordingGeneration && _recorder.IsRecording) _recorder.AbandonPreparation();
            }
            if (generation != _recordingGeneration || !_preparing || _stopping || _quitRequested || !_recorder.IsRecording || _recorder.IsEnding) return;
            _preparationOverlay?.Close(); _preparationOverlay = null;
            _preparing = false;
            if (settings.DimScreen) { _dim = new DimOverlay(); _dim.FadeIn(); }
            if (settings.PlaySounds) CuePlayer.Play(true);
            _wave = new WaveformOverlay(settings, () => _recorder.Level);
            _wave.PositionSaved += SaveWaveformPosition;
            _wave.Begin();
            _window?.SetState(true, false, null); UpdateTrayMenu();
        }
        catch (Exception ex)
        {
            _log?.Error("Finishing recording preparation failed", ex);
            Notify(T("RecordingError"), Forms.ToolTipIcon.Error);
            if (generation == _recordingGeneration && _recorder.IsRecording) StopRecordingAfterTail();
        }
    }

    private async void StopRecordingAfterTail()
    {
        if (_stopping) return;
        _stopping = true;
        // Capture audio that is still in a microphone or driver buffer when
        // the shortcut is pressed. The recording indicator remains visible.
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (!_recorder.IsRecording) return;
        try { _recorder.Stop(); }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
            _log?.Error("Stopping recording failed", ex);
            _stopping = false;
            Notify(T("RecordingError"), Forms.ToolTipIcon.Error);
        }
    }

    private void SaveWaveformPosition(double left, double top)
    {
        Settings.WaveformLeft = left; Settings.WaveformTop = top;
        try { _config.Save(Settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.Error("Saving waveform position failed", ex); Notify(T("SaveFailed"), Forms.ToolTipIcon.Warning); }
    }

    private void RecordingFinished(RecordingFinished result)
    {
        _stopping = false;
        _preparing = false;
        _preparationOverlay?.Close(); _preparationOverlay = null;
        _wave?.Finish(); _wave = null;
        _dim?.FadeOut(); _dim = null;
        _window?.ResumeAudioPreview();
        if (Settings.PlaySounds) CuePlayer.Play(false);
        UpdateTrayMenu(); _window?.SetState(false, false, null); _window?.RefreshRecordings(force: true);
        if (result.Interrupted) _log?.Error("Recording stopped unexpectedly", result.Exception);
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
        catch (OperationCanceledException) when (_processingCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log?.Error("Transcription or text delivery failed", ex);
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
        if (IsRecording) { StopRecordingAfterTail(); return; }
        if (IsProcessing) { _processingCancellation?.Cancel(); return; }
        FinishQuit();
    }
    private void FinishQuit() { _window?.AllowClose(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        _startupCancellation.Cancel(); _startupCancellation.Dispose();
        _openSettingsRegistration?.Unregister(null); _openSettingsSignal?.Dispose();
        _tray?.Dispose(); _hotkey?.Dispose(); _recorder.Dispose(); _http.Dispose(); _instance?.Dispose();
        _log?.Dispose();
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
