using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Task = System.Threading.Tasks.Task;

namespace ClangdVS
{
    /// <summary>
    /// The clangd language server client, ported from the vscode-clangd
    /// extension (https://github.com/clangd/vscode-clangd). The VS LSP host
    /// handles the protocol; this class launches clangd and supplies
    /// clangd-specific initialize options, mirroring src/clangd-context.ts.
    /// </summary>
    [Export(typeof(ILanguageClient))]
    [Export(typeof(ClangdLanguageClient))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    [ContentType("cpp-clangd")]
    public sealed class ClangdLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        private Process _serverProcess;
        private Connection _reusableConnection;
        private readonly SemaphoreSlim _activationLock = new SemaphoreSlim(1, 1);
        private JsonRpc _rpc;
        private int _didOpenVersion;
        private volatile bool _expectingExit;

        // Crash self-healing circuit breaker: stop after more than 3 unexpected exits within 60 seconds
        // (we measured it spawning processes endlessly when --malloc-trim was misconfigured), and wait for
        // the user to restart manually; one successful initialization resets the count.
        private readonly object _crashGate = new object();

        private int _crashCount;

        private DateTime _lastCrashUtc = DateTime.MinValue;

        private int _initCount;

        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);

        internal static TimeSpan ShutdownLockTimeout = TimeSpan.FromSeconds(10);

        private readonly object _initializationGate = new object();
        private readonly System.Threading.AsyncLocal<RestartAttempt> _startContext =
            new System.Threading.AsyncLocal<RestartAttempt>();
        private RestartAttempt _restartAttempt;
        private RestartAttempt _currentStartContext;
        private bool _stopping;
        private bool _initializationPending;
        private int _lifecycleVersion;
        private const int RestartInitializationTimeoutMilliseconds = 30000;

        private sealed class RestartAttempt
        {
            // A result value avoids unobserved task exceptions if Start itself fails.
            internal readonly TaskCompletionSource<Exception> Completion =
                new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync;

        public string Name => "clangd";

        public IEnumerable<string> ConfigurationSections => new[] { "clangd" };

        /// <summary>
        /// clangd-specific initialize options, identical to vscode-clangd's
        /// initializationOptions (clangd-context.ts:108-111).
        /// </summary>
        public object InitializationOptions => new {
            clangdFileStatus = true,
            fallbackFlags = ClangdSettings.Instance.FallbackFlags ?? Array.Empty<string>(),
        };

        /// <summary>Config/build files whose changes are pushed to clangd.</summary>
        public IEnumerable<string> FilesToWatch => new[] {
            "**/.clangd", "**/.clang-format", "**/.clang-tidy", "**/compile_commands.json", "**/compile_flags.txt",
        };

        public bool ShowNotificationOnInitializeFailed => true;

        // --- Lifecycle (ILanguageClient) ---

        internal static bool ShouldAllowRestart(bool takeoverEnabled) => takeoverEnabled;

        internal static bool ShouldRunPostInitializeWork(bool takeoverEnabled) => takeoverEnabled;

        private int _loaded;

        internal bool IsRunning
        {
            get
            {
                lock (_initializationGate)
                {
                    return !_stopping && _rpc != null && _serverProcess != null && !_serverProcess.HasExited;
                }
            }
        }

        internal static ClangdLanguageClient Current { get; private set; }
        private long _sessionVersion;
        internal long SessionVersion => Interlocked.Read(ref _sessionVersion);

        private void NotifySessionChanged()
        {
            Interlocked.Increment(ref _sessionVersion);
            InlayHintCoordinator.SessionChanged();
        }

        public Task OnLoadedAsync()
        {
            ClangdHoverDiagnostics.Lifecycle("on-loaded", this);
            if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
            {
                ClangdLog.WriteLine("diag: OnLoadedAsync deferred because native IntelliSense takeover is disabled.");
                return Task.CompletedTask;
            }

            // Idempotent: may be invoked by both the automatic content-type
            // activation path and our manual broker LoadAsync.
            if (Interlocked.Exchange(ref _loaded, 1) == 1)
            {
                ClangdLog.WriteLine("diag: OnLoadedAsync called again (already loaded) - ignored.");
                return Task.CompletedTask;
            }

            ClangdLog.WriteLine("diag: OnLoadedAsync called - firing StartAsync.");
            // Fire StartAsync so the LSP host spins up the server (required).
            var handler = StartAsync;
            if (handler != null)
            {
                InvokeStartAsync(handler, new RestartAttempt()).Forget();
            }
            return Task.CompletedTask;
        }

        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            ClangdHoverDiagnostics.Lifecycle("activate", this);
            ClangdLog.WriteLine("diag: ActivateAsync called.");

            if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
            {
                ClangdLog.WriteLine("diag: ActivateAsync skipped because native IntelliSense takeover is disabled.");
                return null;
            }

