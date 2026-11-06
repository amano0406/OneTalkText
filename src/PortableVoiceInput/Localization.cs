using PortableVoiceInput.Core;

namespace PortableVoiceInput;

internal static class Loc
{
    private static readonly Dictionary<string, (string En, string Ja)> Terms = new()
    {
        ["AppTitle"] = ("OneTalk Text", "OneTalk Text"),
        ["Subtitle"] = ("Record locally. Transcribe once. Paste anywhere.", "録音して、まとめて文字起こし。いつもの入力欄へ。"),
        ["GeneralTab"] = ("General", "基本設定"), ["AudioTab"] = ("Audio & silence", "音声と無音"), ["RecordingsTab"] = ("Recordings", "録音一覧"),
        ["LanguageLabel"] = ("Language", "言語"),
        ["LanguageHint"] = ("Controls the interface and the expected speech language. Transcription does not translate.", "画面表示と音声認識の言語を切り替えます。文字起こし時に翻訳はしません。"),
        ["ApiLabel"] = ("OpenAI API key", "OpenAI APIキー"),
        ["ApiHint"] = ("Saved in config.ini next to the app. The key is hidden here.", "アプリと同じフォルダのconfig.iniに保存します。画面では伏せて表示します。"),
        ["MicLabel"] = ("Microphone", "マイク"), ["DefaultMic"] = ("Windows default microphone", "Windowsの既定のマイク"),
        ["ShortcutLabel"] = ("Recording shortcut", "録音ショートカット"),
        ["ShortcutHint"] = ("Press once to start, and press again to stop. The shortcut works in other apps.", "１回押すと録音を開始し、もう１回押すと終了します。他のアプリを開いたまま使えます。"),
        ["SetShortcut"] = ("Set shortcut", "ショートカットを設定"), ["PressShortcut"] = ("Press a key combination…", "キーの組み合わせを押してください…"),
        ["DimLabel"] = ("Dim the screen while recording", "録音中は画面全体を暗くする"),
        ["DimHint"] = ("The screen fades in and out; you can still use the apps underneath.", "録音時にふわっと暗くなり、終了時に戻ります。背後の画面は操作できます。"),
        ["SoundLabel"] = ("Play start and stop sounds", "録音の開始・終了時に音を鳴らす"),
        ["TriggerLabel"] = ("Spoken trigger words", "整形のトリガーワード"),
        ["TriggerHint"] = ("Add a phrase and press Enter. A phrase at the end of a recording starts cleanup.", "言葉を入力して追加します。話の最後に言うと文章を整えます。"),
        ["AddTrigger"] = ("Add", "追加"), ["RemoveTrigger"] = ("Remove", "削除"),
        ["ClearKey"] = ("Clear API key", "APIキーを消去"), ["RefreshModels"] = ("Refresh models", "モデル更新"),
        ["ModelsLoading"] = ("Loading models…", "モデルを取得中…"), ["ModelsLoaded"] = ("Model list updated.", "モデル一覧を更新しました。"),
        ["ModelsFailed"] = ("Could not load models.", "モデルを取得できませんでした。"),
        ["PreviewTitle"] = ("Microphone preview", "マイクの確認"),
        ["PreviewLegend"] = ("Green: sound  ·  Gray: retained  ·  Red: shortened (preview)", "緑：音声　灰：残す無音　赤：短縮する無音（目安）"),
        ["PreviewUnavailable"] = ("Microphone preview is unavailable.", "マイクを確認できません。"),
        ["PreviewBusy"] = ("Preview pauses while recording or measuring.", "録音中や測定中はプレビューを停止します。"),
        ["PromptLabel"] = ("Cleanup instructions", "文章を整えるための指示"),
        ["PromptHint"] = ("The transcript and final trigger are sent to the text model with these instructions.", "文字起こしと最後の合図を、この指示と一緒にAIへ渡します。"),
        ["ModelsLabel"] = ("OpenAI models", "OpenAIのモデル"), ["TranscriptionModel"] = ("Transcription", "文字起こし"), ["CleanupModel"] = ("Cleanup", "文章の整形"),
        ["TrimLabel"] = ("Shorten long quiet intervals before sending", "送信前に長い無音部分を短くする"),
        ["ThresholdLabel"] = ("Silence threshold (dB)", "無音とみなす音量（dB）"),
        ["MinimumLabel"] = ("Minimum quiet duration (seconds)", "短くする無音の長さ（秒以上）"),
        ["GapLabel"] = ("Gap to keep (seconds)", "残す間隔（秒）"),
        ["MarginLabel"] = ("Margin above background (dB)", "環境音より上に取る余裕（dB）"),
        ["Calibrate"] = ("Measure background noise", "環境音を測定"),
        ["CalibrateHint"] = ("Stay quiet for 3 seconds. The measured level plus the margin becomes the suggested threshold.", "３秒間、話さずに測定します。環境音に余裕を足した値を基準にします。"),
        ["CalibrationRunning"] = ("Measuring… please stay quiet.", "測定中です。話さずにお待ちください。"),
        ["Save"] = ("Save settings", "設定を保存"), ["Close"] = ("Close to tray", "通知領域に戻る"),
        ["ResetTab"] = ("Reset this tab", "このタブを初期値に戻す"), ["ResetPending"] = ("Defaults restored. Save to apply.", "初期値に戻しました。保存で確定します。"),
        ["RecordNow"] = ("Start recording", "録音を開始"), ["StopNow"] = ("Stop recording", "録音を終了"),
        ["Refresh"] = ("Refresh", "更新"), ["OpenFolder"] = ("Open folder", "フォルダを開く"),
        ["Retry"] = ("Transcribe again", "再送信"), ["Copy"] = ("Copy text", "文字をコピー"), ["Play"] = ("Play", "再生"), ["Pause"] = ("Pause", "一時停止"), ["Stop"] = ("Stop", "停止"), ["Rewind"] = ("Rewind", "先頭へ"), ["Delete"] = ("Delete", "削除"),
        ["RecordingsHint"] = ("Original audio is kept until you delete it. A missing file disappears from this list.", "録音の原本は手動で削除するまで残ります。ファイルがなくなると一覧から消えます。"),
        ["CreatedColumn"] = ("Recorded", "録音日時"), ["DurationColumn"] = ("Length", "長さ"), ["StatusColumn"] = ("Status", "状態"), ["FileColumn"] = ("File", "ファイル"),
        ["ResultLabel"] = ("Transcript / details", "文字起こし・詳細"), ["NoSelection"] = ("Select a recording to see its text and details.", "録音を選択すると、文字や詳細を表示します。"),
        ["Ready"] = ("Ready", "待機中"), ["Recording"] = ("Recording", "録音中"), ["Processing"] = ("Transcribing…", "文字起こし中…"),
        ["Pending"] = ("Pending", "未処理"), ["Complete"] = ("Complete", "完了"), ["Failed"] = ("Failed", "失敗"),
        ["Interrupted"] = ("Interrupted", "中断"), ["NoSpeech"] = ("No text", "文字なし"),
        ["SettingsMenu"] = ("Settings", "設定"), ["QuitMenu"] = ("Quit", "終了"),
        ["CopiedOnly"] = ("Copied to clipboard. Paste it into your chosen app.", "クリップボードにコピーしました。入力したい場所へ貼り付けてください。"),
        ["Pasted"] = ("Transcription copied and pasted.", "文字をコピーして貼り付けました。"),
        ["NoTextMessage"] = ("No speech text was returned. The original recording was kept.", "文字はありませんでした。録音の原本は保存しています。"),
        ["BusyMessage"] = ("Please wait until the current recording is processed.", "現在の録音の処理が終わるまでお待ちください。"),
        ["HotkeyUnavailable"] = ("That shortcut is already in use. Choose another one.", "そのショートカットは使用中です。別のキーを選んでください。"),
        ["MissingKey"] = ("Set your OpenAI API key in Settings first.", "先に設定画面でOpenAI APIキーを入力してください。"),
        ["SaveFailed"] = ("Could not save settings. Check the app folder permissions.", "設定を保存できませんでした。アプリのフォルダに書き込めるか確認してください。"),
        ["DeleteConfirm"] = ("Permanently delete this recording and its transcript?", "この録音と文字起こしを完全に削除しますか？"),
        ["DeleteTitle"] = ("Delete recording", "録音を削除"),
        ["ErrorTitle"] = ("OneTalk Text", "OneTalk Text"),
        ["RetryStatus"] = ("Preparing transcription…", "文字起こしを準備中…"),
        ["SettingsSaved"] = ("Settings saved.", "設定を保存しました。"),
        ["InvalidNumber"] = ("Check the numeric audio settings.", "音声設定の数値を確認してください。"),
        ["IncreaseValue"] = ("Increase value", "数値を増やす"), ["DecreaseValue"] = ("Decrease value", "数値を減らす"),
        ["MissingMicrophone"] = ("The selected microphone is not available.", "選択したマイクが見つかりません。"),
        ["RecordingError"] = ("Recording stopped unexpectedly. The saved audio has been kept.", "録音が途中で停止しました。保存できた音声は残しています。"),
        ["ApiError"] = ("Transcription failed. The recording is kept; retry it from Recordings.", "文字起こしに失敗しました。録音は残っています。一覧から再送できます。"),
        ["RecoverMessage"] = ("An interrupted recording was recovered and is available in Recordings.", "中断された録音を復元しました。録音一覧から確認できます。")
    };

    public static string Text(string key, string language) => Terms.TryGetValue(key, out var value) ? (language == "ja" ? value.Ja : value.En) : key;
    public static void Apply(string language)
    {
        foreach (var key in Terms.Keys) System.Windows.Application.Current.Resources[key] = Text(key, language);
    }
    public static string Status(RecordingStatus status, string language) => Text(status.ToString(), language);
}
