using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using ClangdVS;

internal static class ClangdSettingsReloadRegression
{
    internal static void Run()
    {
        const string directoryVariable = "CLANGDVS_SETTINGS_DIRECTORY";
        var directory = Path.Combine(Path.GetTempPath(), "ClangdVS-settings-reload-" + Guid.NewGuid().ToString("N"));
        var type = typeof(ClangdSettings);
        var gate = type.GetField("WatcherGate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var instance = type.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        var applied = type.GetField("_applied", BindingFlags.Static | BindingFlags.NonPublic);
        var watcher = type.GetField("_watcher", BindingFlags.Static | BindingFlags.NonPublic);
        lock (gate)
        {
            var original = instance.GetValue(null);
            var originalApplied = applied.GetValue(null);
            var originalWatcher = (FileSystemWatcher)watcher.GetValue(null);
            var originalWatcherEnabled = originalWatcher != null && originalWatcher.EnableRaisingEvents;
            var originalDirectory = Environment.GetEnvironmentVariable(directoryVariable);
            FileSystemWatcher sandboxWatcher = null;
            var events = new List<string>();
            Action<bool> takeover = enabled => events.Add("takeover:" + enabled);
            Action<bool, bool, bool, bool, bool> features = (enabled, diagnostics, inactive, semantic, changed) =>
                events.Add($"features:{enabled},{diagnostics},{inactive},{semantic},{changed}");
            ClangdSettings.TakeoverSettingChanged += takeover;
            ClangdSettings.FeatureSettingsChanged += features;
            try
            {
                if (originalWatcher != null) originalWatcher.EnableRaisingEvents = false;
                Environment.SetEnvironmentVariable(directoryVariable, directory);
                // Resolve after setting the override: the regular harness has already
                // initialized ClangdSettings. Never back up or write the user's file.
                var path = (string)type.GetProperty("SettingsFile", BindingFlags.Static | BindingFlags.NonPublic)
                                       .GetValue(null);
                Assert(string.Equals(Path.GetFullPath(path), Path.Combine(directory, "settings.json"),
                                     StringComparison.OrdinalIgnoreCase),
                       "SettingsDirectory must honor CLANGDVS_SETTINGS_DIRECTORY after static initialization");
                Directory.CreateDirectory(directory);
                // Drive callbacks explicitly with Reload. A disabled sandbox watcher
                // prevents Instance from creating an asynchronous watcher of its own.
                sandboxWatcher = new FileSystemWatcher(directory, "settings.json");
                watcher.SetValue(null, sandboxWatcher);
                instance.SetValue(null, new ClangdSettings());
                ClangdSettings.Instance.Save(); // Establish an independent applied baseline.
                Reload();
                events.Clear();

                Write(path, new ClangdSettings {
                    TakeOverFromNativeIntelliSense = true,
                    EnableDiagnostics = false,
                    EnableInactiveRegions = false,
                    EnableSemanticHighlighting = false,
                });
                Reload();
                Assert(events.Count == 2 && events[0] == "takeover:True" &&
                       events[1] == "features:False,True,True,True,True",
                       "external Reload must publish the same ordered delta as Options");
                Reload();
                Assert(events.Count == 2, "unchanged Reload published duplicate events");

                // An Options page opened before Reload must not replay its stale baseline.
                var page = (ApplyPage)FormatterServices.GetUninitializedObject(typeof(ApplyPage));
                Write(path, new ClangdSettings { EnableFormatting = false });
                Reload();
                events.Clear();
                page.Apply();
                Reload();
                Assert(events.Count == 0, "Options or its Save watcher replayed an external delta");

                page.EnableDiagnostics = false;
                page.TakeOverFromNativeIntelliSense = true;
                page.Apply();
                Reload();
                Assert(events.Count == 2 && events[0] == "takeover:True" &&
                       events[1] == "features:False,True,False,False,True",
                       "Options and Save watcher did not publish exactly one delta");
                events.Clear();

                page.EnableInlayHints = false;
                Reload(); // A late callback for the previous Save.
                Assert(!ClangdSettings.Instance.EnableInlayHints,
                       "unchanged Save echo overwrote an in-progress Options edit");
                page.Apply();
                events.Clear();

                // A real external edit immediately after Save must not be time-suppressed.
                Write(path, new ClangdSettings { EnableNavigation = false });
                Reload();
                Assert(events.Count == 2, "Save suppressed a subsequent external edit");
                events.Clear();
                File.WriteAllText(path, "{partial json");
                var valid = ClangdSettings.Instance;
                Reload();
                Assert(ReferenceEquals(valid, ClangdSettings.Instance) && events.Count == 0,
                       "invalid Reload replaced valid settings or published defaults");

                // Every feature toggle must participate in the shared delta calculation.
                foreach (var name in new[] { "EnableCompletionAndHover", "EnableDiagnostics",
                    "EnableSemanticHighlighting", "EnableFormatting", "EnableCodeActions", "EnableNavigation",
                    "EnableInactiveRegions", "EnableInlayHints", "EnableAstView", "EnableSymbolInfo",
                    "EnableTypeHierarchy" })
                {
                    Write(path, new ClangdSettings());
                    Reload();
                    events.Clear();
                    var next = new ClangdSettings();
                    var property = type.GetProperty(name);
                    property.SetValue(next, !(bool)property.GetValue(next));
                    Write(path, next);
                    Reload();
                    Assert(events.Count == 1 && events[0].StartsWith("features:"),
                           name + " did not publish exactly one feature delta");
                }
            }
            finally
            {
                ClangdSettings.TakeoverSettingChanged -= takeover;
                ClangdSettings.FeatureSettingsChanged -= features;
                try
                {
                    sandboxWatcher?.Dispose();
                }
                finally
                {
                    instance.SetValue(null, original);
                    applied.SetValue(null, originalApplied);
                    Environment.SetEnvironmentVariable(directoryVariable, originalDirectory);
                    watcher.SetValue(null, originalWatcher);
                    try
                    {
                        if (originalWatcher != null) originalWatcher.EnableRaisingEvents = originalWatcherEnabled;
                    }
                    finally
                    {
                        if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    }
                }
            }
        }
    }

    private sealed class ApplyPage : ClangdOptionsPage
    {
        internal void Apply() => OnApply(new PageApplyEventArgs());

        // Keep the real Options/SaveAndNotify/base.OnApply path, but omit VS's
        // property-store persistence, which needs a constructed/sited DialogPage.
        public override void SaveSettingsToStorage() { }
    }

    private static void Reload() => ClangdSettings.Reload();
    private static void Write(string path, ClangdSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var stream = File.Create(path))
            new DataContractJsonSerializer(typeof(ClangdSettings)).WriteObject(stream, settings);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
