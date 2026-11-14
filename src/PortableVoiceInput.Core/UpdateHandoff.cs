using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace PortableVoiceInput.Core;

public sealed record UpdateInvocation(UpdatePaths Paths, int ParentId, string Sha256, long Size, string Language, bool OpenSettings);
public enum UpdateWorkerOutcome { Applied, RolledBack, Canceled, Unconfirmed, Failed }

/// <summary>Hands replacement to a temporary copy of the app after the original process exits.</summary>
[SupportedOSPlatform("windows")]
public static class UpdateHandoff
{
    public const string HelperSwitch = "--apply-update";
    public const string CompletedSwitch = "--update-complete";
    public const string SkipSwitch = "--skip-update-once";
    public const string FailedSwitch = "--update-failed";

    public static async Task LaunchAsync(UpdatePaths paths, UpdateRelease release, string language, bool openSettings, CancellationToken cancellationToken)
    {
        await Task.Run(() => Launch(paths, release, language, openSettings, cancellationToken), cancellationToken);
    }

    private static void Launch(UpdatePaths paths, UpdateRelease release, string language, bool openSettings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(paths.HelperExecutable) || File.Exists(paths.Backup) || File.Exists(paths.FailedCandidate))
            throw new IOException("An update work file already exists.");
        var helperCreated = false;
        using var started = NewSignal(paths.Id, "started");
        using var canceled = NewSignal(paths.Id, "cancel");
        try
        {
            using (var input = new FileStream(paths.TargetExecutable, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(paths.HelperExecutable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                helperCreated = true;
                input.CopyTo(output); output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var info = HiddenProcess(paths.HelperExecutable, paths.AppFolder);
            info.ArgumentList.Add(HelperSwitch);
            info.ArgumentList.Add(paths.Id.ToString("N"));
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add(release.Sha256);
            info.ArgumentList.Add(release.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add(paths.TargetFileName);
            info.ArgumentList.Add(language == "ja" ? "ja" : "en");
            info.ArgumentList.Add(openSettings ? "1" : "0");
            using var helper = Process.Start(info) ?? throw new IOException("The update helper did not start.");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (started.WaitOne(100)) return;
                if (helper.HasExited) throw new IOException("The update helper exited before starting.");
            }
            throw new TimeoutException("The update helper did not become ready.");
        }
        catch
        {
            canceled.Set();
            TryDelete(paths.Candidate);
            if (helperCreated) TryDelete(paths.HelperExecutable);
            throw;
        }
    }

    public static bool TryParseHelper(string[] args, string? helperExecutable, out UpdateInvocation? invocation)
    {
        invocation = null;
        try
        {
            if (args.Length != 8 || args[0] != HelperSwitch || !Guid.TryParseExact(args[1], "N", out var id) || id == Guid.Empty ||
                !int.TryParse(args[2], out var parentId) || parentId <= 0 || args[3].Length != 64 ||
                !args[3].All(Uri.IsHexDigit) || !long.TryParse(args[4], out var size) || size <= 0 || size > 512L * 1024 * 1024 ||
                args[6] is not ("en" or "ja") || args[7] is not ("0" or "1") || string.IsNullOrWhiteSpace(helperExecutable)) return false;
            var working = Path.GetDirectoryName(Path.GetFullPath(helperExecutable));
            if (working is null || !string.Equals(Path.GetFileName(working), "working", StringComparison.OrdinalIgnoreCase)) return false;
            var appFolder = Directory.GetParent(working)?.FullName;
            if (appFolder is null) return false;
            var paths = new UpdatePaths(appFolder, args[5], id);
            if (!UpdatePaths.IsSamePath(paths.HelperExecutable, helperExecutable)) return false;
            invocation = new UpdateInvocation(paths, parentId, args[3].ToLowerInvariant(), size, args[6], args[7] == "1");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public static UpdateWorkerOutcome Run(UpdateInvocation invocation, Action<string, Exception?>? log = null)
    {
        var paths = invocation.Paths;
        var replaced = false; var backupCreated = false; var replaceAttempted = false; var parentExited = false;
        Mutex? gate = null; var gateOwned = false;
        try
        {
            using var started = EventWaitHandle.OpenExisting(UpdatePaths.SignalName(paths.Id, "started"));
            using var canceled = EventWaitHandle.OpenExisting(UpdatePaths.SignalName(paths.Id, "cancel"));
            using var parent = Process.GetProcessById(invocation.ParentId);
            var parentPath = parent.MainModule?.FileName;
            if (parentPath is null || !UpdatePaths.IsSamePath(parentPath, paths.TargetExecutable))
                throw new InvalidOperationException("The update parent is not the target app.");
            started.Set();
            if (!parent.WaitForExit(30000)) return UpdateWorkerOutcome.Canceled;
            parentExited = true;
            if (canceled.WaitOne(0)) return UpdateWorkerOutcome.Canceled;

            gate = new Mutex(false, UpdatePaths.InstanceMutexName(paths.AppFolder));
            try { gateOwned = gate.WaitOne(0); } catch (AbandonedMutexException) { gateOwned = true; }
            if (!gateOwned) return UpdateWorkerOutcome.Canceled;
            VerifyCandidate(paths.Candidate, invocation.Size, invocation.Sha256);
            using (var input = new FileStream(paths.TargetExecutable, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(paths.Backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                backupCreated = true;
                input.CopyTo(output); output.Flush(true);
            }
            replaceAttempted = true;
            File.Replace(paths.Candidate, paths.TargetExecutable, null, true);
            replaced = true;
            gate.ReleaseMutex(); gateOwned = false;
            gate.Dispose(); gate = null;

            using var ready = NewSignal(paths.Id, "ready");
            using var child = StartTarget(paths, CompletedSwitch, invocation.OpenSettings);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (ready.WaitOne(200)) return UpdateWorkerOutcome.Applied;
                if (child.HasExited)
                {
                    if (ready.WaitOne(0)) return UpdateWorkerOutcome.Applied;
                    throw new InvalidOperationException("The updated app exited before it was ready.");
                }
            }
            // Never terminate or replace an app that is still running. Keep the backup.
            log?.Invoke("Updated app did not report readiness", null);
            return UpdateWorkerOutcome.Unconfirmed;
        }
        catch (Exception ex)
        {
            log?.Invoke("Automatic update failed", ex);
            if (!parentExited) return UpdateWorkerOutcome.Failed;
            try
            {
                if (replaced && !gateOwned)
                {
                    gate ??= new Mutex(false, UpdatePaths.InstanceMutexName(paths.AppFolder));
                    try { gateOwned = gate.WaitOne(0); } catch (AbandonedMutexException) { gateOwned = true; }
                    if (!gateOwned) return UpdateWorkerOutcome.Unconfirmed;
                }
                if (backupCreated && replaceAttempted && (replaced || !MatchesBackup(paths)))
                {
                    RestoreBackup(paths);
                    replaced = false;
                }
                if (gateOwned) { gate!.ReleaseMutex(); gateOwned = false; }
                gate?.Dispose(); gate = null;
                if (File.Exists(paths.TargetExecutable))
                {
                    using var old = StartTarget(paths, FailedSwitch, invocation.OpenSettings);
                    return UpdateWorkerOutcome.RolledBack;
                }
            }
            catch (Exception restoreError) { log?.Invoke("Restoring the previous app failed", restoreError); }
            return UpdateWorkerOutcome.Failed;
        }
        finally
        {
            if (gateOwned) { try { gate?.ReleaseMutex(); } catch (ApplicationException) { } }
            gate?.Dispose();
        }
    }

    public static void SignalReady(Guid id)
    {
        try { using var ready = EventWaitHandle.OpenExisting(UpdatePaths.SignalName(id, "ready")); ready.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    public static void ScheduleCleanup(UpdatePaths paths)
    {
        _ = Task.Run(async () =>
        {
            // The helper cannot delete its own executable until it exits.
            for (var attempt = 0; attempt < 120; attempt++)
            {
                TryDelete(paths.HelperExecutable);
                if (!File.Exists(paths.HelperExecutable))
                {
                    TryDelete(paths.Candidate); TryDelete(paths.Backup); TryDelete(paths.FailedCandidate);
                    return;
                }
                await Task.Delay(500);
            }
        });
    }

    private static void VerifyCandidate(string path, long expectedSize, string expectedSha256)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != expectedSize || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("The update no longer matches the verified download.");
    }

    private static void RestoreBackup(UpdatePaths paths)
    {
        if (!File.Exists(paths.Backup)) throw new FileNotFoundException("The previous executable backup is unavailable.");
        if (File.Exists(paths.TargetExecutable)) File.Replace(paths.Backup, paths.TargetExecutable, null, true);
        else File.Move(paths.Backup, paths.TargetExecutable);
    }

    private static bool MatchesBackup(UpdatePaths paths)
    {
        if (!File.Exists(paths.TargetExecutable)) return false;
        using var target = File.OpenRead(paths.TargetExecutable);
        using var backup = File.OpenRead(paths.Backup);
        return target.Length == backup.Length && CryptographicOperations.FixedTimeEquals(SHA256.HashData(target), SHA256.HashData(backup));
    }

    private static Process StartTarget(UpdatePaths paths, string mode, bool openSettings)
    {
        var info = HiddenProcess(paths.TargetExecutable, paths.AppFolder);
        if (openSettings) info.WindowStyle = ProcessWindowStyle.Normal;
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(paths.Id.ToString("N"));
        if (mode == FailedSwitch) info.ArgumentList.Add(SkipSwitch);
        if (openSettings) info.ArgumentList.Add("--settings");
        return Process.Start(info) ?? throw new IOException("The app could not be started after the update.");
    }

    private static ProcessStartInfo HiddenProcess(string executable, string workingDirectory) =>
        new() { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };

    private static EventWaitHandle NewSignal(Guid id, string suffix)
    {
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset, UpdatePaths.SignalName(id, suffix), out var created);
        if (created) return signal;
        signal.Dispose(); throw new IOException("An update signal already exists.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
