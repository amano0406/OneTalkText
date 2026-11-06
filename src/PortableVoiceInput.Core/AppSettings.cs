using System.Globalization;
using System.Text;

namespace PortableVoiceInput.Core;

/// <summary>All application-owned settings live beside the executable.</summary>
public sealed class AppSettings
{
    public const string LegacyDefaultCleanupPrompt = "Make the dictated text easier to read while preserving its meaning and all important details. Remove filler words where appropriate. Do not invent facts or answer the dictated content. Remove the final trigger phrase. Return only the resulting text.";
    public const string DefaultCleanupPrompt = "This text is a transcript of the user's spoken voice input. " + LegacyDefaultCleanupPrompt;
    public string ApiKey { get; set; } = "";
    public string Language { get; set; } = "en";
    public string TranscriptionModel { get; set; } = "gpt-transcribe";
    public string CleanupModel { get; set; } = "gpt-4.1-mini";
    public string MicrophoneName { get; set; } = "";
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0004; // Ctrl + Shift
    public uint HotkeyKey { get; set; } = 0x20; // Space
    public bool DimScreen { get; set; } = true;
    public bool PlaySounds { get; set; } = true;
    public bool TrimSilence { get; set; } = true;
    public double SilenceThresholdDb { get; set; } = -45;
    public double MinimumSilenceSeconds { get; set; } = 2;
    public double RemainingGapSeconds { get; set; } = 1;
    public double NoiseMarginDb { get; set; } = 6;
    public double WaveformLeft { get; set; } = double.NaN;
    public double WaveformTop { get; set; } = double.NaN;
    public string TriggerWords { get; set; } = "整理して\nきれいにして\n綺麗にして\n整えて\nclean this up\npolish this";
    public string CleanupPrompt { get; set; } = DefaultCleanupPrompt;

    public AppSettings Copy() => (AppSettings)MemberwiseClone();

    public void ResetGeneral()
    {
        var defaults = new AppSettings();
        Language = defaults.Language;
        TranscriptionModel = defaults.TranscriptionModel; CleanupModel = defaults.CleanupModel;
        MicrophoneName = defaults.MicrophoneName;
        HotkeyModifiers = defaults.HotkeyModifiers; HotkeyKey = defaults.HotkeyKey;
        DimScreen = defaults.DimScreen; PlaySounds = defaults.PlaySounds;
        TriggerWords = defaults.TriggerWords; CleanupPrompt = defaults.CleanupPrompt;
    }

    public void ResetAudio()
    {
        var defaults = new AppSettings();
        TrimSilence = defaults.TrimSilence; SilenceThresholdDb = defaults.SilenceThresholdDb;
        MinimumSilenceSeconds = defaults.MinimumSilenceSeconds; RemainingGapSeconds = defaults.RemainingGapSeconds;
        NoiseMarginDb = defaults.NoiseMarginDb;
    }
}

public sealed class ConfigStore(string folder)
{
    public string Path { get; } = System.IO.Path.Combine(folder, "config.ini");

    public AppSettings Load()
    {
        var s = new AppSettings();
        if (!File.Exists(Path)) return s;
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(Path, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed.StartsWith('[')) continue;
            var equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;
            entries[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim();
        }
        string Get(string name, string fallback) => entries.TryGetValue(name, out var v) ? v : fallback;
        bool Bool(string name, bool fallback) => bool.TryParse(Get(name, ""), out var v) ? v : fallback;
        uint UInt(string name, uint fallback) => uint.TryParse(Get(name, ""), out var v) ? v : fallback;
        double Double(string name, double fallback) => double.TryParse(Get(name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        string Text(string name, string fallback)
        {
            if (!entries.TryGetValue(name, out var value)) return fallback;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch (FormatException) { return fallback; }
        }
        s.ApiKey = Get("OPENAI_API_KEY", "");
        s.Language = Get("LANGUAGE", "en") == "ja" ? "ja" : "en";
        s.TranscriptionModel = Get("TRANSCRIPTION_MODEL", s.TranscriptionModel);
        s.CleanupModel = Get("CLEANUP_MODEL", s.CleanupModel);
        s.MicrophoneName = Text("MICROPHONE_NAME_BASE64", "");
        s.HotkeyModifiers = UInt("HOTKEY_MODIFIERS", s.HotkeyModifiers);
        s.HotkeyKey = UInt("HOTKEY_KEY", s.HotkeyKey);
        s.DimScreen = Bool("DIM_SCREEN", s.DimScreen);
        s.PlaySounds = Bool("PLAY_SOUNDS", s.PlaySounds);
        s.TrimSilence = Bool("TRIM_SILENCE", s.TrimSilence);
        s.SilenceThresholdDb = Math.Clamp(Double("SILENCE_THRESHOLD_DB", s.SilenceThresholdDb), -90, 0);
        s.MinimumSilenceSeconds = Math.Clamp(Double("MIN_SILENCE_SECONDS", s.MinimumSilenceSeconds), 0.1, 120);
        s.RemainingGapSeconds = Math.Clamp(Double("REMAINING_GAP_SECONDS", s.RemainingGapSeconds), 0, 10);
        s.NoiseMarginDb = Math.Clamp(Double("NOISE_MARGIN_DB", s.NoiseMarginDb), 0, 30);
        s.WaveformLeft = Double("WAVEFORM_LEFT", double.NaN);
        s.WaveformTop = Double("WAVEFORM_TOP", double.NaN);
        s.TriggerWords = Text("TRIGGER_WORDS_BASE64", s.TriggerWords);
        s.CleanupPrompt = Text("CLEANUP_PROMPT_BASE64", s.CleanupPrompt);
        if (s.CleanupPrompt == AppSettings.LegacyDefaultCleanupPrompt) s.CleanupPrompt = AppSettings.DefaultCleanupPrompt;
        return s;
    }

    public void Save(AppSettings s)
    {
        static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        static string D(double value) => value.ToString(CultureInfo.InvariantCulture);
        // Never write the key to logs, exceptions, test output, or a source-controlled file.
        var lines = new[]
        {
            "; Portable Voice Input settings. Keep this file private: it contains your API key.",
            "OPENAI_API_KEY=" + s.ApiKey.Trim(),
            "LANGUAGE=" + s.Language,
            "TRANSCRIPTION_MODEL=" + s.TranscriptionModel.Trim(),
            "CLEANUP_MODEL=" + s.CleanupModel.Trim(),
            "MICROPHONE_NAME_BASE64=" + B64(s.MicrophoneName),
            "HOTKEY_MODIFIERS=" + s.HotkeyModifiers,
            "HOTKEY_KEY=" + s.HotkeyKey,
            "DIM_SCREEN=" + s.DimScreen,
            "PLAY_SOUNDS=" + s.PlaySounds,
            "TRIM_SILENCE=" + s.TrimSilence,
            "SILENCE_THRESHOLD_DB=" + D(s.SilenceThresholdDb),
            "MIN_SILENCE_SECONDS=" + D(s.MinimumSilenceSeconds),
            "REMAINING_GAP_SECONDS=" + D(s.RemainingGapSeconds),
            "NOISE_MARGIN_DB=" + D(s.NoiseMarginDb),
            "WAVEFORM_LEFT=" + D(s.WaveformLeft),
            "WAVEFORM_TOP=" + D(s.WaveformTop),
            "TRIGGER_WORDS_BASE64=" + B64(s.TriggerWords),
            "CLEANUP_PROMPT_BASE64=" + B64(s.CleanupPrompt)
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".new";
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
