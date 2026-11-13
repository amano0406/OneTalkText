using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortableVoiceInput.Core;

public enum RecordingStatus { Pending, Processing, Complete, Failed, Interrupted, NoSpeech }

public sealed class RecordingMetadata
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public RecordingStatus Status { get; set; } = RecordingStatus.Pending;
    public string Language { get; set; } = "en";
    public string Transcript { get; set; } = "";
    public string FinalText { get; set; } = "";
    public string Error { get; set; } = "";
    public string ProcessingSignature { get; set; } = "";
    public List<string> CompletedChunks { get; set; } = [];
    public double OriginalSeconds { get; set; }
    public double SentSeconds { get; set; }
    public bool Refined { get; set; }
    public bool? CalibrationReliable { get; set; }
    public double? CalibratedNoiseDb { get; set; }
    public double? CalibratedThresholdDb { get; set; }
    public double ProtectedStartSeconds { get; set; }
}

public sealed record WaveInfo(int SampleRate, short Channels, short BitsPerSample, long DataOffset, long DataLength)
{
    public double DurationSeconds => DataLength / (double)(SampleRate * Channels * (BitsPerSample / 8));
}

/// <summary>
/// Stores metadata in a custom, standards-compliant RIFF chunk following PCM data.
/// Each append is a checkpoint; an incomplete trailing checkpoint never changes the audio.
/// </summary>
public static class WaveMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private const string MetadataId = "pvmd";

    public static (WaveInfo Info, RecordingMetadata Metadata) Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(file);
        if (file.Length < 44 || ReadId(reader) != "RIFF") throw new InvalidDataException("Not a RIFF recording.");
        _ = reader.ReadUInt32();
        if (ReadId(reader) != "WAVE") throw new InvalidDataException("Not a WAVE recording.");
        int rate = 0; short channels = 0, bits = 0; long dataOffset = -1, dataLength = 0;
        RecordingMetadata? latest = null;
        while (file.Position + 8 <= file.Length)
        {
            var id = ReadId(reader);
            var size = reader.ReadUInt32();
            var start = file.Position;
            if (start + size > file.Length) break; // interrupted append: earlier chunks remain valid
            if (id == "fmt " && size >= 16)
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                _ = reader.ReadInt32(); _ = reader.ReadInt16(); bits = reader.ReadInt16();
                if (format != 1) throw new InvalidDataException("Only PCM recordings are supported.");
            }
            else if (id == "data" && dataOffset < 0) { dataOffset = start; dataLength = size; }
            else if (id == MetadataId && size is > 0 and <= 16 * 1024 * 1024)
            {
                try { latest = JsonSerializer.Deserialize<RecordingMetadata>(reader.ReadBytes((int)size), JsonOptions) ?? latest; }
                catch (JsonException) { /* retain the last complete checkpoint */ }
            }
            file.Position = start + size + (size % 2);
        }
        if (dataOffset < 0 || rate <= 0 || channels <= 0 || bits <= 0) throw new InvalidDataException("Recording has no usable audio.");
        var info = new WaveInfo(rate, channels, bits, dataOffset, dataLength);
        latest ??= new RecordingMetadata { CreatedAt = File.GetCreationTimeUtc(path), OriginalSeconds = info.DurationSeconds };
        return (info, latest);
    }

    public static void Write(string path, RecordingMetadata metadata)
    {
        metadata.UpdatedAt = DateTimeOffset.Now;
        var json = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("Recording metadata is too large.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var reader = new BinaryReader(file, System.Text.Encoding.ASCII, leaveOpen: true);
        using var writer = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
        if (file.Length < 44 || ReadId(reader) != "RIFF") throw new InvalidDataException("Not a RIFF recording.");
        _ = reader.ReadUInt32();
        if (ReadId(reader) != "WAVE") throw new InvalidDataException("Not a WAVE recording.");
        long validEnd = 12; bool foundData = false;
        while (file.Position + 8 <= file.Length)
        {
            var id = ReadId(reader); var size = reader.ReadUInt32(); var start = file.Position;
            if (start + size > file.Length)
            {
                if (id != MetadataId || !foundData) throw new InvalidDataException("The audio file is incomplete.");
                break;
            }
            if (id == "data") foundData = true;
            validEnd = start + size + (size % 2);
            file.Position = validEnd;
        }
        if (!foundData) throw new InvalidDataException("The audio file has no data chunk.");
        // Only remove an incomplete tail from a previous metadata append, never recorded audio.
        if (validEnd < file.Length) file.SetLength(validEnd);
        file.Position = validEnd;
        WriteId(writer, MetadataId);
        writer.Write((uint)json.Length);
        writer.Write(json);
        if ((json.Length & 1) != 0) writer.Write((byte)0);
        writer.Flush();
        if (file.Length - 8 > uint.MaxValue) throw new InvalidDataException("Recording exceeds the WAV size limit.");
        file.Position = 4;
        writer.Write((uint)(file.Length - 8));
        writer.Flush();
        file.Flush(true);
    }

    internal static string ReadId(BinaryReader reader) => new(reader.ReadChars(4));
    internal static void WriteId(BinaryWriter writer, string id) => writer.Write(System.Text.Encoding.ASCII.GetBytes(id));
}

