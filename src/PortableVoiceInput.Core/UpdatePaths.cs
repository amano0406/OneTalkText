using System.Security.Cryptography;
using System.Text;

namespace PortableVoiceInput.Core;

/// <summary>All update files are unique, app-owned files under the portable working folder.</summary>
public sealed class UpdatePaths
{
    public string AppFolder { get; }
    public string WorkingFolder { get; }
    public string TargetFileName { get; }
    public string TargetExecutable { get; }
    public string Candidate { get; }
    public string Backup { get; }
    public string FailedCandidate { get; }
    public string HelperExecutable { get; }
    public Guid Id { get; }

    public UpdatePaths(string appFolder, string targetFileName, Guid id)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(targetFileName) ||
            targetFileName != Path.GetFileName(targetFileName) || !targetFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid update target.");
        AppFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appFolder));
        WorkingFolder = Path.Combine(AppFolder, "working");
        TargetFileName = targetFileName; Id = id;
        TargetExecutable = Path.Combine(AppFolder, targetFileName);
        var prefix = "onetalk-update-" + id.ToString("N");
        Candidate = Path.Combine(WorkingFolder, prefix + ".download");
        Backup = Path.Combine(WorkingFolder, prefix + ".backup");
        FailedCandidate = Path.Combine(WorkingFolder, prefix + ".failed");
        HelperExecutable = Path.Combine(WorkingFolder, prefix + "-helper.exe");
    }

    public static string SignalName(Guid id, string suffix) => "Local\\OneTalkText-Update-" + id.ToString("N") + "-" + suffix;
    public static string InstanceMutexName(string appFolder)
    {
        var folder = Path.GetFullPath(appFolder);
        if (!Path.EndsInDirectorySeparator(folder)) folder += Path.DirectorySeparatorChar;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..16];
        return "Local\\PortableVoiceInput-" + id;
    }

    public static bool IsSamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