            // Idempotent + race-free: the client may be activated twice (our
            // manual broker LoadAsync at package init + the automatic
            // content-type activation), possibly concurrently. The semaphore
            // guarantees only one clangd process is ever spawned; later
            // activations reuse it (a stray instance wastes hundreds of MB).
            await _activationLock.WaitAsync(token);
            try
            {
                lock (_initializationGate)
                {
                    if (_stopping ||
                        (_startContext.Value != null && !ReferenceEquals(_startContext.Value, _currentStartContext)))
                    {
                        throw new InvalidOperationException("clangd is stopping.");
                    }
                }

                if (_serverProcess != null && !_serverProcess.HasExited && _reusableConnection != null)
                {
                    ClangdLog.WriteLine("diag: reusing existing clangd process.");
                    return _reusableConnection;
                }

                int version;
                lock (_initializationGate)
                {
                    if (_stopping ||
                        (_startContext.Value != null && !ReferenceEquals(_startContext.Value, _currentStartContext)))
                    {
                        throw new InvalidOperationException("clangd is stopping.");
                    }

                    _initializationPending = true;
                    Interlocked.Increment(ref _lifecycleVersion);
                    version = _lifecycleVersion;
                }

                return await StartServerAsync(token, version);
            }
            finally
            {
                _activationLock.Release();
            }
        }

