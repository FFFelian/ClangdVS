using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ClangdVS
{
    /// <summary>
    /// Global clangd settings, mirrored from the vscode-clangd extension's
    /// "clangd.*" configuration section. Persisted as JSON under
    /// %LOCALAPPDATA%\ClangdVS\settings.json so both the MEF language client
    /// and the Tools &gt; Options page can reach them without package services.
    /// </summary>
    public sealed class ClangdSettings
    {
        private static ClangdSettings _instance = Load();
        private static FileSystemWatcher _watcher;
        private static readonly object WatcherGate = new object();
        // Instance is mutable (Options setters write through), so compare with a
        // value snapshot of the last applied state, never with Instance itself.
        private static SettingsSnapshot _applied = new SettingsSnapshot(_instance);
        internal static event Action<bool> TakeoverSettingChanged;
        internal static event Action<bool, bool, bool, bool, bool> FeatureSettingsChanged;

        internal static void NotifyTakeoverSettingChanged(bool enabled) => TakeoverSettingChanged?.Invoke(enabled);

        internal static void NotifyFeatureSettingsChanged(bool diagnosticsEnabled, bool diagnosticsChanged,
                                                          bool inactiveRegionsChanged, bool semanticHighlightingChanged,
                                                          bool takeoverChanged) =>
            FeatureSettingsChanged?.Invoke(diagnosticsEnabled, diagnosticsChanged, inactiveRegionsChanged,
                                            semanticHighlightingChanged, takeoverChanged);

        public static ClangdSettings Instance
        {
            get {
                EnsureWatcher();
                return _instance;
            }
        }

        private static void EnsureWatcher()
        {
            lock (WatcherGate)
            {
                if (_watcher != null)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(SettingsDirectory);
                    _watcher = new FileSystemWatcher(SettingsDirectory, "settings.json") {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true,
                    };
                    _watcher.Changed += (sender, e) => Reload();
                    _watcher.Created += (sender, e) => Reload();
                }
                catch
                {
                    // Best effort: without a watcher settings stay in memory.
                    _watcher?.Dispose();
                    _watcher = null;
                }
            }
        }

        internal static void Reload()
        {
            lock (WatcherGate)
            {
                // Invalid/partially written files must not publish defaults.
                if (!TryLoad(out var reloaded))
                    return;
                var previous = _applied;
                var current = new SettingsSnapshot(reloaded);
                // A late Save echo must not replace Instance after an Options
                // setter has already started the next (not yet applied) edit.
                if (current.HasSameContents(previous))
                    return;
                System.Threading.Interlocked.Exchange(ref _instance, reloaded);
                _applied = current;
                current.NotifyChanges(previous);
            }
        }

        private static string SettingsDirectory =>
            Environment.GetEnvironmentVariable("CLANGDVS_SETTINGS_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClangdVS");

        private static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

        private static ClangdSettings Load() => TryLoad(out var settings) ? settings : new ClangdSettings();

        private static bool TryLoad(out ClangdSettings settings)
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ClangdSettings));
                    using (var stream = File.OpenRead(SettingsFile))
                    {
                        if (serializer.ReadObject(stream) is ClangdSettings loaded)
                        {
                            settings = loaded;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Keep the current settings when an external write is incomplete.
            }

            settings = null;
            return false;
        }

        public void Save() => Save(false);

        internal void SaveAndNotify() => Save(true);

        private void Save(bool notify)
        {
            lock (WatcherGate)
            {
                var previous = _applied;
                var current = new SettingsSnapshot(this);
                try
                {
                    Directory.CreateDirectory(SettingsDirectory);
                    var serializer = new DataContractJsonSerializer(typeof(ClangdSettings));
                    using (var stream = File.Create(SettingsFile))
                    {
                        serializer.WriteObject(stream, this);
                    }
                }
                catch
                {
                    // Best effort: options stay in memory for this session.
                }

                // Watcher callbacks cannot observe our partial write. Once they
                // acquire the gate, the value snapshot makes Save echoes no-ops
                // without suppressing a different external edit in a time window.
                _applied = current;
                if (notify)
                    current.NotifyChanges(previous);
            }
        }

        private sealed class SettingsSnapshot
        {
            private readonly bool _takeover;
            private readonly bool[] _features;
            private readonly string _contents;

            internal SettingsSnapshot(ClangdSettings settings)
            {
                using (var stream = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(ClangdSettings)).WriteObject(stream, settings);
                    _contents = Encoding.UTF8.GetString(stream.ToArray());
                }
                _takeover = settings.TakeOverFromNativeIntelliSense;
                _features = new[] {
                    settings.EnableCompletionAndHover, settings.EnableDiagnostics,
                    settings.EnableSemanticHighlighting, settings.EnableFormatting,
                    settings.EnableCodeActions, settings.EnableNavigation, settings.EnableInactiveRegions,
                    settings.EnableInlayHints, settings.EnableAstView, settings.EnableSymbolInfo,
                    settings.EnableTypeHierarchy,
                };
            }

            internal bool HasSameContents(SettingsSnapshot other) => _contents == other._contents;

            internal void NotifyChanges(SettingsSnapshot previous)
            {
                var takeoverChanged = _takeover != previous._takeover;
                var featureChanged = false;
                for (var i = 0; i < _features.Length; i++)
                    featureChanged |= _features[i] != previous._features[i];
                // Queue takeover first so its lifecycle transition consumes the
                // feature restart reasons when both belong to this same delta.
                if (takeoverChanged)
                    NotifyTakeoverSettingChanged(_takeover);
                if (featureChanged)
                    NotifyFeatureSettingsChanged(_features[1], _features[1] != previous._features[1],
                                                 _features[6] != previous._features[6],
                                                  _features[2] != previous._features[2] ||
                                                  _features[4] != previous._features[4], takeoverChanged);
            }
        }

        // --- Mirrors of vscode-clangd settings ---

        /// <summary>Path to the clangd executable ("clangd" = resolve via PATH / auto-detection).</summary>
        public string ClangdPath { get; set; } = "clangd";

        /// <summary>Extra arguments passed to clangd (same as clangd.arguments in vscode-clangd).</summary>
        public string[] Arguments { get; set; } = Array.Empty<string>();

        /// <summary>Extra clang flags used when no compilation database is found (clangd.fallbackFlags).</summary>
        public string[] FallbackFlags { get; set; } = Array.Empty<string>();

        /// <summary>File clangd should log a chrome trace-viewer performance trace to (clangd.trace).</summary>
        public string TraceFile { get; set; } = "";

        /// <summary>Automatically restart clangd after a crash (clangd.restartAfterCrash).</summary>
        public bool RestartAfterCrash { get; set; } = true;

        /// <summary>
        /// When enabled, the extension disables the built-in C++ IntelliSense
        /// engine (QuickInfo / member lists / squiggles) so clangd takes over.
        /// </summary>
        public bool TakeOverFromNativeIntelliSense { get; set; } = false;

        public bool EnableCompletionAndHover { get; set; } = true;

        public bool EnableDiagnostics { get; set; } = true;

        public bool EnableSemanticHighlighting { get; set; } = true;

        public bool EnableFormatting { get; set; } = true;

        public bool EnableCodeActions { get; set; } = true;

        public bool EnableNavigation { get; set; } = true;

        public bool EnableInactiveRegions { get; set; } = true;

        public bool EnableInlayHints { get; set; } = true;

        public bool EnableAstView { get; set; } = false;

        public bool EnableSymbolInfo { get; set; } = false;

        public bool EnableTypeHierarchy { get; set; } = false;

        /// <summary>When true, log clangd stderr to the "clangd" output pane.</summary>
        public bool LogToOutputWindow { get; set; } = true;
    }

    /// <summary>Static accessor helpers (used by the MEF language client).</summary>
    internal static class ClangdSettingsExtensions
    {
        public static string ExpandVariables(this string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var builder = new StringBuilder(value);
            builder.Replace("${userHome}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            builder.Replace("${programFiles}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            builder.Replace("${localAppData}",
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            builder.Replace("${env:VCINSTALLDIR}", Environment.GetEnvironmentVariable("VCINSTALLDIR") ?? "");
            builder.Replace("${env:VSINSTALLDIR}", Environment.GetEnvironmentVariable("VSINSTALLDIR") ?? "");
            return builder.ToString();
        }
    }
}
