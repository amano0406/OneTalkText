# VoicePaste

話した内容を録音し、話し終えてからまとめて文字起こし・貼り付けする Windows 向けの音声入力アプリです。OpenAI API を使用します。

[最新版をダウンロード](https://github.com/amano0406/VoicePaste/releases/latest) · [English](README.en.md)

![VoicePaste の設定画面](docs/settings-ja.png)

## できること

- 通知領域に常駐し、ショートカットを1回押して録音、もう1回押して終了します。
- 録音中は波形と時間、終了後は処理中の状態を表示します。画面の暗転や開始・終了音は設定できます。
- 文字起こしがすべて終わってから、結果をクリップボードにコピーし、その時点でフォーカスされているアプリに貼り付けます。
- 話の最後に設定済みのトリガーワードを言うと、設定した指示に従って文章を整えます。
- 元の録音はローカルに残ります。通信エラーなどが起きた場合は自動で再試行し、録音一覧から後で再送信することもできます。
- 長い無音を送信前に短くできます。マイクの選択、音量の基準、無音の長さ、残す間隔を調整できます。
- 英語・日本語の表示と音声認識に対応しています。

## 動作環境

- Windows x64（Windows 11、または [.NET 10 が対応する Windows 10](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions)）
- マイク、インターネット接続、利用可能な OpenAI API キー
- 配布版には .NET ランタイムが含まれるため、別途インストールする必要はありません。
- 単体 EXE の実行時には、ランタイムの一部が Windows の一時フォルダに展開されます。設定と録音はアプリと同じフォルダに保存されます。

## はじめかた

1. [Releases](https://github.com/amano0406/VoicePaste/releases/latest) から `VoicePaste-windows-x64.zip` をダウンロードして展開します。`VoicePaste.exe` 単体もダウンロードできます。
2. 書き込み可能なフォルダに置き、`VoicePaste.exe` を起動します。通知領域のアイコンを右クリックして **Settings** を選ぶか、実行ファイルをもう一度開くと設定画面が表示されます。
3. 日本語で使う場合は **Language** で **日本語** を選びます。**OpenAI APIキー** を入力し、必要に応じてマイクやショートカットを変更して **設定を保存** を押します。
4. 入力したい欄を選び、既定の **Ctrl + Shift + Space** で録音を開始します。話し終えたら同じキーをもう一度押してください。

設定画面を閉じても、アプリは通知領域で待機します。終了するときは、通知領域のアイコンを右クリックして **終了** を選びます。

## 録音とデータの扱い

- 録音はアプリと同じフォルダの `recordings` に WAV ファイルとして保存し、手動で削除するまで残します。文字起こし結果などの状態も、その録音ファイル内に保存します。
- 無音の短縮は送信用の音声に行い、元の音声データはそのまま残します。小さな声が無音と判定された場合は、無音の基準を調整して再送信できます。
- 入力された API キーは、アプリと同じフォルダの `config.ini` に**平文で保存**されます。フォルダを共有する際は、キーや録音を含めないようにしてください。
- 文字起こしでは音声が OpenAI に送信されます。トリガーワードによる文章整形を使う場合は、文字起こしと整形の指示も OpenAI に送信されます。
- OpenAI API の利用には料金が発生する場合があります。[OpenAI API の料金](https://developers.openai.com/api/docs/pricing)をご確認ください。

## ソースからビルド

Windows の [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/windows) を使用します。`VoicePaste.sln` は Visual Studio でも開けます。

```powershell
dotnet build VoicePaste.sln -c Release
dotnet test tests/PortableVoiceInput.Tests/PortableVoiceInput.Tests.csproj -c Release
```

配布用の単体 EXE を作る場合:

```powershell
dotnet publish src/PortableVoiceInput/PortableVoiceInput.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

`test-audio` にはテスト用の合成音声を含みます。通常のテストは実際の API キーを必要としません。

依存ライブラリのライセンスは [third-party-licenses](third-party-licenses/) にあります。
