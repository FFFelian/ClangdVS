using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ClangdVS;

internal static class ProcessStreamRegression
{
    internal static async Task RunAsync()
    {
        await VerifyVersionQueryBounded();
        var type = typeof(ClangdLanguageClient);
        var drain = type.GetMethod("DrainStandardError", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(drain != null, "stderr drain helper is missing");
        var executable = Assembly.GetExecutingAssembly().Location;
        var info = new ProcessStartInfo {
            FileName = executable, Arguments = "stderr-flood 2000000", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true,       RedirectStandardOutput = true,
        };
        using (var child = Process.Start(info))
        {
            var logDirectory = Environment.GetEnvironmentVariable("CLANGDVS_LOG_DIRECTORY");
            var logFile = Path.Combine(logDirectory, "clangd.log");
            var before = File.Exists(logFile) ? File.ReadAllText(logFile) : string.Empty;
            var drainTask = (Task)drain.Invoke(null, new object[] { child.StandardError, false });
            var exitTask = Task.Run(() => child.WaitForExit());
            Assert(await Task.WhenAny(exitTask, Task.Delay(10000)) == exitTask,
                   "child blocked writing stderr when logging was disabled");
            await drainTask;
            Assert(child.ExitCode == 0, "stderr flood child failed");
            var after = File.Exists(logFile) ? File.ReadAllText(logFile) : string.Empty;
            Assert(after.Length == before.Length, "stderr was logged while logging was disabled");
        }
        Console.WriteLine("PASS: stderr is continuously drained without logging when disabled");
    }

    private static async Task VerifyVersionQueryBounded()
    {
        var locator = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdLocator");
        var query = locator?.GetMethod("QueryVersion", BindingFlags.Static | BindingFlags.Public);
        Assert(query != null, "production version-query method is missing");
        var exe = Assembly.GetExecutingAssembly().Location;
        Environment.SetEnvironmentVariable("CLANGDVS_VERSION_TEST_MODE", "flood");
        var floodPidFile =
            Path.Combine(Path.GetTempPath(), "ClangdVS-VersionQuery-" + Guid.NewGuid().ToString("N") + ".txt");
        Environment.SetEnvironmentVariable("CLANGDVS_VERSION_TEST_PID_FILE", floodPidFile);
        var flood = Task.Run(() => (string)query.Invoke(null, new object[] { exe }));
        Assert(await Task.WhenAny(flood, Task.Delay(15000)) == flood, "version output flood was not drained");
        var output = await flood;
        Assert(output != null && output.Length >= 2000000, "production version query lost flooded stdout");
        Assert(File.Exists(floodPidFile), "flood child did not start");
        var floodPid = int.Parse(File.ReadAllText(floodPidFile));
        var floodExited = false;
        try
        {
            using (Process.GetProcessById(floodPid))
            {
            }
        }
        catch (ArgumentException)
        {
            floodExited = true;
        }
        File.Delete(floodPidFile);
        Assert(floodExited, "version query returned while child remained blocked on stderr");

        Environment.SetEnvironmentVariable("CLANGDVS_VERSION_TEST_MODE", "hang");
        var pidFile =
            Path.Combine(Path.GetTempPath(), "ClangdVS-VersionQuery-" + Guid.NewGuid().ToString("N") + ".txt");
        Environment.SetEnvironmentVariable("CLANGDVS_VERSION_TEST_PID_FILE", pidFile);
        var started = DateTime.UtcNow;
        var timedOut = Task.Run(() => query.Invoke(null, new object[] { exe }));
        Assert(await Task.WhenAny(timedOut, Task.Delay(8000)) == timedOut, "version query exceeded its 5 second bound");
        Assert(await timedOut == null, "timed-out production version query returned a version");
        Assert(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "version query did not return near its timeout");
        Assert(File.Exists(pidFile), "hanging child did not start");
        var childPid = int.Parse(File.ReadAllText(pidFile));
        var childExited = false;
        try
        {
            using (Process.GetProcessById(childPid))
            {
            }
        }
        catch (ArgumentException)
        {
            childExited = true;
        }
        File.Delete(pidFile);
        Assert(childExited, "timed-out version query left its child running");
        Environment.SetEnvironmentVariable("CLANGDVS_VERSION_TEST_PID_FILE", null);
        Environment.SetEnvironmentVariable("CLANGDVS_VERSION_TEST_MODE", null);
        Console.WriteLine("PASS: version output drains stdout/stderr and terminates hung child at timeout");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
