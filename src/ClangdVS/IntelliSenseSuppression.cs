using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ClangdVS
{
    /// <summary>
    /// Disables the built-in C/C++ IntelliSense engine so clangd can take
    /// over.
    ///
    /// The primary path reads and writes the C/C++ automation properties
    /// exposed by the Visual Studio DTE. These properties bridge to native
    /// C/C++ settings in current Visual Studio versions. A classic registry
    /// fallback is retained for older versions; it only changes values that
    /// are absent or DWORDs and records their original state for restoration.
    /// </summary>
    internal static class IntelliSenseSuppression
    {
        private const string RegistryValueWasMissing = "__CLANGDVS_REGISTRY_VALUE_MISSING__";
        private const string RegistryKeyWasMissing = "__CLANGDVS_REGISTRY_KEY_MISSING__";
        /// <summary>
        /// Bool props on the real VC automation page ("TextEditor / C/C++
        /// Specific", with a space) that bridge to the VS 2026 unified
        /// settings. Verified live: DisableIntelliSense is the master switch;
        /// DisableSquiggles / DisableErrorReporting /
        /// DisableIntelliSenseErrorsInErrorList kill squiggles (E0144) and
        /// the Error List entries.
        /// </summary>
        private static readonly string[] DteVcDisableFlags = {
            "DisableIntelliSense",
            "DisableSquiggles",
            "DisableErrorReporting",
            "DisableIntelliSenseErrorsInErrorList",
            "DisableIntelliSenseUpdating",
            // Background code analysis (VCR00xxx refactoring suggestions).
            "DisableCodeAnalysisInBackground",
            "DisableCodeAnalysisSquiggles",
            "DisableVCCodeAnalysisUX",
            // The VS 2026 code linter (VCR003 "can be made static" etc.):
            // EnableLinter on the DTE page maps to
            // languages.cpp.codeStyle.linter.enableCodeLinter.
            "EnableLinter_1",
            "EnableLinter",
            "EnableErrorFixSuggestions",
        };

        /// <summary>
        /// "*ErrorTagType" / "*SuggestionLevel" int props on the VC DTE page:
        /// 0 = None - silences every individual linter suggestion (VCR00x)
        /// even if the master switches miss one.
        /// </summary>
        private static bool IsLinterSeverityProperty(string name) =>
            name.EndsWith("ErrorTagType", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("SuggestionLevel", StringComparison.OrdinalIgnoreCase);

        private static bool RestoreProperty(Dictionary<string, string> backup, string key, EnvDTE.Property property)
        {
            if (backup == null || !backup.TryGetValue(key, out var stored) ||
                !TryGetPropValue(property, out var currentValue))
            {
                return false;
            }
            object restoredValue = null;
            if (currentValue is bool && bool.TryParse(stored, out var boolValue))
                restoredValue = boolValue;
            else if (currentValue is int && int.TryParse(stored, out var intValue))
                restoredValue = intValue;
            if (restoredValue == null)
                return false;
            try
            {
                property.Value = restoredValue;
                ClangdLog.WriteLine($"takeover: DTE {property.Name} restored to {restoredValue}");
                return true;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"takeover: DTE restore failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Classic registry flags (VS 2022 and older).</summary>
        private static readonly string[] DisableFlagNames = {
            "DisableIntelliSense",        "DisableIntelliSenseUpdating",   "DisableDatabase",
            "DisableDatabaseAutoUpdates", "DisableBackgroundCodeAnalysis", "DisableCodeAnalysisSquiggles",
            "DisableIntellisense",
        };

        private static string BackupFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClangdVS",
                         "intellisense-backup.json");

        // Multi-VS-instance isolation: backups are split per file by registryRoot (which contains the VS version hive),
        // avoiding instance A Apply, instance B Apply, then A's restore deleting the file and causing B to lose its backup.
        // The old single-file layout is still read for migration.
        private static string BackupFileFor(string registryRoot)
        {
            if (string.IsNullOrEmpty(registryRoot))
            {
                return BackupFile;
            }

            var sanitized = new System.Text.StringBuilder();
            foreach (var ch in registryRoot)
            {
                sanitized.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
            }

            var name = sanitized.ToString().Trim('-');
            if (name.Length > 64)
            {
                name = name.Substring(name.Length - 64);
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClangdVS",
                                $"intellisense-backup-{name}.json");
        }

        internal const int BackupMutexTimeoutMilliseconds = 500;

        private static System.IDisposable BackupMutex()
        {
            // Serializes backup reads/writes across VS processes. The Mutex name cannot contain '\', so flatten it once.
            // Failing to acquire the lock does not block takeover; at worst it falls back to lock-free reads/writes.
            // Explicit timeout bound: Apply/Revert each acquire the lock twice, so a single toggle blocks for at most about 1 second,
            // DTE reads/writes still happen on the main thread, and file backup reads/writes stay synchronous but degrade in a bounded way.
            var mutex = new System.Threading.Mutex(false, "Local\\ClangdVS-Takeover-Backup");
            try
            {
                mutex.WaitOne(BackupMutexTimeoutMilliseconds);
            }
            catch
            {
                // Timed out or was abandoned: continue on the lock-free path to avoid hanging the UI.
            }

            return new MutexReleaser(mutex);
        }

        private sealed class MutexReleaser : System.IDisposable
        {
            private System.Threading.Mutex _mutex;

            public MutexReleaser(System.Threading.Mutex mutex)
            {
                _mutex = mutex;
            }

            public void Dispose()
            {
                try
                {
                    _mutex?.ReleaseMutex();
                }
                catch
                {
                    // Not held or already abandoned: ignore.
                }

                _mutex?.Dispose();
                _mutex = null;
            }
        }

        private static bool TryGetPropValue(EnvDTE.Property prop, out object value)
        {
            try
            {
                value = prop.Value;
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    ClangdLog.WriteLine($"takeover: DTE {prop.Name} read failed: {ex.Message}");
                }
                catch
                {
                    // Logging must never throw during suppression.
                }

                value = null;
                return false;
            }
        }

        /// <summary>
        /// Applies or reverts the suppression.
        /// dte may be null. registryRoot identifies the legacy registry
        /// fallback location.
        /// </summary>
        public static bool SetSuppressed(bool suppressed, EnvDTE.DTE dte, string registryRoot)
        {
            if (suppressed)
            {
                Apply(dte, registryRoot);
                return true;
            }

            return TryRevert(dte, registryRoot);
        }

        private static void Apply(EnvDTE.DTE dte, string registryRoot)
        {
            var backupPath = BackupFileFor(registryRoot);
            Dictionary<string, string> backup;
            using (BackupMutex())
            {
                backup = ReadBackup(backupPath) ?? ReadBackup(BackupFile) ?? new Dictionary<string, string>();
            }

            var changed = false;

            // Primary on VS 2026 (proven live): the real VC automation page
            // "TextEditor / C/C++ Specific" bridges to the unified settings.
            if (dte != null)
            {
                foreach (var page in GetCppPropertyPages(dte))
                {
                    EnvDTE.Property[] props;
                    try
                    {
                        props = page.Cast<EnvDTE.Property>().ToArray();
                    }
                    catch (Exception ex)
                    {
                        ClangdLog.WriteLine($"takeover: enumerate DTE page failed: {ex.Message}");
                        continue;
                    }

                    foreach (var prop in props)
                    {
                        string propName;
                        try
                        {
                            propName = prop.Name;
                        }
                        catch
                        {
                            continue;
                        }

                        if (!TryGetPropValue(prop, out var rawValue))
                        {
                            continue;
                        }

                        // Int-valued linter severity props (ErrorTagType /
                        // SuggestionLevel): set 0 (= None) to silence each
                        // individual VCR00x suggestion.
                        if (rawValue is int intValue && IsLinterSeverityProperty(propName))
                        {
                            if (intValue != 0)
                            {
                                backup[$"dte:{propName}"] = intValue.ToString();
                            }

                            try
                            {
                                prop.Value = 0;
                                changed = true;
                                ClangdLog.WriteLine($"takeover: DTE {propName} = 0 (none)");
                            }
                            catch (Exception ex)
                            {
                                ClangdLog.WriteLine($"takeover: DTE {propName} write failed: {ex.Message}");
                            }

                            continue;
                        }

                        if (!(rawValue is bool boolValue))
                        {
                            continue;
                        }

                        var isVcDisable = Array.IndexOf(DteVcDisableFlags, propName) >= 0;
                        // "EnableLinter*"/"EnableErrorFixSuggestions" are
                        // enable-style: suppress by writing false.
                        var isVcEnableOff = propName.StartsWith("EnableLinter", StringComparison.OrdinalIgnoreCase) ||
                                            propName == "EnableErrorFixSuggestions";
                        var isGenericFlag =
                            propName == "AutoListMembers" || propName == "AutoListParams" || propName == "ShowErrors";

                        if (!isVcDisable && !isVcEnableOff && !isGenericFlag)
                        {
                            continue;
                        }

                        // VC "Disable*" props flip to true; enable-style and
                        // generic-page flags flip to false.
                        var target = isVcEnableOff ? false : isVcDisable;
                        if (boolValue != target)
                        {
                            backup[$"dte:{propName}"] = boolValue.ToString();
                        }

                        try
                        {
                            prop.Value = target;
                            changed = true;
                            ClangdLog.WriteLine($"takeover: DTE {propName} = {target}");
                        }
                        catch (Exception ex)
                        {
                            ClangdLog.WriteLine($"takeover: DTE {propName} write failed: {ex.Message}");
                        }
                    }
                }
            }

            // Legacy fallback (VS 2022): registry flags.
            foreach (var name in DisableFlagNames)
            {
                var hasIntegerValue = TryReadRegistryFlag(registryRoot, name, out var value, out var keyExists,
                                                          out var valueExists, out var readSucceeded);
                if (hasIntegerValue)
                {
                    var key = $"reg:{name}";
                    if (value != 1 && !backup.ContainsKey(key))
                    {
                        backup[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                else if (readSucceeded && !valueExists)
                {
                    var key = $"reg:{name}";
                    if (!backup.ContainsKey(key))
                        backup[key] = RegistryValueWasMissing;
                    if (!keyExists && !backup.ContainsKey($"reg-key:{name}"))
                        backup[$"reg-key:{name}"] = RegistryKeyWasMissing;
                }

                if (readSucceeded && (!valueExists || hasIntegerValue && value != 1) &&
                    TrySetFlag(registryRoot, name, 1))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                using (BackupMutex())
                {
                    WriteBackup(backupPath, backup);
                }

                ClangdLog.WriteLine("Native C/C++ IntelliSense suppressed (takeover enabled).");
            }
        }

        internal static bool TryRevert(EnvDTE.DTE dte, string registryRoot)
        {
            var backupPath = BackupFileFor(registryRoot);
            Dictionary<string, string> backup;
            using (BackupMutex())
            {
                backup = ReadBackup(backupPath) ?? ReadBackup(BackupFile);
            }

            var skippedForMissingBackup = 0;

            // DTE restore (VC page + generic page flags written by Apply).
            if (dte != null)
            {
                foreach (var page in GetCppPropertyPages(dte))
                {
                    EnvDTE.Property[] props;
                    try
                    {
                        props = page.Cast<EnvDTE.Property>().ToArray();
                    }
                    catch (Exception ex)
                    {
                        ClangdLog.WriteLine($"takeover: enumerate DTE page failed: {ex.Message}");
                        continue;
                    }

                    foreach (var prop in props)
                    {
                        string propName;
                        try
                        {
                            propName = prop.Name;
                        }
                        catch
                        {
                            continue;
                        }

                        if (!TryGetPropValue(prop, out var rawValue))
                        {
                            continue;
                        }

                        // Restore only values captured before takeover changed them.
                        if (rawValue is int && IsLinterSeverityProperty(propName))
                        {
                            var intKey = $"dte:{propName}";
                            if (!RestoreProperty(backup, intKey, prop) && backup == null)
                            {
                                skippedForMissingBackup++;
                            }

                            continue;
                        }

                        if (!(rawValue is bool))
                        {
                            continue;
                        }

                        var isVcDisable = Array.IndexOf(DteVcDisableFlags, propName) >= 0;
                        var isVcEnableOff = propName.StartsWith("EnableLinter", StringComparison.OrdinalIgnoreCase) ||
                                            propName == "EnableErrorFixSuggestions";
                        var isGenericFlag =
                            propName == "AutoListMembers" || propName == "AutoListParams" || propName == "ShowErrors";
                        if (!isVcDisable && !isVcEnableOff && !isGenericFlag)
                        {
                            continue;
                        }

                        var key = $"dte:{propName}";
                        if (!RestoreProperty(backup, key, prop) && backup == null)
                        {
                            skippedForMissingBackup++;
                        }
                    }
                }
            }

            // Registry restore.
            if (backup == null)
            {
                ClangdLog.WriteLine("takeover: no registry backup found; leaving existing values unchanged.");
            }
            else
            {
                foreach (var pair in backup.Where(p => p.Key.StartsWith("reg:")))
                {
                    var name = pair.Key.Substring(4);
                    if (pair.Value == RegistryValueWasMissing)
                    {
                        TryDeleteFlag(registryRoot, name);
                    }
                    else if (int.TryParse(pair.Value, System.Globalization.NumberStyles.Integer,
                                          System.Globalization.CultureInfo.InvariantCulture, out var value))
                    {
                        TrySetFlag(registryRoot, name, value);
                    }
                }

                foreach (var pair in backup.Where(p =>
                                                      p.Key.StartsWith("reg-key:") && p.Value == RegistryKeyWasMissing))
                {
                    TryDeleteCreatedRegistryKey(registryRoot);
                }
            }

            try
            {
                using (BackupMutex())
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    // Compatibility with the old single-file layout: delete it too after the new file, so residue does not cause a misread next time.
                    if (!string.Equals(backupPath, BackupFile, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(BackupFile))
                    {
                        File.Delete(BackupFile);
                    }
                }
            }
            catch
            {
                // Ignore.
            }

            if (backup == null)
            {
                if (HasSuppressionResidue(dte, registryRoot))
                {
                    ClangdLog.WriteLine(
                        $"takeover: no backup found, {skippedForMissingBackup} setting(s) skipped and suppression residue remains; not deleting anything else as evidence.");
                    return false;
                }

                ClangdLog.WriteLine(
                    "takeover: no backup found, but no suppression residue detected; nothing to restore.");
                return true;
            }

            ClangdLog.WriteLine("Native C/C++ IntelliSense restored.");
            return true;
        }

        private static bool HasSuppressionResidue(EnvDTE.DTE dte, string registryRoot)
        {
            if (dte != null)
            {
                foreach (var page in GetCppPropertyPages(dte))
                {
                    EnvDTE.Property[] props;
                    try
                    {
                        props = page.Cast<EnvDTE.Property>().ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var prop in props)
                    {
                        string propName;
                        try
                        {
                            propName = prop.Name;
                        }
                        catch
                        {
                            continue;
                        }

                        if (!TryGetPropValue(prop, out var rawValue))
                        {
                            continue;
                        }

                        if (rawValue is int intValue && IsLinterSeverityProperty(propName) && intValue == 0)
                        {
                            return true;
                        }

                        if (!(rawValue is bool boolValue))
                        {
                            continue;
                        }

                        var isVcDisable = Array.IndexOf(DteVcDisableFlags, propName) >= 0;
                        var isVcEnableOff = propName.StartsWith("EnableLinter", StringComparison.OrdinalIgnoreCase) ||
                                            propName == "EnableErrorFixSuggestions";
                        var isGenericFlag =
                            propName == "AutoListMembers" || propName == "AutoListParams" || propName == "ShowErrors";
                        if (!isVcDisable && !isVcEnableOff && !isGenericFlag)
                        {
                            continue;
                        }

                        var suppressed = isVcEnableOff ? false : isVcDisable;
                        if (boolValue == suppressed)
                        {
                            return true;
                        }
                    }
                }
            }

            foreach (var name in DisableFlagNames)
            {
                if (TryReadRegistryFlag(registryRoot, name, out var value, out _, out _, out _) && value == 1)
                {
                    return true;
                }
            }

            return false;
        }

        // --- DTE helpers ---

        private static IEnumerable<EnvDTE.Properties> GetCppPropertyPages(EnvDTE.DTE dte)
        {
            var combos = new[] {
                new[] { "TextEditor", "C/C++ Specific" }, // real VC automation page (space)
                new[] { "TextEditor", "C/C++" },          // generic per-language page
            };

            foreach (var combo in combos)
            {
                EnvDTE.Properties props = null;
                try
                {
                    props = dte.get_Properties(combo[0], combo[1]);
                }
                catch
                {
                    // Category/page does not exist - keep probing.
                }

                if (props != null)
                {
                    yield return props;
                }
            }
        }

        // --- Registry fallback helpers ---

        private static bool TryReadRegistryFlag(string registryRoot, string name, out int value, out bool keyExists,
                                                out bool valueExists, out bool readSucceeded)
        {
            value = 0;
            keyExists = false;
            valueExists = false;
            readSucceeded = false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath(registryRoot)))
                {
                    readSucceeded = true;
                    keyExists = key != null;
                    var stored = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    valueExists = stored != null;
                    if (stored is int flag)
                    {
                        value = flag;
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore and fall through.
            }

            return false;
        }

        private static bool TryDeleteFlag(string registryRoot, string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath(registryRoot), writable: true))
                {
                    if (key == null || key.GetValue(name) == null)
                        return true;
                    key.DeleteValue(name, throwOnMissingValue: false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"Failed to remove takeover registry flag '{name}': {ex.Message}");
                return false;
            }
        }

        private static void TryDeleteCreatedRegistryKey(string registryRoot)
        {
            try
            {
                // DeleteSubKey only removes an empty key. Never recursively remove
                // values another component may have added since takeover began.
                Registry.CurrentUser.DeleteSubKey(SettingsKeyPath(registryRoot), throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"Failed to remove empty takeover registry key: {ex.Message}");
            }
        }

        private static bool TrySetFlag(string registryRoot, string name, int value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath(registryRoot)))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    if (value == 0 && key.GetValue(name) == null)
                    {
                        return false; // Nothing to clear.
                    }

                    key.SetValue(name, value, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"Failed to set '{name}' = {value}: {ex.Message}");
                return false;
            }
        }

        private static string SettingsKeyPath(string registryRoot) => $"{registryRoot}\\Text Editor\\C/C++-Specific";

        // --- Backup persistence ---

        private static Dictionary<string, string> ReadBackup(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                    typeof(Dictionary<string, string>));
                using (var stream = File.OpenRead(path))
                {
                    return serializer.ReadObject(stream) as Dictionary<string, string>;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void WriteBackup(string path, Dictionary<string, string> backup)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                    typeof(Dictionary<string, string>));
                using (var stream = File.Create(path))
                {
                    serializer.WriteObject(stream, backup);
                }
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
