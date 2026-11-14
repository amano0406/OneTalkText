using System.Runtime.Versioning;
using PortableVoiceInput.Core;

if (!OperatingSystem.IsWindows()) return 1;
return await Fixture.RunAsync(args);

[SupportedOSPlatform("windows")]
internal static class Fixture
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == UpdateHandoff.HelperSwitch)
        {
            if (!UpdateHandoff.TryParseHelper(args, Environment.ProcessPath, out var invocation) || invocation is null) return 4;
            var outcome = UpdateHandoff.Run(invocation, (operation, error) =>
                File.AppendAllText(Path.Combine(invocation.Paths.AppFolder, "fixture-log.txt"), operation + " " + error?.GetType().Name + Environment.NewLine));
            return outcome is UpdateWorkerOutcome.Failed or UpdateWorkerOutcome.Unconfirmed ? 5 : 0;
        }
        var root = AppContext.BaseDirectory;
        var targetName = Path.GetFileName(Environment.ProcessPath!);
        using var mutex = new Mutex(true, UpdatePaths.InstanceMutexName(root), out var created);
        if (!created) return 6;
        if (args.Length >= 2 && Guid.TryParseExact(args[1], "N", out var id))
        {
            var paths = new UpdatePaths(root, targetName, id);
            if (args[0] == UpdateHandoff.CompletedSwitch)
            {
#if NEW_FIXTURE
                if (Environment.GetEnvironmentVariable("ONETALK_FIXTURE_FAIL") == "1") return 42;
                UpdateHandoff.SignalReady(id);
                File.WriteAllText(Path.Combine(root, "new-started.ok"), "updated");
                UpdateHandoff.ScheduleCleanup(paths);
                await Task.Delay(2500);
                return 0;
#else
                return 7;
#endif
            }
            if (args[0] == UpdateHandoff.FailedSwitch)
            {
#if OLD_FIXTURE
                File.WriteAllText(Path.Combine(root, "old-restored.ok"), "restored");
                UpdateHandoff.ScheduleCleanup(paths);
                await Task.Delay(2500);
                return 0;
#else
                return 8;
#endif
            }
            if (args[0] == "--fixture-wait")
            {
#if OLD_FIXTURE
                using var started = new EventWaitHandle(false, EventResetMode.ManualReset, UpdatePaths.SignalName(id, "started"));
                using var canceled = new EventWaitHandle(false, EventResetMode.ManualReset, UpdatePaths.SignalName(id, "cancel"));
                File.WriteAllText(Path.Combine(root, "parent-ready.ok"), "waiting");
                return started.WaitOne(TimeSpan.FromSeconds(20)) ? 0 : 11;
#else
                return 12;
#endif
            }
            if (args[0] == "--fixture-start" && args.Length == 4)
            {
#if OLD_FIXTURE
                var release = new UpdateRelease(new Version(1, 0, 1), "v1.0.1", new Uri("https://github.com/amano0406/OneTalkText/releases/download/v1.0.1/OneTalkText.exe"), long.Parse(args[3]), args[2]);
                await UpdateHandoff.LaunchAsync(paths, release, "en", false, CancellationToken.None);
                return 0;
#else
                return 9;
#endif
            }
        }
        return 10;
    }
}