        private async Task<Connection> StartServerAsync(CancellationToken token, int lifecycleVersion)
        {
            var settings = ClangdSettings.Instance;

            var clangdPath = ClangdLocator.Resolve(out var failure);
            if (clangdPath == null)
            {
                throw new InvalidOperationException(failure);
            }

            var arguments = new List<string>(settings.Arguments ?? Array.Empty<string>());

            // The workspace root only needs a DTE read, so a single switch to the main thread suffices; the process launch itself stays on a background thread.
            string workspaceRoot = Environment.CurrentDirectory;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            workspaceRoot = GetWorkspaceRoot();
            await TaskScheduler.Default;

            var startInfo = new ProcessStartInfo {
                FileName = clangdPath,
                Arguments = BuildArguments(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // clangd indexes relative to the working directory.
                WorkingDirectory = workspaceRoot,
            };

            var traceFile = (settings.TraceFile ?? string.Empty).ExpandVariables();
            if (!string.IsNullOrEmpty(traceFile))
            {
                startInfo.EnvironmentVariables["CLANGD_TRACE"] = traceFile;
            }

            ClangdLog.WriteLine($"clangd path: {clangdPath}");
            ClangdLog.WriteLine($"clangd args: {string.Join(" ", arguments)}");
            var version = ClangdLocator.QueryVersion(clangdPath);
            if (!string.IsNullOrEmpty(version))
            {
                ClangdLog.WriteLine($"clangd version: {version.Split('\n')[0].Trim()}");
            }

            // A Stop may have started while activation was acquiring VS services.
            lock (_initializationGate)
            {
                if (_stopping || lifecycleVersion != _lifecycleVersion ||
                    (_startContext.Value != null && !ReferenceEquals(_startContext.Value, _currentStartContext)))
                {
                    throw new InvalidOperationException("clangd is stopping.");
                }
            }

            var process = Process.Start(startInfo);
            if (process == null || process.HasExited)
            {
                throw new InvalidOperationException($"Failed to start clangd at '{clangdPath}'.");
            }

            DrainStandardError(process.StandardError, settings.LogToOutputWindow).Forget();

            _serverProcess = process;
            _expectingExit = false;
            // On net472 EnableRaisingEvents lives on Process, not on StartInfo.
            process.EnableRaisingEvents = true;
            process.Exited += (sender, e) =>
            {
                try
                {
                    OnServerExited(process, process.ExitCode);
                }
                catch
                {
                    // The Exited callback never throws.
                }
            };

            // Connection(reader, writer): reader = server stdout, writer = server stdin.
            // Keep the Connection (the process streams) so a second activation
            // reuses this process instead of starting another one. The
            // streams stay alive for the process lifetime; VS disposes the
            // connection wrapper at shutdown via StopAsync.
            var filteredOutput = new LspMessageFilterStream(
                process.StandardOutput.BaseStream,
                message =>
                {
                    var method = (string)message["method"];
                    var current = ClangdSettings.Instance;
                    if (method == "textDocument/publishDiagnostics" && !current.EnableDiagnostics)
                        return false;
                    if (method == "textDocument/inactiveRegions" && !current.EnableInactiveRegions)
                        return false;
                    return true;
                });
            var initializedInput = new LspInitializeStream(process.StandardInput.BaseStream,
                                                           () => ClangdSettings.Instance.EnableInactiveRegions);
            _reusableConnection = new Connection(filteredOutput, initializedInput);
            return _reusableConnection;
        }

        public Task OnServerInitializedAsync()
        {
            if (!ShouldRunPostInitializeWork(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
            {
                ClangdLog.WriteLine(
                    "diag: post-initialize work skipped because native IntelliSense takeover is disabled.");
                return Task.CompletedTask;
            }

            var version = Volatile.Read(ref _lifecycleVersion);
            var initCount = RecordServerInitialized();
            if (initCount == 0)
            {
                ClangdLog.WriteLine("diag: stale/duplicate clangd initialization ignored.");
                return Task.CompletedTask;
            }

            ClangdLog.WriteLine("clangd initialized.");
            NotifySessionChanged();
            ClangdHoverDiagnostics.Lifecycle("initialized", this, initialized: true);
            lock (_crashGate)
            {
                _crashCount = 0;
            }

            // Preserve the existing cold-start workaround; it is not a failure
            // detector and also restarts sessions where hover already works.
            // The confirmed hover rejection occurs while the host document client
            // has no openedDocumentUri. The original host didOpen loss remains
            // unconfirmed; the binding repair below addresses its observed result.
            if (initCount == 1)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => MaybeColdStartResyncAsync(version));
            }

            // Documents recovered by cold start may have been created before the client
            // initialized, so the host's internal LanguageServiceClient never recorded an
            // openedDocumentUri. Go through the host's own didOpen factory to both repair the
            // binding and send the standard notification; when the private host interface is
            // unavailable or the binding is unconfirmed, keep the original direct resend as a
            // server-side fallback.
            ThreadHelper.JoinableTaskFactory.RunAsync(
                async () =>
                {
                    Func<bool> isCurrent = () =>
                        Volatile.Read(ref _initCount) == initCount && Volatile.Read(ref _lifecycleVersion) == version;
                    await SynchronizeInitializedDocumentsAsync(
                        isCurrent,
                        () =>
                            ClangdHoverDiagnostics.RepairHostDocumentBindingsAsync(this, initCount, version, isCurrent),
                        () => ResendDidOpenForOpenDocumentsAsync(isCurrent));
                });
            return Task.CompletedTask;
        }

        internal static async Task SynchronizeInitializedDocumentsAsync(Func<bool> isCurrent,
                                                                        Func<Task<bool>> repairHostBinding,
                                                                        Func<Task> resendDirect)
        {
            if (!isCurrent())
                return;
            var bound = await repairHostBinding();
            if (!bound && isCurrent())
                await resendDirect();
        }

        // ILanguageClient callbacks have no connection ID. Stop is the broker's
        // session boundary; also retain the Start execution context so delayed
        // callbacks from an earlier restart cannot complete a newer attempt.
        private int RecordServerInitialized()
        {
            lock (_initializationGate)
            {
                if (!IsCurrentInitialization())
                {
                    return 0;
                }

                _initializationPending = false;
                var count = Interlocked.Increment(ref _initCount);
                _currentStartContext?.Completion.TrySetResult(null);
                _restartAttempt?.Completion.TrySetResult(null);
                return count;
            }
        }

        // Called only under _initializationGate. An untagged callback must follow
        // a fresh ActivateAsync, rather than merely follow a Start event.
        private bool IsCurrentInitialization()
        {
            if (_stopping)
            {
                return false;
            }

            var context = _startContext.Value;
            if (context != null)
            {
                return ReferenceEquals(context, _currentStartContext) &&
                       (_initializationPending || !context.Completion.Task.IsCompleted);
            }

            return _initializationPending && (_restartAttempt == null || !_restartAttempt.Completion.Task.IsCompleted);
        }

        /// <summary>
        /// Cold-start re-sync: 20 seconds after the first initialization, if there was no
        /// re-initialization in the meantime (a manual restart or crash self-healing both change
        /// _initCount) and a C++ document is still open, automatically run the exact same
        /// RestartAsync as the manual "restart language server".
        /// Runs on a background thread; RestartAsync performs its own thread switches internally,
        /// consistent with the 0x1101 menu path.
        /// </summary>
        private async Task MaybeColdStartResyncAsync(int version)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(20000);
                if (!ShouldRunPostInitializeWork(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
                {
                    ClangdLog.WriteLine("diag: cold-start re-sync aborted (takeover disabled).");
                    return;
                }

                if (Volatile.Read(ref _initCount) != 1 || Volatile.Read(ref _lifecycleVersion) != version)
                {
                    ClangdLog.WriteLine("diag: cold-start re-sync aborted (re-initialized).");
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var hasDoc = false;
                try
                {
                    var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                    if (dte != null)
                    {
                        foreach (EnvDTE.Document document in dte.Documents)
                        {
                            string path = null;
                            try
                            {
                                path = document.FullName;
                            }
                            catch
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(path) && ClangdFileExtensions.IsCppFile(path))
                            {
                                hasDoc = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // If DTE is not ready, abandon this round without retrying (to avoid disturbing the user).
                }

                if (!hasDoc)
                {
                    ClangdLog.WriteLine("diag: cold-start re-sync aborted (no C++ document).");
                    return;
                }

                ClangdLog.WriteLine(
                    "diag: cold-start re-sync restarting (existing workaround; hover failure not established).");
                await RestartAsync(version);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"diag: cold-start re-sync failed: {ex.Message}");
            }
        }

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo info)
        {
            var message = info?.InitializationException?.Message ?? info?.StatusMessage ?? "unknown error";
            ClangdLog.WriteLine($"clangd failed to initialize: {message}");
            lock (_initializationGate)
            {
                if (IsCurrentInitialization())
                {
                    _initializationPending = false;
                    var failure = new InvalidOperationException($"clangd failed to initialize: {message}",
                                                                info?.InitializationException);
                    _currentStartContext?.Completion.TrySetResult(failure);
                    _restartAttempt?.Completion.TrySetResult(failure);
                    // The host stops failed initialization itself (without our
                    // StopAsync event). Its shutdown/exit is not a server crash.
                    _expectingExit = true;
                    _stopping = true;
                    _currentStartContext = null;
                    Interlocked.Increment(ref _lifecycleVersion);
                }
            }

            return Task.FromResult<InitializationFailureContext>(null);
        }

        // --- Custom messages (ILanguageClientCustomMessage2) ---

        /// <summary>Target for clangd-specific notifications from the server.</summary>
        private static readonly ClangdFeatureMiddleLayer FeatureMiddleLayer =
            new ClangdFeatureMiddleLayer(() => ClangdSettings.Instance);

        private static readonly ClangdCustomTarget CustomTargetInstance = new ClangdCustomTarget();

        public object CustomMessageTarget => CustomTargetInstance;

        public Task AttachForCustomMessageAsync(JsonRpc rpc)
        {
            lock (_initializationGate)
            {
                if (_stopping ||
                    (_startContext.Value != null && !ReferenceEquals(_startContext.Value, _currentStartContext)))
                {
                    return Task.CompletedTask;
                }

                _rpc = rpc;
                Current = this;
            }
            NotifySessionChanged();
            ClangdLog.WriteLine("Attached to clangd JSON-RPC channel.");
            ClangdHoverDiagnostics.Lifecycle("rpc-attached", this);
            return Task.CompletedTask;
        }

        object ILanguageClientCustomMessage2.MiddleLayer => FeatureMiddleLayer;

        internal async Task<Newtonsoft.Json.Linq.JToken> RequestInlayHintsAsync(string uri, int startLine,
                                                                                int endLine,
                                                                                CancellationToken token)
        {
            var rpc = _rpc;
            if (rpc == null || !ClangdSettings.Instance.EnableInlayHints)
                return null;
            return await rpc.InvokeWithParameterObjectAsync<Newtonsoft.Json.Linq.JToken>(
                "clangd/inlayHints",
                new { textDocument = new { uri }, range = new { start = new { line = startLine, character = 0 },
                                                             end = new { line = endLine + 1, character = 0 } } },
                token);
        }

        internal Task<Newtonsoft.Json.Linq.JToken> RequestAstAsync(string uri, int line, int character,
                                                                   CancellationToken token) =>
            RequestExtensionAsync("textDocument/ast",
                                  new { textDocument = new { uri }, range = new {
                                      start = new { line, character }, end = new { line, character },
                                  } }, ClangdSettings.Instance.EnableAstView, token);

        internal Task<Newtonsoft.Json.Linq.JToken> RequestSymbolInfoAsync(string uri, int line, int character,
                                                                           CancellationToken token) =>
            RequestExtensionAsync("textDocument/symbolInfo",
                                  new { textDocument = new { uri }, position = new { line, character } },
                                  ClangdSettings.Instance.EnableSymbolInfo, token);

        internal async Task<Newtonsoft.Json.Linq.JToken> RequestTypeHierarchyAsync(string uri, int line,
                                                                                   int character,
                                                                                   CancellationToken token)
        {
            if (!ClangdSettings.Instance.EnableTypeHierarchy)
                return null;
            JsonRpc rpc;
            long sessionVersion;
            lock (_initializationGate)
            {
                token.ThrowIfCancellationRequested();
                if (_stopping || _rpc == null)
                    return null;
                rpc = _rpc;
                sessionVersion = SessionVersion;
            }
            var item = await RequestExtensionAsync(
                rpc, sessionVersion,
                "textDocument/prepareTypeHierarchy",
                new { textDocument = new { uri }, position = new { line, character } }, token);
            ThrowIfRequestSessionChanged(rpc, sessionVersion, token);
            if (!(item is Newtonsoft.Json.Linq.JArray items) || items.Count == 0)
                return item;
            var roots = new Newtonsoft.Json.Linq.JArray();
            foreach (var value in items)
            {
                if (!ClangdSettings.Instance.EnableTypeHierarchy)
                    return null;
                var supers = await RequestExtensionAsync(
                    rpc, sessionVersion, "typeHierarchy/supertypes", new { item = value }, token);
                ThrowIfRequestSessionChanged(rpc, sessionVersion, token);
                if (!ClangdSettings.Instance.EnableTypeHierarchy)
                    return null;
                var subs = await RequestExtensionAsync(
                    rpc, sessionVersion, "typeHierarchy/subtypes", new { item = value }, token);
                ThrowIfRequestSessionChanged(rpc, sessionVersion, token);
                roots.Add(new Newtonsoft.Json.Linq.JObject {
                    ["item"] = value,
                    ["supertypes"] = supers as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray(),
                    ["subtypes"] = subs as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray(),
                });
            }
            ThrowIfRequestSessionChanged(rpc, sessionVersion, token);
            return roots;
        }

        internal async Task<Newtonsoft.Json.Linq.JToken[]> RequestHierarchyChildrenAsync(
            Newtonsoft.Json.Linq.JToken item, bool supertypes, CancellationToken token)
        {
            var result = await RequestExtensionAsync(
                supertypes ? "typeHierarchy/supertypes" : "typeHierarchy/subtypes",
                new { item }, ClangdSettings.Instance.EnableTypeHierarchy, token);
            return result is Newtonsoft.Json.Linq.JArray values
                       ? System.Linq.Enumerable.ToArray(values)
                       : Array.Empty<Newtonsoft.Json.Linq.JToken>();
        }

        private async Task<Newtonsoft.Json.Linq.JToken> RequestExtensionAsync(string method, object parameters,
                                                                              bool enabled,
                                                                              CancellationToken token)
        {
            if (!enabled)
                return null;
            JsonRpc rpc;
            long sessionVersion;
            lock (_initializationGate)
            {
                token.ThrowIfCancellationRequested();
                if (_stopping || _rpc == null)
                    return null;
                rpc = _rpc;
                sessionVersion = SessionVersion;
            }
            var result = await RequestExtensionAsync(rpc, sessionVersion, method, parameters, token);
            ThrowIfRequestSessionChanged(rpc, sessionVersion, token);
            return result;
        }

        private async Task<Newtonsoft.Json.Linq.JToken> RequestExtensionAsync(
            JsonRpc rpc, long sessionVersion, string method, object parameters, CancellationToken token)
        {
            ThrowIfRequestSessionChanged(rpc, sessionVersion, token);
            // Never hold the state gate while invoking RPC or awaiting its response.
            var result = await rpc.InvokeWithParameterObjectAsync<Newtonsoft.Json.Linq.JToken>(method, parameters, token);
            ThrowIfRequestSessionChanged(rpc, sessionVersion, token);
            return result;
        }

        private void ThrowIfRequestSessionChanged(JsonRpc rpc, long sessionVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_initializationGate)
            {
                if (_stopping || !ReferenceEquals(_rpc, rpc) || SessionVersion != sessionVersion)
                    throw new OperationCanceledException("clangd request session has ended.", token);
            }
        }

        // --- clangd-specific requests (ported from vscode-clangd) ---

        /// <summary>
        /// textDocument/switchSourceHeader - see vscode-clangd switch-source-header.ts.
        /// </summary>
        public async Task<string> SwitchSourceHeaderAsync(string documentPath, string documentText)
        {
            if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
            {
                ClangdLog.WriteLine(
                    "diag: switchSourceHeader skipped because native IntelliSense takeover is disabled.");
                return null;
            }

            if (_rpc == null)
            {
                return null;
            }

            // clangd requires the document to have been added via didOpen
            // before it will switch (otherwise "non-added document"). VS only
            // routes didOpen once the buffer enters the LSP pipeline; if the
            // active document has not been opened there yet, send didOpen on
            // our direct channel first (shared sender below, returns the URI
            // actually used so the switch request references the same one).
            var uri = await SendDidOpenAsync(documentPath, documentText, "switchSourceHeader");
            if (uri == null)
            {
                return null;
            }

            // Parameter shape mirrors vscode-clangd (switch-source-header.ts):
            // the TextDocumentIdentifier IS the root object: {"uri": "..."}.
            // Anonymous object (not JObject) for SystemTextJson safety.
            var result =
                await _rpc.InvokeWithParameterObjectAsync<string>("textDocument/switchSourceHeader", new { uri });
            return result;
        }

        /// <summary>
        /// Queries the definition over the direct channel (for the priority command target to bypass
        /// the host's routing). Returns the raw JToken; the caller parses
        /// Location | Location[] | LocationLink[] | null.
        /// </summary>
        public async Task<Newtonsoft.Json.Linq.JToken> GetDefinitionAsync(string uri, int line, int character,
                                                                          CancellationToken cancellationToken = default)
        {
            if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense ||
                !ClangdSettings.Instance.EnableNavigation)
            {
                ClangdLog.WriteLine("nav: definition skipped because takeover or navigation is disabled.");
                return null;
            }

            var rpc = _rpc;
            if (rpc == null)
            {
                return null;
            }

            try
            {
                return await rpc.InvokeWithParameterObjectAsync<Newtonsoft.Json.Linq.JToken>(
                    "textDocument/definition", new { textDocument = new { uri }, position = new { line, character } },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"nav: definition query failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Sends textDocument/didOpen over the clangd direct channel (self-rescue when the host drops
        /// it), returning the URI actually used (the caller must send follow-up requests with the same
        /// URI).
        /// The URI must match the host exactly: experiments show the host uses the original
        /// drive-letter casing (file:///D:/...), whereas the earlier lowercase drive letter
        /// (file:///d:/...) produced a "shadow document" in clangd - content and version both forked,
        /// and jump results referencing the lowercase URI could not be mapped by VS, causing some
        /// Ctrl+clicks to fail silently. version uses a shared counter and increases monotonically.
        /// didOpen is a notification with no id, so params must be the object itself:
        /// NotifyAsync wraps it into an array that clangd rejects ("expected object"), so
        /// NotifyWithParameterObjectAsync is required; the payload uses an anonymous object (JObject is
        /// corrupted under VS 2026's SystemTextJsonFormatter).
        /// </summary>
        /// <returns>The URI actually sent; null when _rpc is not ready.</returns>
        private async Task<string> SendDidOpenAsync(string documentPath, string documentText, string reason)
        {
            if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
            {
                ClangdLog.WriteLine("diag: didOpen skipped because native IntelliSense takeover is disabled.");
                return null;
            }

            if (_rpc == null)
            {
                return null;
            }

            // Byte-for-byte identical to the host: Uri.AbsoluteUri preserves the drive-letter casing as-is.
            var uri = new Uri(documentPath).AbsoluteUri;
            var textLength = documentText?.Length ?? 0;

            try
            {
                var version = Interlocked.Increment(ref _didOpenVersion);
                var didOpenPayload = new {
                    textDocument =
                        new {
                            uri,
                            languageId = "cpp",
                            text = documentText ?? string.Empty,
                            version,
                        },
                };
                await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", didOpenPayload);
                ClangdLog.WriteLine(
                    $"diag: didOpen sent for {reason}: uri={uri} version={version} chars={textLength}.");
                return uri;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"diag: didOpen send failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Cold-start self-rescue: resend didOpen for all open C/C++ documents.
        /// DTE access must be on the main thread; if _rpc is not ready yet, wait 3 seconds and retry once;
        /// during cold start the solution/documents may still be restoring, so retry twice with a delay
        /// when there are 0 documents.
        /// </summary>
        private async Task ResendDidOpenForOpenDocumentsAsync(Func<bool> isCurrent)
        {
            try
            {
                if (!isCurrent())
                    return;
                if (_rpc == null)
                {
                    await Task.Delay(3000);
                    if (!isCurrent())
                        return;
                    if (_rpc == null)
                    {
                        ClangdLog.WriteLine("diag: startup didOpen resend skipped (no rpc channel).");
                        return;
                    }
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!isCurrent())
                    return;
                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    ClangdLog.WriteLine("diag: startup didOpen resend skipped (no DTE).");
                    return;
                }

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0)
                    {
                        await TaskScheduler.Default;
                        await Task.Delay(5000);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    }

                    if (!isCurrent())
                        return;
                    var sent = await ResendDidOpenOnceAsync(dte, isCurrent);
                    ClangdLog.WriteLine(
                        $"diag: startup didOpen resend done, {sent} document(s) (attempt {attempt + 1}).");
                    if (sent > 0)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"diag: startup didOpen resend failed: {ex.Message}");
            }
        }

        /// <summary>A single resend round; returns the number of documents actually sent.</summary>
        private async Task<int> ResendDidOpenOnceAsync(EnvDTE.DTE dte, Func<bool> isCurrent)
        {
            var sent = 0;
            foreach (EnvDTE.Document document in dte.Documents)
            {
                if (!isCurrent())
                    return sent;
                string path = null;
                try
                {
                    path = document.FullName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(path) || !ClangdFileExtensions.IsCppFile(path))
                {
                    continue;
                }

                string text = null;
                try
                {
                    var textDocument = document.Object("TextDocument") as EnvDTE.TextDocument;
                    text = textDocument?.StartPoint?.CreateEditPoint()?.GetText(textDocument.EndPoint);
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine($"diag: startup didOpen read failed: {ex.Message}");
                    continue;
                }

                // Do not send empty text: once a null/empty string read while a document is still
                // restoring is sent, it overwrites the real document on clangd's side with empty
                // content (a new version), losing all navigation/diagnostics. Skip it, log it, and
                // wait for the next retry round.
                if (string.IsNullOrEmpty(text))
                {
                    ClangdLog.WriteLine($"diag: startup didOpen skipped (empty text): {path}");
                    continue;
                }

                if (!isCurrent())
                    return sent;
                if (await SendDidOpenAsync(path, text, "startup-resend") != null)
                {
                    sent++;
                }
            }

            return sent;
        }

        /// <summary>Cycles Stop/Start and waits for the replacement to initialize.</summary>
        public Task RestartAsync() => RestartAsync(null);

        internal Task RestartForFeatureSettingsAsync() => RestartAsync(null, null, true);

        private Task RestartAsync(int? coldStartVersion) => RestartAsync(coldStartVersion, null);

        private async Task RestartAsync(int? coldStartVersion, int? crashVersion, bool featureSettings = false)
        {
            if (!ShouldAllowRestart(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
            {
                if (featureSettings)
                    return;
                throw new InvalidOperationException(
                    "clangd restart rejected because native IntelliSense takeover is disabled.");
            }

            if (!await _lifecycleLock.WaitAsync(0))
            {
                if (featureSettings)
                    throw new FeatureSettingsApplyDeferredException(WaitForFeatureLifecycleReadyAsync);
                throw new InvalidOperationException("clangd restart/shutdown is already in progress.");
            }

            RestartAttempt attempt = null;
            Task startTask = null;
            var initialized = false;
            try
            {
                // IsRunning may be transiently false while another restart owns
                // the lock. Only decide whether features need a restart here.
                if (featureSettings &&
                    (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense || !IsRunning))
                    return;

                // Recheck after the DTE scan and at the lifecycle boundary: a
                // manual restart (even a failed one) invalidates cold-start work.
                if (coldStartVersion.HasValue && (Volatile.Read(ref _initCount) != 1 ||
                                                  Volatile.Read(ref _lifecycleVersion) != coldStartVersion.Value))
                {
                    return;
                }

                if (crashVersion.HasValue && Volatile.Read(ref _lifecycleVersion) != crashVersion.Value)
                {
                    return;
                }

                Interlocked.Increment(ref _lifecycleVersion);
                var handler = StartAsync;
                if (handler == null)
                {
                    throw new InvalidOperationException(
                        "clangd cannot restart: no language server host is subscribed to StartAsync.");
                }

                await StopServerAsync();
                attempt = new RestartAttempt();
                lock (_initializationGate)
                {
                    _restartAttempt = attempt;
                    _stopping = false;
                }

                startTask = InvokeStartAsync(handler, attempt);

                await WaitForRestartInitializationAsync(startTask, attempt);
                initialized = true;
            }
            finally
            {
                lock (_initializationGate)
                {
                    if (attempt != null && ReferenceEquals(_restartAttempt, attempt))
                    {
                        if (!initialized)
                        {
                            // Includes timeout/Start exceptions: later host
                            // cleanup must not schedule a hidden second Start.
                            _expectingExit = true;
                            _stopping = true;
                            _currentStartContext = null;
                            Interlocked.Increment(ref _lifecycleVersion);
                        }

                        _restartAttempt = null;
                        _initializationPending = false;
                    }
                }

                if (startTask != null && (!startTask.IsCompleted || startTask.IsFaulted))
                {
                    ObserveLateStartAsync(startTask).Forget();
                }

                _lifecycleLock.Release();
            }
        }

        private async Task WaitForFeatureLifecycleReadyAsync(CancellationToken cancellationToken)
        {
            if (!await _lifecycleLock.WaitAsync(ShutdownLockTimeout, cancellationToken))
                throw new TimeoutException("clangd feature apply timed out waiting for the lifecycle lock.");
            _lifecycleLock.Release();
        }

        public async Task ShutdownAsync()
        {
            if (!await _lifecycleLock.WaitAsync(ShutdownLockTimeout))
            {
                throw new TimeoutException("clangd shutdown timed out waiting for the lifecycle lock.");
            }

            try
            {
                await StopServerAsync();
            }
            finally
            {
                // Keep _stopping set until the next explicit Start; callbacks
                // after shutdown must not count as another initialization.
                _lifecycleLock.Release();
            }
        }

        internal bool HasActiveServer()
        {
            lock (_initializationGate)
            {
                return _serverProcess != null;
            }
        }

        internal async Task<bool> ShutdownIfActiveAsync()
        {
            if (!HasActiveServer())
            {
                return false;
            }

            await ShutdownAsync();
            return true;
        }

        private async Task StopServerAsync()
        {
            lock (_initializationGate)
            {
                _stopping = true;
                // Retire queries at the Stop boundary, not after the host finishes stopping.
                Interlocked.Increment(ref _sessionVersion);
                _initializationPending = false;
                _currentStartContext = null;
                Interlocked.Increment(ref _lifecycleVersion);
                _expectingExit = true;
            }

            try
            {
                InlayHintCoordinator.SessionChanged();
                await(StopAsync?.InvokeAsync(this, EventArgs.Empty) ?? Task.CompletedTask);
            }
            finally
            {
                // Never hold the activation semaphore across the broker's Stop:
                // it may be joining a previously started ActivateAsync.
                await _activationLock.WaitAsync();
                try
                {
                    KillServerProcess();
                    _rpc = null;
                    if (ReferenceEquals(Current, this))
                        Current = null;
                    InactiveRegionServices.Store.ClearAll();
                    lock (_initializationGate)
                    {
                        _initializationPending = false;
                    }
                }
                finally
                {
                    _activationLock.Release();
                }
            }
        }

        private Task InvokeStartAsync(AsyncEventHandler<EventArgs> handler, RestartAttempt context)
        {
            lock (_initializationGate)
            {
                if (_stopping || (_restartAttempt != null && !ReferenceEquals(_restartAttempt, context)))
                {
                    throw new InvalidOperationException("clangd cannot start during another lifecycle operation.");
                }

                _currentStartContext = context;
                Interlocked.Increment(ref _lifecycleVersion);
            }

            var previousContext = _startContext.Value;
            try
            {
                _startContext.Value = context;
                return handler.InvokeAsync(this, EventArgs.Empty);
            }
            finally
            {
                _startContext.Value = previousContext;
            }
        }

        // These are SDK event tasks and callback signals, not a separately
        // scheduled JTF operation. They are only awaited asynchronously.
#pragma warning disable VSTHRD003
        private static async Task WaitForRestartInitializationAsync(Task startTask, RestartAttempt attempt)
        {
            using (var timeoutCancellation = new CancellationTokenSource())
            {
                var timeout = Task.Delay(RestartInitializationTimeoutMilliseconds, timeoutCancellation.Token);
                try
                {
                    var first = await Task.WhenAny(startTask, attempt.Completion.Task, timeout);
                    if (first == startTask)
                    {
                        await startTask;
                    }

                    if (await Task.WhenAny(attempt.Completion.Task, timeout) != attempt.Completion.Task)
                    {
                        throw new TimeoutException("clangd restart timed out waiting for initialization (30 seconds).");
                    }

                    var failure = await attempt.Completion.Task;
                    if (failure != null)
                    {
                        throw failure;
                    }

                    if (await Task.WhenAny(startTask, timeout) != startTask)
                    {
                        throw new TimeoutException(
                            "clangd restart timed out waiting for the language server host (30 seconds).");
                    }

                    await startTask;
                }
                finally
                {
                    timeoutCancellation.Cancel();
                }
            }
        }

        private static async Task ObserveLateStartAsync(Task startTask)
        {
            try
            {
                await startTask;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"clangd late Start failed: {ex.Message}");
            }
        }
#pragma warning restore VSTHRD003

        private void OnServerExited(Process process, int exitCode)
        {
            int version;
            lock (_initializationGate)
            {
                if (!ReferenceEquals(_serverProcess, process))
                {
                    return;
                }

                ClangdLog.WriteLine($"clangd exited (code {exitCode}).");
                _serverProcess = null;
                _reusableConnection = null;
                _rpc = null;
                if (ReferenceEquals(Current, this))
                    Current = null;
                InactiveRegionServices.Store.ClearAll();
                NotifySessionChanged();
                _initializationPending = false;
                if (_expectingExit)
                {
                    return;
                }

                if (_restartAttempt != null)
                {
                    _restartAttempt.Completion.TrySetResult(
                        new InvalidOperationException($"clangd exited during restart (code {exitCode})."));
                    return;
                }

                _currentStartContext = null;
                version = _lifecycleVersion;
            }

            if (!ShouldAllowRestart(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
            {
                ClangdLog.WriteLine("clangd crash recovery skipped because native IntelliSense takeover is disabled.");
                return;
            }

            // Settings-driven crash self-healing: a manual Restart/Shutdown sets _expectingExit first,
            // so only an unexpected exit reaches here. Use Task.Run rather than JTF to avoid analyzer
            // false positives. Circuit breaker: stop auto-spawning after the 4th time within 60 seconds
            // (it once restarted endlessly under a misconfiguration); the user restarts from the menu,
            // and the next successful initialization resets the count.
            if (ClangdSettings.Instance.RestartAfterCrash)
            {
                var allowRestart = true;
                lock (_crashGate)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastCrashUtc).TotalSeconds > 60)
                    {
                        _crashCount = 0;
                    }

                    _lastCrashUtc = now;
                    _crashCount++;
                    allowRestart = _crashCount <= 3;
                }

                if (!allowRestart)
                {
                    ClangdLog.WriteLine("clangd crashed repeatedly - auto-restart suspended, use menu restart.");
                    return;
                }

                ClangdLog.WriteLine("clangd crashed - restarting (RestartAfterCrash=true).");
#pragma warning disable VSTHRD110 // Intentional fire-and-forget: background crash-recovery restart; exceptions are caught internally and logged.
                System.Threading.Tasks.Task.Run(() => RestartAfterCrashAsync(version));
#pragma warning restore VSTHRD110
            }
        }

        private async Task RestartAfterCrashAsync(int version)
        {
            try
            {
                await RestartAsync(null, version);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"clangd auto-restart failed: {ex.Message}");
            }
        }

        private void KillServerProcess()
        {
            _expectingExit = true;
            try
            {
                if (_serverProcess != null && !_serverProcess.HasExited)
                {
                    _serverProcess.Kill();
                }
            }
            catch
            {
                // Server already gone.
            }

            _serverProcess?.Dispose();
            _serverProcess = null;
            _reusableConnection = null;
        }

        private static string BuildArguments(List<string> arguments)
        {
            // net472 has no ProcessStartInfo.ArgumentList, so the Arguments string must be built by hand.
            // Rules: quote only when the value contains a space/quote or ends with a backslash; escape
            // embedded quotes as \"; double trailing backslashes so they cannot swallow the closing quote.
            var parts = new List<string>(arguments.Count);
            foreach (var raw in arguments)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                if (raw.IndexOfAny(new[] {
                        ' ',
                        '\t',
                        '"',
                    }) < 0)
                {
                    parts.Add(raw);
                    continue;
                }

                var escaped = raw.Replace("\"", "\\\"");
                var trailing = 0;
                for (var i = escaped.Length - 1; i >= 0 && escaped[i] == '\\'; i--)
                {
                    trailing++;
                }

                if (trailing > 0)
                {
                    escaped += new string('\\', trailing);
                }

                parts.Add("\"" + escaped + "\"");
            }

            return string.Join(" ", parts);
        }

