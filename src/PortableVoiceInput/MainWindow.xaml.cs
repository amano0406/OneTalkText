using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using PortableVoiceInput.Core;

namespace PortableVoiceInput;

public sealed record RecordingRow(string Path, string Created, string Duration, string Status, string FileName, RecordingMetadata Metadata);

public partial class MainWindow : Window
{
    private readonly App _app;
    private AppSettings _draft;
    private readonly ObservableCollection<RecordingRow> _rows = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _modelsTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly HttpClient _modelsHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _modelsCancellation;
    private MicrophonePreview? _preview;
    private readonly List<string> _triggers = [];
    private bool _loading = true, _capturingShortcut, _quitting, _refreshingRows, _updatingPlayback, _measuring, _modelFeedbackActive;
    private WaveOutEvent? _playback;
    private WaveFileReader? _playbackReader;
    private string? _playbackPath;
    private TimeSpan _selectedDuration, _pendingSeek;
    private string _lastListing = "";
    private static readonly string[] DefaultTranscriptionModels = ["gpt-transcribe", "gpt-4o-transcribe", "gpt-4o-mini-transcribe", "whisper-1"];
    private static readonly string[] DefaultCleanupModels = ["gpt-4.1-mini", "gpt-4.1", "gpt-5.4-mini", "gpt-6-luna", "gpt-6.1-sol", "gpt-6-astra"];

    public MainWindow(App app)
    {
        _app = app; _draft = app.Settings.Copy();
        InitializeComponent();
        UpdateColumnHeaders();
        RecordingsGrid.ItemsSource = _rows;
        PopulateSettings();
        _loading = false;
        UpdateResetButton();
        _refreshTimer.Tick += (_, _) => { if (IsVisible) RefreshRecordings(); };
        _refreshTimer.Start();
        _previewTimer.Tick += (_, _) => UpdateAudioPreview();
        _playbackTimer.Tick += (_, _) => UpdatePlayback();
        _modelsTimer.Tick += async (_, _) => { _modelsTimer.Stop(); await LoadModelsAsync(); };
        Loaded += async (_, _) => await LoadModelsAsync();
        IsVisibleChanged += (_, _) => { if (IsVisible) ResumeAudioPreview(); else { SuspendAudioPreview(); if (_capturingShortcut) EndShortcutCapture(); } };
        MicrophoneBox.SelectionChanged += (_, _) => { if (!_loading) { SuspendAudioPreview(); ResumeAudioPreview(); } };
        RefreshRecordings(force: true);
    }

    private new string Language => _draft.Language;
    private string T(string key) => Loc.Text(key, Language);

    private void PopulateSettings()
    {
        LanguageBox.SelectedIndex = _draft.Language == "ja" ? 1 : 0;
        ApiKeyBox.Password = _draft.ApiKey;
        SetModelChoices(TranscriptionModelBox, DefaultTranscriptionModels, _draft.TranscriptionModel);
        SetModelChoices(CleanupModelBox, DefaultCleanupModels, _draft.CleanupModel);
        PopulateMicrophones();
        ShortcutText.Text = FormatShortcut(_draft.HotkeyModifiers, _draft.HotkeyKey);
        DimCheck.IsChecked = _draft.DimScreen; SoundCheck.IsChecked = _draft.PlaySounds;
        _triggers.Clear(); _triggers.AddRange(_draft.TriggerWords.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        RenderTriggers(); PromptBox.Text = _draft.CleanupPrompt;
        TrimCheck.IsChecked = _draft.TrimSilence;
        ThresholdBox.Text = _draft.SilenceThresholdDb.ToString("0.##", CultureInfo.InvariantCulture);
        MinimumBox.Text = _draft.MinimumSilenceSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        GapBox.Text = _draft.RemainingGapSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        MarginBox.Text = _draft.NoiseMarginDb.ToString("0.##", CultureInfo.InvariantCulture);
        SetState(_app.IsRecording, _app.IsProcessing, null);
    }

    private void PopulateMicrophones()
    {
        var selected = (MicrophoneBox.SelectedItem as MicrophoneOption)?.DeviceNumber == -1 ? "" :
            (MicrophoneBox.SelectedItem as MicrophoneOption)?.Name ?? _draft.MicrophoneName;
        var devices = AudioRecorder.GetMicrophones(T("DefaultMic"));
        if (!string.IsNullOrWhiteSpace(selected) && !devices.Any(x => x.DeviceNumber >= 0 && x.Name == selected))
            devices.Add(new MicrophoneOption(selected, -2));
        MicrophoneBox.ItemsSource = devices;
        var item = devices.FirstOrDefault(x => x.DeviceNumber != -1 && x.Name == selected);
        MicrophoneBox.SelectedItem = item ?? devices[0];
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem selected) return;
        _draft.Language = selected.Tag?.ToString() == "ja" ? "ja" : "en";
        Loc.Apply(_draft.Language);
        UpdateColumnHeaders();
        PopulateMicrophones();
        SetState(_app.IsRecording, _app.IsProcessing, null);
        RefreshRecordings(force: true);
    }

