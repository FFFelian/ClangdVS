using System;
using System.IO;

namespace ClangdVS
{
    /// <summary>
    /// Simple logger: writes to %LOCALAPPDATA%\ClangdVS\clangd.log and
    /// System.Diagnostics.Debug. Kept dependency-free so the MEF language
    /// client can log from any thread.
    /// </summary>
    internal static class ClangdLog
    {
        private static readonly object Gate = new object();
        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        private const long MaxLogSizeBytes = 5L * 1024 * 1024;
        private static string LogDirectory =>
            Environment.GetEnvironmentVariable("CLANGDVS_LOG_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClangdVS");

        private static string LogFile => Path.Combine(LogDirectory,
#if CLANGD_LSP
                                                      // The OOP LSP-provider extension logs to its own file to avoid
                                                      // concurrent writes from a second process.
                                                      "clangd-lsp.log");
#else
                                                      "clangd.log");
#endif

        public static void WriteLine(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={ProcessId}] {message}";
            System.Diagnostics.Debug.WriteLine(line);
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    RotateIfNeeded();
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never throw.
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var info = new FileInfo(LogFile);
                if (!info.Exists || info.Length < MaxLogSizeBytes)
                {
                    return;
                }

                var backup = LogFile + ".1";
                try
                {
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                }
                catch
                {
                    // Best effort: if the old backup cannot be cleared, skip rotation.
                    return;
                }

                File.Move(LogFile, backup);
            }
            catch
            {
                // Rotation must never break logging.
            }
        }
    }
}