        private static async Task DrainStandardError(TextReader reader, bool log)
        {
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (log && line.Length > 0)
                {
                    ClangdLog.WriteLine("[stderr] " + line);
                }
            }
        }

        private static string GetWorkspaceRoot()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                var solution = dte?.Solution;
                if (solution != null && !string.IsNullOrEmpty(solution.FullName))
                {
                    return Path.GetDirectoryName(solution.FullName);
                }

                if (dte?.ActiveDocument != null)
                {
                    return Path.GetDirectoryName(dte.ActiveDocument.FullName);
                }
            }
            catch
            {
                // Not on UI thread or DTE unavailable.
            }

            return Environment.CurrentDirectory;
        }

        /// <summary>Receives clangd custom notifications, e.g. textDocument/clangd.fileStatus.</summary>
        internal sealed class ClangdCustomTarget
        {
            // Registered by method name via StreamJsonRpc's mapping conventions.
            public void OnFileStatus(object arg)
            {
                ClangdLog.WriteLine("fileStatus: " + arg);
            }

            [JsonRpcMethod("textDocument/inactiveRegions", UseSingleObjectParameterDeserialization = true)]
            public void OnInactiveRegions(Newtonsoft.Json.Linq.JObject arg)
            {
                InactiveRegionNotification.OnInactiveRegions(arg);
            }
        }
    }
}