    private void UpdateColumnHeaders()
    {
        CreatedHeader.Header = T("CreatedColumn");
        DurationHeader.Header = T("DurationColumn");
        StatusHeader.Header = T("StatusColumn");
        FileHeader.Header = T("FileColumn");
    }

    private void SetShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingShortcut) return;
        _app.SuspendShortcut();
        _capturingShortcut = true;
        SetShortcutButton.Content = T("PressShortcut");
        SetShortcutButton.Focus();
    }

    private void Shortcut_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturingShortcut) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndShortcutCapture(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var code = KeyInterop.VirtualKeyFromKey(key);
        if (code <= 0) return;
        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 8;
        _draft.HotkeyModifiers = modifiers; _draft.HotkeyKey = (uint)code;
        ShortcutText.Text = FormatShortcut(modifiers, (uint)code);
        EndShortcutCapture();
    }

    private void EndShortcutCapture()
    {
        if (!_capturingShortcut) return;
        _capturingShortcut = false;
        SetShortcutButton.SetResourceReference(ContentControl.ContentProperty, "SetShortcut");
        _app.ResumeShortcut();
    }

    private void SetShortcutButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { if (_capturingShortcut) EndShortcutCapture(); }

    private void ClearKey_Click(object sender, RoutedEventArgs e) => ApiKeyBox.Clear();
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _modelsCancellation?.Cancel(); _modelsTimer.Stop();
        if (_modelFeedbackActive) { FooterText.Text = ""; _modelFeedbackActive = false; }
        SetModelChoices(TranscriptionModelBox, DefaultTranscriptionModels, TranscriptionModelBox.SelectedItem as string ?? _draft.TranscriptionModel);
        SetModelChoices(CleanupModelBox, DefaultCleanupModels, CleanupModelBox.SelectedItem as string ?? _draft.CleanupModel);
        if (ApiKeyBox.Password.Trim().Length >= 20) _modelsTimer.Start();
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        _modelsTimer.Stop();
        await LoadModelsAsync(manual: true);
    }

    private static void SetModelChoices(ComboBox box, IEnumerable<string> models, string current)
    {
        var choices = models.Where(x => !string.IsNullOrWhiteSpace(x)).Append(current)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        box.ItemsSource = choices;
        box.SelectedItem = choices.FirstOrDefault(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
    }
    private static bool IsTranscriptionModel(string id) =>
        id.Equals("whisper-1", StringComparison.OrdinalIgnoreCase) ||
        ((id.StartsWith("gpt-transcribe", StringComparison.OrdinalIgnoreCase) || id.StartsWith("gpt-4o-transcribe", StringComparison.OrdinalIgnoreCase) || id.StartsWith("gpt-4o-mini-transcribe", StringComparison.OrdinalIgnoreCase)) &&
         !id.Contains("diarize", StringComparison.OrdinalIgnoreCase));
    private static bool IsCleanupModel(string id) =>
        (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) &&
        !new[] { "audio", "transcribe", "realtime", "image", "tts", "search", "instruct", "embedding", "moderation", "live" }.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase));

    private async Task LoadModelsAsync(bool manual = false)
    {
        if (_quitting) return;
        var key = ApiKeyBox.Password.Trim();
        if (string.IsNullOrEmpty(key)) { if (manual) FooterText.Text = T("MissingKey"); return; }
        _modelsCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _modelsCancellation = cancellation;
        if (manual) { FooterText.Text = T("ModelsLoading"); _modelFeedbackActive = true; }
        try
        {
            var models = await new OpenAiClient(_modelsHttp).ListModelsAsync(key, cancellation.Token);
            if (cancellation.IsCancellationRequested || key != ApiKeyBox.Password.Trim() || _quitting) return;
            var speech = TranscriptionModelBox.SelectedItem as string ?? _draft.TranscriptionModel;
            var cleanup = CleanupModelBox.SelectedItem as string ?? _draft.CleanupModel;
            SetModelChoices(TranscriptionModelBox, models.Where(IsTranscriptionModel), speech);
            SetModelChoices(CleanupModelBox, models.Where(IsCleanupModel), cleanup);
            if (manual && _modelFeedbackActive) { FooterText.Text = T("ModelsLoaded"); _modelFeedbackActive = false; }
        }
        catch (OperationCanceledException)
        { if (manual && _modelFeedbackActive && !cancellation.IsCancellationRequested && !_quitting) { FooterText.Text = T("ModelsFailed"); _modelFeedbackActive = false; } }
        catch (Exception ex) when (ex is OpenAiException or HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        { if (manual && _modelFeedbackActive && !cancellation.IsCancellationRequested && !_quitting) { FooterText.Text = T("ModelsFailed"); _modelFeedbackActive = false; } }
        finally { if (_modelsCancellation == cancellation) _modelsCancellation = null; cancellation.Dispose(); }
    }

    private void RenderTriggers()
    {
        TriggerTags.Children.Clear();
        foreach (var word in _triggers.ToArray())
        {
            var label = new TextBlock { Text = word, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
            var remove = new Button { Content = "×", Padding = new Thickness(2, 0, 4, 0), MinHeight = 20, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, ToolTip = T("RemoveTrigger") };
            remove.Click += (_, _) => { _triggers.Remove(word); RenderTriggers(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(label); row.Children.Add(remove);
            TriggerTags.Children.Add(new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 64, 86)), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 4), Child = row });
        }
    }
    private void AddPendingTrigger()
    {
        foreach (var word in TriggerInput.Text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!_triggers.Contains(word, StringComparer.OrdinalIgnoreCase)) _triggers.Add(word);
        TriggerInput.Clear(); RenderTriggers();
    }
    private void AddTrigger_Click(object sender, RoutedEventArgs e) => AddPendingTrigger();
    private void TriggerInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) { AddPendingTrigger(); e.Handled = true; } }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || e.OriginalSource != Tabs) return;
        FooterText.Text = ""; _modelFeedbackActive = false;
        UpdateResetButton();
        SuspendAudioPreview(); ResumeAudioPreview();
    }

    private void UpdateResetButton()
    {
        if (ResetTabButton is null || Tabs is null) return;
        ResetTabButton.Visibility = Tabs.SelectedIndex is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
        ResetTabButton.IsEnabled = !(_measuring && Tabs.SelectedIndex == 1);
    }

    private void ResetTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedIndex == 0)
        {
            _draft.ResetGeneral();
            LanguageBox.SelectedIndex = _draft.Language == "ja" ? 1 : 0;
            SetModelChoices(TranscriptionModelBox, (TranscriptionModelBox.ItemsSource as IEnumerable<string>) ?? DefaultTranscriptionModels, _draft.TranscriptionModel);
            SetModelChoices(CleanupModelBox, (CleanupModelBox.ItemsSource as IEnumerable<string>) ?? DefaultCleanupModels, _draft.CleanupModel);
            MicrophoneBox.SelectedIndex = 0;
            ShortcutText.Text = FormatShortcut(_draft.HotkeyModifiers, _draft.HotkeyKey);
            DimCheck.IsChecked = _draft.DimScreen; SoundCheck.IsChecked = _draft.PlaySounds;
            _triggers.Clear(); _triggers.AddRange(_draft.TriggerWords.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            TriggerInput.Clear(); RenderTriggers(); PromptBox.Text = _draft.CleanupPrompt;
        }
        else if (Tabs.SelectedIndex == 1 && !_measuring)
        {
            _draft.ResetAudio();
            TrimCheck.IsChecked = _draft.TrimSilence;
            ThresholdBox.Text = _draft.SilenceThresholdDb.ToString("0.##", CultureInfo.InvariantCulture);
            MinimumBox.Text = _draft.MinimumSilenceSeconds.ToString("0.##", CultureInfo.InvariantCulture);
            GapBox.Text = _draft.RemainingGapSeconds.ToString("0.##", CultureInfo.InvariantCulture);
            MarginBox.Text = _draft.NoiseMarginDb.ToString("0.##", CultureInfo.InvariantCulture);
            CalibrationText.Text = "";
        }
        else return;
        _modelFeedbackActive = false;
        FooterText.Text = T("ResetPending");
    }
    public void SuspendAudioPreview()
    {
        _previewTimer.Stop();
        if (_preview is not null) { _preview.Dispose(); _preview = null; }
    }
    public void ResumeAudioPreview()
    {
        if (_loading || !IsVisible || Tabs.SelectedItem != AudioTabItem || _app.IsRecording || _measuring || _preview is not null) return;
        try
        {
            var microphone = MicrophoneBox.SelectedItem is MicrophoneOption { DeviceNumber: not -1 } mic ? mic.Name : "";
            _preview = new MicrophonePreview(microphone); PreviewGraph.Reset();
            PreviewStatus.Text = ""; _previewTimer.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or MmException)
        { PreviewStatus.Text = T("PreviewUnavailable"); }
    }
    private void UpdateAudioPreview()
    {
        if (_preview is null) return;
        static double Read(string text, double fallback, double min, double max) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
        var db = _preview.Decibels;
        PreviewGraph.Push(db, Read(ThresholdBox.Text, -45, -90, 0), Read(MinimumBox.Text, 2, .1, 120), Read(GapBox.Text, 1, 0, 10), TrimCheck.IsChecked == true);
        PreviewStatus.Text = Language == "ja" ? $"入力 {db:0} dB" : $"Input {db:0} dB";
    }

    public static string FormatShortcut(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 8) != 0) parts.Add("Win");
        var k = KeyInterop.KeyFromVirtualKey((int)key);
        parts.Add(k == Key.Space ? "Space" : k.ToString());
        return string.Join(" + ", parts);
    }

    private bool ReadDraftFromControls()
    {
        static bool Number(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        if (!Number(ThresholdBox.Text, out var threshold) || threshold is < -90 or > 0 ||
            !Number(MinimumBox.Text, out var minimum) || minimum is < 0.1 or > 120 ||
            !Number(GapBox.Text, out var gap) || gap is < 0 or > 10 ||
            !Number(MarginBox.Text, out var margin) || margin is < 0 or > 30)
        { System.Windows.MessageBox.Show(T("InvalidNumber"), T("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        _draft.ApiKey = ApiKeyBox.Password.Trim();
        _draft.TranscriptionModel = TranscriptionModelBox.SelectedItem as string ?? "gpt-transcribe";
        _draft.CleanupModel = CleanupModelBox.SelectedItem as string ?? "gpt-4.1-mini";
        _draft.MicrophoneName = MicrophoneBox.SelectedItem is MicrophoneOption { DeviceNumber: not -1 } mic ? mic.Name : "";
        _draft.DimScreen = DimCheck.IsChecked == true; _draft.PlaySounds = SoundCheck.IsChecked == true;
        AddPendingTrigger(); _draft.TriggerWords = string.Join("\n", _triggers); _draft.CleanupPrompt = PromptBox.Text;
        _draft.TrimSilence = TrimCheck.IsChecked == true; _draft.SilenceThresholdDb = threshold;
        _draft.MinimumSilenceSeconds = minimum; _draft.RemainingGapSeconds = gap; _draft.NoiseMarginDb = margin;
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadDraftFromControls()) return;
        if (!_app.ApplySettings(_draft.Copy())) return;
        FooterText.Text = T("SettingsSaved");
        RefreshRecordings(force: true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_quitting) { e.Cancel = true; Hide(); }
    }
    public void AllowClose() { _quitting = true; _refreshTimer.Stop(); _modelsTimer.Stop(); _modelsCancellation?.Cancel(); SuspendAudioPreview(); StopPlayback(); _modelsHttp.Dispose(); Close(); }
    private void RecordButton_Click(object sender, RoutedEventArgs e) => _app.ToggleRecording();

    private async void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_app.IsRecording || _app.IsProcessing) { CalibrationText.Text = T("BusyMessage"); return; }
        if (!double.TryParse(MarginBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin) || margin is < 0 or > 30)
        { CalibrationText.Text = T("InvalidNumber"); return; }
        CalibrateButton.IsEnabled = false; CalibrationText.Text = T("CalibrationRunning");
        _measuring = true; UpdateResetButton(); SuspendAudioPreview();
        try
        {
            var microphone = MicrophoneBox.SelectedItem is MicrophoneOption { DeviceNumber: not -1 } mic ? mic.Name : "";
            var db = await AudioRecorder.MeasureBackgroundAsync(microphone, CancellationToken.None);
            var suggested = Math.Clamp(db + margin, -90, -5);
            ThresholdBox.Text = suggested.ToString("0.0", CultureInfo.InvariantCulture);
            CalibrationText.Text = Language == "ja" ? $"環境音 {db:0.0} dB → 基準 {suggested:0.0} dB" : $"Background {db:0.0} dB → threshold {suggested:0.0} dB";
        }
        catch (Exception ex) when (ex is InvalidOperationException or MmException or TimeoutException)
        { CalibrationText.Text = T("MissingMicrophone"); }
        finally { _measuring = false; UpdateResetButton(); CalibrateButton.IsEnabled = true; ResumeAudioPreview(); }
    }

    public void SetState(bool recording, bool processing, string? detail)
    {
        if (!IsInitialized) return;
        StatusText.Text = detail ?? (recording ? T("Recording") : processing ? T("Processing") : T("Ready"));
        RecordButton.Content = T(recording ? "StopNow" : "RecordNow");
    }

    public void RefreshRecordings(bool force = false)
    {
        if (!Directory.Exists(_app.RecordingsFolder)) return;
        try
        {
            var files = Directory.EnumerateFiles(_app.RecordingsFolder, "*.wav").Select(p => new FileInfo(p)).OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
            var stamp = string.Join('|', files.Select(f => f.Name + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks)) + Language;
            if (!force && stamp == _lastListing) return;
            _lastListing = stamp;
            var selected = (RecordingsGrid.SelectedItem as RecordingRow)?.Path;
            _refreshingRows = true;
            _rows.Clear();
            foreach (var file in files)
            {
                try
                {
                    var (info, meta) = WaveMetadata.Read(file.FullName);
                    var status = file.Name.EndsWith(".recording.wav", StringComparison.OrdinalIgnoreCase) && _app.IsRecording ? T("Recording") : Loc.Status(meta.Status, Language);
                    var seconds = Math.Max(0, info.DurationSeconds);
                    var duration = seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
                    _rows.Add(new(file.FullName, meta.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), duration, status, file.Name, meta));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _rows.Add(new(file.FullName, file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"), "—", T("Interrupted"), file.Name,
                        new RecordingMetadata { Status = RecordingStatus.Interrupted, Error = "The recording needs recovery." }));
                }
            }
            RecordingsGrid.SelectedItem = _rows.FirstOrDefault(x => x.Path == selected);
            _refreshingRows = false;
            if (_playbackPath is not null && !string.Equals((RecordingsGrid.SelectedItem as RecordingRow)?.Path, _playbackPath, StringComparison.OrdinalIgnoreCase)) StopPlayback();
            if (RecordingsGrid.SelectedItem is null) { _selectedDuration = TimeSpan.Zero; _pendingSeek = TimeSpan.Zero; UpdatePlayback(); }
            UpdateResult();
        }
        catch (IOException) { }
        finally { _refreshingRows = false; }
    }

    private void RecordingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingRows) return;
        if (_playbackPath is not null && !string.Equals((RecordingsGrid.SelectedItem as RecordingRow)?.Path, _playbackPath, StringComparison.OrdinalIgnoreCase)) StopPlayback();
        _selectedDuration = TimeSpan.Zero; _pendingSeek = TimeSpan.Zero;
        if (RecordingsGrid.SelectedItem is RecordingRow row)
        {
            try { _selectedDuration = TimeSpan.FromSeconds(Math.Max(0, WaveMetadata.Read(row.Path).Info.DurationSeconds)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        UpdatePlayback();
        UpdateResult();
    }
    private void UpdateResult()
    {
        if (ResultBox is null) return;
        if (RecordingsGrid.SelectedItem is not RecordingRow row) { ResultBox.Text = T("NoSelection"); return; }
        var text = string.IsNullOrWhiteSpace(row.Metadata.FinalText) ? row.Metadata.Transcript : row.Metadata.FinalText;
        var error = row.Metadata.Status == RecordingStatus.Failed && Language == "ja" ? "文字起こしに失敗しました。録音は残っています。" : row.Metadata.Error;
        ResultBox.Text = string.Join("\n\n", new[] { text, error }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingsGrid.SelectedItem is RecordingRow row) { StopPlayback(); _app.Retry(row.Path); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingsGrid.SelectedItem is not RecordingRow row) return;
        var text = string.IsNullOrWhiteSpace(row.Metadata.FinalText) ? row.Metadata.Transcript : row.Metadata.FinalText;
        if (!string.IsNullOrWhiteSpace(text)) { try { System.Windows.Clipboard.SetText(text); FooterText.Text = T("CopiedOnly"); } catch (System.Runtime.InteropServices.COMException) { FooterText.Text = T("ApiError"); } }
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingsGrid.SelectedItem is not RecordingRow row) return;
        try
        {
            if (_playback is null || _playbackReader is null || !string.Equals(_playbackPath, row.Path, StringComparison.OrdinalIgnoreCase))
            {
                StopPlayback(); _playbackReader = new WaveFileReader(row.Path); _playback = new WaveOutEvent();
                _playback.Init(_playbackReader); _playbackPath = row.Path;
                _playbackReader.CurrentTime = _pendingSeek;
            }
            if (_playbackReader.CurrentTime >= _playbackReader.TotalTime) _playbackReader.CurrentTime = TimeSpan.Zero;
            _playback.Play(); _playbackTimer.Start(); UpdatePlayback();
        }
        catch (Exception ex) when (ex is IOException or MmException or InvalidDataException) { FooterText.Text = T("RecordingError"); StopPlayback(); }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { _playback?.Pause(); UpdatePlayback(); }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _playback?.Stop(); _pendingSeek = TimeSpan.Zero;
        if (_playbackReader is not null) _playbackReader.CurrentTime = TimeSpan.Zero;
        UpdatePlayback();
    }
    private void Rewind_Click(object sender, RoutedEventArgs e) { _pendingSeek = TimeSpan.Zero; if (_playbackReader is not null) _playbackReader.CurrentTime = TimeSpan.Zero; UpdatePlayback(); }
    private void PlaybackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPlayback) return;
        var duration = _playbackReader?.TotalTime ?? _selectedDuration;
        _pendingSeek = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, duration.TotalSeconds));
        if (_playbackReader is not null) _playbackReader.CurrentTime = _pendingSeek;
        PlaybackElapsed.Text = FormatTime(_playbackReader?.CurrentTime ?? _pendingSeek);
    }
    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    private void UpdatePlayback()
    {
        if (PlaybackSlider is null) return;
        _updatingPlayback = true;
        try
        {
            var total = _playbackReader?.TotalTime ?? _selectedDuration;
            var current = _playbackReader?.CurrentTime ?? _pendingSeek;
            PlaybackSlider.Maximum = Math.Max(1, total.TotalSeconds);
            PlaybackSlider.Value = Math.Clamp(current.TotalSeconds, 0, PlaybackSlider.Maximum);
            PlaybackElapsed.Text = FormatTime(current); PlaybackDuration.Text = FormatTime(total);
        }
        finally { _updatingPlayback = false; }
    }
    private void StopPlayback()
    {
        _playbackTimer.Stop(); _playback?.Stop(); _playback?.Dispose(); _playback = null;
        _playbackReader?.Dispose(); _playbackReader = null; _playbackPath = null; UpdatePlayback();
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingsGrid.SelectedItem is not RecordingRow row || _app.IsRecording || _app.IsProcessing) return;
        if (System.Windows.MessageBox.Show(T("DeleteConfirm"), T("DeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { StopPlayback(); File.Delete(row.Path); RefreshRecordings(force: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FooterText.Text = T("SaveFailed"); }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRecordings(force: true);
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_app.RecordingsFolder);
        Process.Start(new ProcessStartInfo { FileName = _app.RecordingsFolder, UseShellExecute = true });
    }
}
