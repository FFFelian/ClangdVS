using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ClangdVS
{
    /// <summary>
    /// Resolves the clangd executable to launch. Resolution order mirrors
    /// vscode-clangd (settings path -> PATH), plus Windows/VS specific probes:
    /// the LLVM component shipped with VS ("%VCINSTALLDIR%\Tools\Llvm\x64\bin")
    /// and a standard LLVM installation ("C:\Program Files\LLVM\bin").
    /// </summary>
    internal static class ClangdLocator
    {
        public static string Resolve(out string failureReason)
        {
            var settings = ClangdSettings.Instance;
            failureReason = null;

            // 1. Explicit user-configured path.
            var configured = (settings.ClangdPath ?? "clangd").ExpandVariables();
            if (!string.IsNullOrWhiteSpace(configured) &&
                !configured.Equals("clangd", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(configured))
                {
                    return configured;
                }

                if (TryFindOnPath(configured, out var onPath))
                {
                    return onPath;
                }

                failureReason = $"Configured clangd path '{configured}' was not found.";
                return null;
            }

            // 2. PATH.
            if (TryFindOnPath("clangd.exe", out var fromPath))
            {
                return fromPath;
            }

            // 3. VS "C++ Clang tools for Windows" component (unmodified LLVM
            //    distribution installed to %VCINSTALLDIR%\Tools\Llvm\bin).
            var candidates = new List<string>();

            var vcInstallDir = Environment.GetEnvironmentVariable("VCINSTALLDIR");
            if (!string.IsNullOrEmpty(vcInstallDir))
            {
                candidates.Add(Path.Combine(vcInstallDir, @"Tools\Llvm\x64\bin\clangd.exe"));
                candidates.Add(Path.Combine(vcInstallDir, @"Tools\Llvm\bin\clangd.exe"));
            }

            var vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            if (!string.IsNullOrEmpty(vsInstallDir))
            {
                candidates.Add(Path.Combine(vsInstallDir, @"VC\Tools\Llvm\x64\bin\clangd.exe"));
                candidates.Add(Path.Combine(vsInstallDir, @"VC\Tools\Llvm\bin\clangd.exe"));
            }

            // 4. Standalone LLVM install.
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM",
                                        "bin", "clangd.exe"));

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            failureReason = "clangd was not found. Install the LLVM tools (e.g. the VS component " +
                            "'C++ Clang tools for Windows', or https://releases.llvm.org), or set the " +
                            "clangd path under Tools > Options > clangd.";
            return null;
        }

        private static bool TryFindOnPath(string executable, out string fullPath)
        {
            fullPath = null;
            var name =
                executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe";

            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathVariable.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        fullPath = candidate;
                        return true;
                    }
                }
                catch
                {
                    // Malformed PATH entry - skip.
                }
            }

            return false;
        }

        /// <summary>Returns the clangd version string, or null if the binary cannot be queried.</summary>
        public static string QueryVersion(string clangdPath)
        {
            try
            {
                var info = new ProcessStartInfo {
                    FileName = clangdPath, Arguments = "--version",       UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var process = Process.Start(info))
                {
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        process.WaitForExit(1000);
                        return null;
                    }

                    process.WaitForExit();
                    Task.WaitAll(outputTask, errorTask);
                    return outputTask.Result.Trim();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