/// <summary>Incrementally writes recoverable mono PCM. Header is checkpointed during capture.</summary>
public sealed class DurableWaveWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly BinaryWriter _writer;
    private readonly int _sampleRate;
    private bool _disposed;
    public long AudioBytes { get; private set; }

    public DurableWaveWriter(string path, int sampleRate = 16000)
    {
        _sampleRate = sampleRate;
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536);
        _writer = new BinaryWriter(_file, System.Text.Encoding.ASCII, leaveOpen: true);
        WaveMetadata.WriteId(_writer, "RIFF"); _writer.Write((uint)36); WaveMetadata.WriteId(_writer, "WAVE");
        WaveMetadata.WriteId(_writer, "fmt "); _writer.Write((uint)16); _writer.Write((short)1); _writer.Write((short)1);
        _writer.Write(sampleRate); _writer.Write(sampleRate * 2); _writer.Write((short)2); _writer.Write((short)16);
        WaveMetadata.WriteId(_writer, "data"); _writer.Write((uint)0);
        _writer.Flush(); _file.Flush(true);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = data.Length - data.Length % 2;
        if (AudioBytes + count > uint.MaxValue - 36) throw new IOException("Recording exceeds the WAV size limit.");
        _file.Position = 44 + AudioBytes;
        _file.Write(data[..count]);
        AudioBytes += count;
    }

    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.Flush();
        _file.Position = 4; _writer.Write((uint)(36 + AudioBytes));
        _file.Position = 40; _writer.Write((uint)AudioBytes);
        _writer.Flush(); _file.Flush(true);
        _file.Position = 44 + AudioBytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Checkpoint(); }
        finally { _disposed = true; _writer.Dispose(); _file.Dispose(); }
    }

    public static void Recover(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var reader = new BinaryReader(file, System.Text.Encoding.ASCII, leaveOpen: true);
        using var writer = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
        if (file.Length < 44 || WaveMetadata.ReadId(reader) != "RIFF") throw new InvalidDataException("Not a recoverable recording.");
        file.Position = 8;
        if (WaveMetadata.ReadId(reader) != "WAVE") throw new InvalidDataException("Not a recoverable recording.");
        file.Position = 36;
        if (WaveMetadata.ReadId(reader) != "data") throw new InvalidDataException("Unexpected audio layout.");
        var bytes = (file.Length - 44) & ~1L;
        if (bytes > uint.MaxValue - 36) throw new InvalidDataException("Recording exceeds WAV size limit.");
        if (file.Length != 44 + bytes) file.SetLength(44 + bytes);
        file.Position = 4; writer.Write((uint)(36 + bytes));
        file.Position = 40; writer.Write((uint)bytes);
        writer.Flush(); file.Flush(true);
    }
}
