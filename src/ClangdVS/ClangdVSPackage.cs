using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Task = System.Threading.Tasks.Task;

namespace ClangdVS
{
    /// <summary>
    /// clangd for Visual Studio - classic in-proc VSIX package hosting the
    /// Tools menu commands, the Tools &gt; Options page and the native
    /// IntelliSense takeover logic. The LSP client itself is the MEF-exported
    /// <see cref="ClangdLanguageClient"/>.
    /// Ported from the vscode-clangd extension (https://github.com/clangd/vscode-clangd).
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(ClangdOptionsPage), "clangd", "常规", 0, 0, true)]
    [ProvideToolWindow(typeof(ClangdInspectionToolWindow))]
    [ProvideAutoLoad(UIContextGuids.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class ClangdVSPackage : AsyncPackage
    {
        public const string PackageGuidString = "7E1B3A54-64C1-4F0B-9A2D-52C4F1C6C999";
        public const string CommandSetGuidString = "9A2C5E77-3D44-4E5F-8B6A-0C1D2E3F4A55";

        private static readonly int[] CommandIds = {
            0x1100, // cmdidSwitchHeaderSource
            0x1101, // cmdidRestartClangd
            0x1102, // cmdidToggleTakeover
            0x1103, // cmdidSwitchHeaderSourceContext
            0x1104, // cmdidViewClangdAst
            0x1105, // cmdidViewClangdSymbolInfo
            0x1106, // cmdidViewClangdTypeHierarchy
        };

        protected override async Task InitializeAsync(CancellationToken cancellationToken,
                                                      IProgress<ServiceProgressData> progress)
        {
            _brokerLoadGate = new BrokerLoadGate(JoinableTaskFactory);
            ClangdSettings.TakeoverSettingChanged += OnTakeoverSettingChanged;
            ClangdSettings.FeatureSettingsChanged += OnFeatureSettingsChanged;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                RegisterCommands(commandService);
            }
            else
            {
                ClangdLog.WriteLine("diag: menu command service unavailable during package init.");
            }

            // Priority command target: takes over Edit.GoToDefinition ahead of the native C++ handler
            // (F12 / context-menu go-to-definition use this command; Ctrl+click is handled by a separate mouse handler). Once the native
            // chain is suppressed by takeover, the key silently dies; fan-out features such as hover are unaffected.
            if (await GetServiceAsync(typeof(SVsRegisterPriorityCommandTarget))
                    is IVsRegisterPriorityCommandTarget priorityCommands)
            {
                _priorityCommandTarget = new ClangdNavigationTarget(this);
                var hr = priorityCommands.RegisterPriorityCommandTarget(0, _priorityCommandTarget,
                                                                        out _priorityCommandCookie);
                ClangdLog.WriteLine(
                    $"nav: priority command target registered hr=0x{hr:X8} cookie={_priorityCommandCookie}.");
            }
            else
            {
                ClangdLog.WriteLine("nav: priority command target service unavailable.");
            }

            // DTE document events: attach view filters for C++ documents opened later (complementary to the call inside resend).
            // The Events object must be held at field level, or the GC eats it and the events are cut off.
            try
            {
                if (await GetServiceAsync(typeof(DTE)) is DTE dteForEvents)
                {
                    _dteEvents = dteForEvents.Events;
                    _docEvents = _dteEvents.DocumentEvents;
                    _docEvents.DocumentOpened += OnDocumentOpened;
                    _windowEvents = _dteEvents.WindowEvents;
                    _windowEvents.WindowActivated += OnWindowActivated;
                    ClangdLog.WriteLine("nav: DTE DocumentOpened event wired.");
                }
                else
                {
                    ClangdLog.WriteLine("nav: DTE service unavailable; DocumentOpened event not wired.");
                }
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"nav: document events wire-up failed: {ex.Message}");
            }

            // Attach filters immediately to views that already exist (restored by cold start); documents opened later go through the events above.
            ScheduleEnsureViewFilters("package-init");

            // Manual broker load, LAZY: VS 2026's automatic content-type
            // activation does not dispatch to us for C/C++ (the built-in
            // languageServerProvider-C/C++ owns that flow in solution mode),
            // so load the client through ILanguageClientBroker.LoadAsync -
            // the documented path for loading a client without relying on
            // automatic MEF activation.
            //
            // Timing is critical (proven empirically): loading immediately at init means the restored
            // documents are not open yet, the broker loads nothing, and the host never comes back to take
            // over existing buffers afterward → the whole cold start has no didOpen and no commands
            // (Edit.GoToDefinition shows "unavailable").
            // Instead, wait until the first C/C++ document appears and then load, so the host takes over
            // the existing buffers too; if none appears within 30 seconds, load as usual (documents opened
            // afterward go through the automatic pipeline).
            if (ShouldLoadClangd(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
                JoinableTaskFactory.RunAsync(() => EnsureBrokerLoadAsync());

            // Apply the IntelliSense takeover on startup if configured.
            if (ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
            {
                ApplyTakeover(enabled: true);
            }

            ClangdLog.WriteLine("clangd extension loaded.");
        }

        /// <summary>
        /// Loads the clangd language client through the broker manually,
        /// bypassing automatic content-type activation. See
        /// ILanguageClientBroker.LoadAsync ("used when one chooses to load an
        /// ILanguageClient without going through MEF").
        /// </summary>
        private BrokerLoadGate _brokerLoadGate;

        private ClangdLanguageClient _languageClient;

        private int _clientIdentityLogged;

        // DTE event holders (to prevent GC) and the views whose filters are already attached (weak references to prevent leaks).
        private EnvDTE.Events _dteEvents;

        private EnvDTE.DocumentEvents _docEvents;

        private EnvDTE.WindowEvents _windowEvents;

        private ClangdNavigationTarget _priorityCommandTarget;

        private uint _priorityCommandCookie;

        private int _viewFilterEnsureRunning;

        private int _viewFilterEnsurePending;

        private int _viewFilterScanSequence;

        private readonly System.Runtime.CompilerServices
            .ConditionalWeakTable<IVsTextView, ClangdViewCommandFilter> _filteredViews =
            new System.Runtime.CompilerServices.ConditionalWeakTable<IVsTextView, ClangdViewCommandFilter>();

        private MouseNavigationAttachment _mouseAttachment;

        private long _takeoverSequence;

        private CancellationTokenSource _inspectionCancellation;

        private long _inspectionSequence;

        private ClangdInspectionToolWindow _inspectionWindow;

        private int _inspectionCommandId;

        private readonly FeatureSettingsApplyCoordinator _featureSettingsApply = new FeatureSettingsApplyCoordinator();

        private int _featureSettingsDisposed;

        /// <summary>
        /// Condition-waiting load variant: polls (conditionally, not blindly waiting) for the first C/C++ document for at most ~30 seconds.
        /// </summary>
        private async Task EnsureBrokerLoadAsync()
        {
            try
            {
                var sawDoc = false;
                for (var i = 0; i < 15; i++)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (HasOpenCppDocument())
                    {
                        sawDoc = true;
                        break;
                    }

                    await System.Threading.Tasks.TaskScheduler.Default;
                    await System.Threading.Tasks.Task.Delay(2000);
                }

                ClangdLog.WriteLine($"clangd: broker ensure-load trigger (docSeen={sawDoc}).");
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    if (await _brokerLoadGate.EnsureLoadedAsync(LoadClangdClientManuallyAsync))
                    {
                        return;
                    }

                    await TaskScheduler.Default;
                    await Task.Delay(1000);
                }

                ClangdLog.WriteLine("clangd: broker load retries exhausted; a document event or command can retry.");
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"clangd: broker ensure-load failed: {ex.Message}");
            }
        }

        /// <summary>Whether an open C/C++ document exists (called on the DTE main thread).</summary>
        private bool HasOpenCppDocument()
        {
            try
            {
                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    return false;
                }

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

                    if (!string.IsNullOrEmpty(path) && IsCppFile(path))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // DTE not ready; retry next round.
            }

            return false;
        }

        private void OnDocumentOpened(EnvDTE.Document document)
        {
            string path = null;
            try
            {
                path = document?.FullName;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(path) || !IsCppFile(path))
            {
                return;
            }

            ClangdLog.WriteLine($"nav: C++ document opened, scheduling view scan: {path}");
            ScheduleEnsureViewFilters("document-opened");
            if (ShouldLoadClangd(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
                JoinableTaskFactory.RunAsync(() => _brokerLoadGate.EnsureLoadedAsync(LoadClangdClientManuallyAsync));
        }

        private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            if (IsCppDocumentActive())
            {
                ScheduleEnsureViewFilters("window-activated");
                if (ShouldLoadClangd(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
                    JoinableTaskFactory.RunAsync(() =>
                                                     _brokerLoadGate.EnsureLoadedAsync(LoadClangdClientManuallyAsync));
            }
        }

        private void ScheduleEnsureViewFilters(string reason)
        {
            if (Interlocked.CompareExchange(ref _viewFilterEnsureRunning, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _viewFilterEnsurePending, 1);
                return;
            }

            JoinableTaskFactory.RunAsync(() => EnsureViewFiltersAsync(reason));
        }

        /// <summary>
        /// Attaches command filters to all C/C++ views (package-side direct connection, not through MEF).
        /// The weak table deduplicates them, and they are released as views close. A document opening and
        /// package initialization may both precede the core editor creating the text view, so scanning
        /// continues through a bounded recovery window; after the window ends, document open/activate
        /// events keep triggering it, and events arriving during a scan schedule the next round.
        /// </summary>
        private async Task EnsureViewFiltersAsync(string reason)
        {
            try
            {
                ViewFilterScanResult result = null;
                for (var attempt = 1; attempt <= 10; attempt++)
                {
                    if (attempt > 1)
                    {
                        await TaskScheduler.Default;
                        await Task.Delay(500);
                    }

                    result = await ScanAndAttachViewFiltersAsync(reason, attempt);
                    if (result.Attached > 0)
                    {
                        ClangdLog.WriteLine(
                            $"nav: view scan attached reason={reason} attempt={attempt} views={result.Views} " +
                            $"cpp={result.CppViews} attached={result.Attached} existing={result.AlreadyAttached}.");
                        // Restored documents and split views may appear after the first
                        // existing C++ view. Keep scanning through this bounded window.
                    }
                }

                ClangdLog.WriteLine(
                    $"nav: view scan finished reason={reason} attempts=10 cpp={result?.CppViews ?? 0}.");
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"nav: ensure view filters failed reason={reason}: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _viewFilterEnsureRunning, 0);
                if (Interlocked.Exchange(ref _viewFilterEnsurePending, 0) != 0)
                {
                    ScheduleEnsureViewFilters("pending-document-event");
                }
            }
        }

        private async Task<ViewFilterScanResult> ScanAndAttachViewFiltersAsync(string reason, int attempt)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var scanId = Interlocked.Increment(ref _viewFilterScanSequence);
            ClangdLog.WriteLine($"nav: view scan start id={scanId} reason={reason} attempt={attempt}.");

            var componentModelObject = await GetServiceAsync(typeof(SComponentModel));
            var componentModel = componentModelObject as IComponentModel;
            ClangdLog.WriteLine(
                $"nav: view scan id={scanId} componentModel={(componentModel == null ? "null" : componentModel.GetType().FullName)}.");
            var adapters = componentModel?.GetService<Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService>();
            ClangdLog.WriteLine(
                $"nav: view scan id={scanId} adapters={(adapters == null ? "null" : adapters.GetType().FullName)}.");

            var textManagerObject = await GetServiceAsync(typeof(SVsTextManager));
            var textManager = textManagerObject as IVsTextManager;
            var rdt = await GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            ClangdLog.WriteLine(
                $"nav: view scan id={scanId} textManager={(textManager == null ? "null" : textManager.GetType().FullName)}.");
            if (textManager == null || rdt == null)
            {
                ClangdLog.WriteLine($"nav: view scan id={scanId} aborted: required editor service unavailable.");
                return new ViewFilterScanResult();
            }

            var result = new ViewFilterScanResult();
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_mouseAttachment == null)
            {
                _mouseAttachment = new MouseNavigationAttachment(
                    (wpf, source, documents, navigator, classifier) =>
                        new ClangdMouseNavigation(wpf, source, documents, navigator, classifier,
                                                  () => ClangdSettings.Instance.TakeOverFromNativeIntelliSense,
                                                  action =>
                                                  { _ = JoinableTaskFactory.RunAsync(action); },
                                                  GoToDefinitionAtPositionAsync),
                    ClangdLog.WriteLine);
            }
            foreach (var view in EnumerateDocumentViews(rdt, textManager))
            {
                result.Views++;
                if (view == null)
                {
                    ClangdLog.WriteLine($"nav: view scan id={scanId} view#{result.Views} is null.");
                    continue;
                }

                try
                {
                    var existingFilter = _filteredViews.TryGetValue(view, out _);
                    if (adapters == null)
                    {
                        _mouseAttachment.AttachMouseNavigation(view, null, adapters, componentModel, existingFilter);
                        continue;
                    }
                    var getBufferHr = view.GetBuffer(out var lines);
                    if (getBufferHr != VSConstants.S_OK || lines == null)
                    {
                        ClangdLog.WriteLine(
                            $"nav: view scan id={scanId} view#{result.Views} view={MouseNavigationAttachment.Identity(view)} GetBuffer hr=0x{getBufferHr:X8} lines={(lines == null ? "null" : lines.GetType().FullName)}.");
                        _mouseAttachment.AttachMouseNavigation(view, null, adapters, componentModel, existingFilter);
                        continue;
                    }

                    var buffer = adapters.GetDataBuffer(lines);
                    try
                    {
                        var registry = componentModel?.GetService<IContentTypeRegistryService>();
                        if (registry != null && buffer != null)
                        {
                            TakeoverContentTypeRouter.EnsureBufferRouted(
                                buffer, registry, ClangdSettings.Instance.TakeOverFromNativeIntelliSense,
                                ClangdLog.WriteLine);
                        }
                    }
                    catch
                    { /* Routing must not interrupt navigation attachment. */
                    }
                    try
                    {
                        ClangdHoverDiagnostics.Observe(buffer, adapters.GetWpfTextView(view), "package-view-scan");
                    }
                    catch
                    { /* Diagnostic adapter lookup must not interrupt navigation attachment. */
                    }
                    var contentType = buffer?.ContentType;
                    var contentTypeName = contentType?.TypeName ?? "<null>";
                    var isCpp =
                        contentType != null && (contentType.IsOfType("C/C++") || contentType.IsOfType("cpp-clangd"));
                    ClangdLog.WriteLine(
                        $"nav: view scan id={scanId} view#{result.Views} view={MouseNavigationAttachment.Identity(view)} type={contentTypeName} cpp={isCpp} alreadyAttached={existingFilter}.");
                    if (!isCpp)
                    {
                        if (buffer == null || existingFilter)
                            _mouseAttachment.AttachMouseNavigation(view, buffer, adapters, componentModel,
                                                                   existingFilter);
                        continue;
                    }

                    result.CppViews++;
                    MouseNavigationAttachment.EnsureViewNavigation(
                        existingFilter,
                        () => _mouseAttachment.AttachMouseNavigation(view, buffer, adapters, componentModel,
                                                                     existingFilter),
                        () => AttachCommandFilter(view, scanId, result));
                    if (existingFilter)
                    {
                        result.AlreadyAttached++;
                    }
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine(
                        $"nav: view scan id={scanId} view#{result.Views} view={MouseNavigationAttachment.Identity(view)} failed: {ex}");
                }
            }

            return result;
        }

        private void AttachCommandFilter(IVsTextView view, int scanId, ViewFilterScanResult result)
        {
            var filter = new ClangdViewCommandFilter(this);
            var addHr = view.AddCommandFilter(filter, out var nextTarget);
            ClangdLog.WriteLine(
                $"nav: AddCommandFilter id={scanId} view#{result.Views} view={MouseNavigationAttachment.Identity(view)} hr=0x{addHr:X8} next={(nextTarget == null ? "null" : nextTarget.GetType().FullName)}.");
            if (addHr != VSConstants.S_OK)
                return;
            filter.SetNextTarget(nextTarget);
            _filteredViews.Add(view, filter);
            result.Attached++;
        }

        // The SDK permits EnumViews(null), but the current host returns E_INVALIDARG.
        // RDT doc data supplies valid buffers, including provider-backed documents.
        internal static IEnumerable<IVsTextView> EnumerateDocumentViews(IVsRunningDocumentTable rdt,
                                                                        IVsTextManager textManager)
        {
            var result = new List<IVsTextView>();
            var seenBuffers = new HashSet<IVsTextBuffer>();
            var seenViews = new HashSet<IVsTextView>();
            if (rdt.GetRunningDocumentsEnum(out var documents) != VSConstants.S_OK || documents == null)
            {
                return result;
            }

            var cookies = new uint[1];
            while (documents.Next(1, cookies, out var count) == VSConstants.S_OK && count == 1)
            {
                var docData = IntPtr.Zero;
                try
                {
                    var hr = rdt.GetDocumentInfo(cookies[0], out _, out _, out _, out _, out _, out _, out docData);
                    if (hr != VSConstants.S_OK || docData == IntPtr.Zero)
                    {
                        continue;
                    }

                    var data = Marshal.GetObjectForIUnknown(docData);
                    var buffer = data as IVsTextBuffer;
                    if (buffer == null && data is IVsTextBufferProvider provider &&
                        provider.GetTextBuffer(out var lines) == VSConstants.S_OK)
                    {
                        buffer = lines;
                    }

                    if (buffer == null || !seenBuffers.Add(buffer))
                    {
                        continue;
                    }

                    hr = textManager.EnumViews(buffer, out var views);
                    if (hr != VSConstants.S_OK || views == null)
                    {
                        ClangdLog.WriteLine($"nav: buffer EnumViews failed cookie={cookies[0]} hr=0x{hr:X8}.");
                        continue;
                    }

                    var item = new IVsTextView[1];
                    uint fetched = 0;
                    while (views.Next(1, item, ref fetched) == VSConstants.S_OK && fetched == 1)
                    {
                        if (item[0] != null && seenViews.Add(item[0]))
                        {
                            result.Add(item[0]);
                        }

                        fetched = 0;
                    }
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine($"nav: RDT view enumeration failed cookie={cookies[0]}: {ex.Message}");
                }
                finally
                {
                    // Balance GetDocumentInfo's AddRef even on non-text/failure paths.
                    if (docData != IntPtr.Zero)
                    {
                        Marshal.Release(docData);
                    }
                }
            }

            return result;
        }

        internal static IEnumerable<IVsTextBuffer> EnumerateRunningTextBuffers(IVsRunningDocumentTable rdt)
        {
            var result = new List<IVsTextBuffer>();
            var seen = new HashSet<IVsTextBuffer>();
            if (rdt == null || rdt.GetRunningDocumentsEnum(out var documents) != VSConstants.S_OK || documents == null)
            {
                return result;
            }

            var cookies = new uint[1];
            while (documents.Next(1, cookies, out var count) == VSConstants.S_OK && count == 1)
            {
                var docData = IntPtr.Zero;
                try
                {
                    if (rdt.GetDocumentInfo(cookies[0], out _, out _, out _, out _, out _, out _, out docData) !=
                            VSConstants.S_OK ||
                        docData == IntPtr.Zero)
                    {
                        continue;
                    }

                    var data = Marshal.GetObjectForIUnknown(docData);
                    var buffer = data as IVsTextBuffer;
                    if (buffer == null && data is IVsTextBufferProvider provider &&
                        provider.GetTextBuffer(out var lines) == VSConstants.S_OK)
                    {
                        buffer = lines;
                    }

                    if (buffer != null && seen.Add(buffer))
                    {
                        result.Add(buffer);
                    }
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine($"takeover: RDT buffer enumeration failed cookie={cookies[0]}: {ex.Message}");
                }
                finally
                {
                    if (docData != IntPtr.Zero)
                    {
                        Marshal.Release(docData);
                    }
                }
            }

            return result;
        }

        private static HashSet<ITextBuffer> CollectRunningDataBuffers(
            IVsRunningDocumentTable rdt, Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService adapters)
        {
            var buffers = new HashSet<ITextBuffer>();
            if (adapters == null)
            {
                return buffers;
            }

            foreach (var textBuffer in EnumerateRunningTextBuffers(rdt))
            {
                try
                {
                    var lines = textBuffer as IVsTextLines;
                    if (lines == null)
                    {
                        continue;
                    }

                    var buffer = adapters.GetDataBuffer(lines);
                    if (buffer != null)
                    {
                        buffers.Add(buffer);
                    }
                }
                catch
                {
                    // One document must not block the remaining takeover restore.
                }
            }

            return buffers;
        }

        private sealed class ViewFilterScanResult
        {
            public int Views;

            public int CppViews;

            public int Attached;

            public int AlreadyAttached;
        }

        private async Task<bool> LoadClangdClientManuallyAsync()
        {
            try
            {
                if (!ShouldLoadClangd(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
                {
                    ClangdLog.WriteLine(
                        "clangd: manual load skipped because native IntelliSense takeover is disabled.");
                    return false;
                }

                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                if (componentModel == null)
                {
                    ClangdLog.WriteLine("clangd: manual load - IComponentModel unavailable.");
                    return false;
                }

                var broker =
                    componentModel.DefaultExportProvider.GetExportedValues<ILanguageClientBroker>().FirstOrDefault();
                var client = componentModel.DefaultExportProvider.GetExportedValueOrDefault<ClangdLanguageClient>();

                if (Interlocked.Exchange(ref _clientIdentityLogged, 1) == 0)
                {
                    try
                    {
                        var interfaceExports =
                            componentModel.DefaultExportProvider.GetExports<ILanguageClient>().Count();
                        ClangdLog.WriteLine(
                            $"clangd: MEF identity interfaceExports={interfaceExports} concreteExport={client != null} " +
                            $"package={typeof(ClangdVSPackage).Assembly.Location} " +
                            $"api={typeof(ILanguageClient).Assembly.FullName} apiPath={typeof(ILanguageClient).Assembly.Location}.");
                        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name ==
                                                                                                    "ClangdVS"))
                        {
                            ClangdLog.WriteLine(
                                $"clangd: loaded client assembly={assembly.FullName} path={assembly.Location} mvid={assembly.ManifestModule.ModuleVersionId}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        ClangdLog.WriteLine($"clangd: MEF identity diagnostics failed: {ex.Message}");
                    }
                }

                if (broker == null || client == null)
                {
                    ClangdLog.WriteLine(
                        $"clangd: manual load skipped (broker={broker != null}, client={client != null}).");
                    return false;
                }

                ClangdLog.WriteLine("clangd: manual broker LoadAsync starting.");
                ClangdHoverDiagnostics.Lifecycle("broker-load-start", client);
                await broker.LoadAsync(new ClangdClientMetadata(), client);
                _languageClient = client;
                ClangdHoverDiagnostics.Lifecycle("broker-load-returned", client);
                ClangdLog.WriteLine("clangd: manual broker LoadAsync completed.");
                return true;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"clangd: manual broker LoadAsync failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Metadata for the manual broker load. ClientName MUST be null:
        /// the broker matches clients via
        /// DoesClientNameMatch(metadata.ClientName, buffer.GetClientName()),
        /// and ordinary document buffers have no ClientName property (null) -
        /// a non-null ClientName here would make the client applicable to
        /// no buffer at all (decompiled VS 18.9 LSP host evidence).
        /// </summary>
        private sealed class ClangdClientMetadata : ILanguageClientMetadata
        {
            // "C/C++" matches .cpp/.h buffers by inheritance;
            // "cpp-clangd" is the bridged content type registered in
            // ClangdContentTypeBridge (base: C/C++ + code-languageserver-preview)
            // so C/C++ buffers enter the LSP document pipeline.
            public IEnumerable<string> ContentTypes => new[] { "cpp-clangd" };

            public string ClientName => null;
        }

        private const int CmdIdSelfTest = 0x11FF; // registered in code only, never appears in vsct

        private void RegisterCommands(OleMenuCommandService commandService)
        {
            var commandSet = new Guid(CommandSetGuidString);
            foreach (var id in CommandIds)
            {
                // Four-argument constructor = (invoke, changeHandler, beforeQueryStatus, id).
                // Per the VSSDK XML docs: in the three-argument form the 2nd argument is changeHandler
                // (called only when the state changes; menu queries do not go through it), so
                // beforeQueryStatus can only go in the 3rd slot of the four-argument form.
                // Previously using the three-argument form meant QueryStatus never ran, so menu check/disable state never refreshed.
                var command = new OleMenuCommand(ExecuteCommand, null, QueryStatus, new CommandID(commandSet, id));
                commandService.AddCommand(command);
            }

            commandService.AddCommand(
                new OleMenuCommand(ExecuteCommand, null, QueryStatus, new CommandID(commandSet, CmdIdSelfTest)));

            var asm = typeof(ClangdVSPackage).Assembly.GetName();
            ClangdLog.WriteLine(
                $"commands registered: {CommandIds.Length} in {{{CommandSetGuidString}}} v{asm.Version}");

            // Self-test: dispatch once directly inside VS, without relying on menu/shortcut routing.
            // If not even this line appears, ExecuteCommand/logging itself is broken;
            // if it does appear, then a user click with no log means VS is not routing the command to this package.
            var invoked = commandService.GlobalInvoke(new CommandID(commandSet, CmdIdSelfTest));
            ClangdLog.WriteLine($"cmd self-test GlobalInvoke -> {invoked}");
        }

        private void QueryStatus(object sender, EventArgs e)
        {
            if (!(sender is OleMenuCommand command))
            {
                return;
            }

            var id = command.CommandID.ID;
            try
            {
                var requiresCppDocument = id == 0x1100 || id == 0x1103 || id >= 0x1104 && id <= 0x1106;
                var takeoverEnabled = ClangdSettings.Instance.TakeOverFromNativeIntelliSense;
                var queryFeatureEnabled = id < 0x1104 || id > 0x1106 || takeoverEnabled;
                var featureEnabled = id == 0x1104 ? ClangdSettings.Instance.EnableAstView
                                    : id == 0x1105 ? ClangdSettings.Instance.EnableSymbolInfo
                                    : id == 0x1106 ? ClangdSettings.Instance.EnableTypeHierarchy
                                    : true;
                if (id == 0x1100 || id == 0x1103)
                    featureEnabled &= ClangdSettings.Instance.EnableNavigation;
                command.Enabled = queryFeatureEnabled && featureEnabled &&
                                  (requiresCppDocument
                                       ? IsCppDocumentActive() && IsClangdCommandEnabled(takeoverEnabled, id)
                                       : IsClangdCommandEnabled(takeoverEnabled, id));
                if (id == 0x1102)
                {
                    command.Checked = ClangdSettings.Instance.TakeOverFromNativeIntelliSense;
                }
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"cmd querystatus 0x{id:X4} threw: {ex.Message}");
                return;
            }
        }

        private bool IsCppDocumentActive()
        {
            try
            {
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var document = dte?.ActiveDocument;
                if (document == null)
                {
                    return false;
                }

                return IsCppFile(document.FullName);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCppFile(string path) => ClangdFileExtensions.IsCppFile(path);

        private void ExecuteCommand(object sender, EventArgs e)
        {
            var id = (sender as OleMenuCommand)?.CommandID.ID ?? 0;
            ClangdLog.WriteLine(
                $"command invoked: 0x{id:X4} (sender={(sender == null ? "null" : sender.GetType().Name)})");
            switch (id)
            {
            case 0x1100:
            case 0x1103:
                JoinableTaskFactory.RunAsync(() => SwitchHeaderSourceAsync());
                break;
            case 0x1101:
                JoinableTaskFactory.RunAsync(() => RestartClangdAsync());
                break;
            case 0x1102:
                JoinableTaskFactory.RunAsync(ToggleTakeoverAsync);
                break;
            case 0x1104:
            case 0x1105:
            case 0x1106:
                JoinableTaskFactory.RunAsync(() => InspectClangdAsync(id));
                break;
            default:
                ClangdLog.WriteLine($"command invoked: unknown id 0x{id:X4}, ignored.");
                break;
            }
        }

        // --- Command handlers (ported from vscode-clangd commands) ---

        private static bool IsInspectionEnabled(int commandId)
        {
            var settings = ClangdSettings.Instance;
            return settings.TakeOverFromNativeIntelliSense &&
                (commandId == 0x1104 ? settings.EnableAstView :
                 commandId == 0x1105 ? settings.EnableSymbolInfo :
                 commandId == 0x1106 && settings.EnableTypeHierarchy);
        }

        private static bool IsInspectionContextCurrent(int commandId, object document, object activeDocument,
                                                       ClangdLanguageClient client, long sessionVersion) =>
            IsInspectionEnabled(commandId) && document != null && ReferenceEquals(document, activeDocument) &&
            client != null && client.IsRunning && client.SessionVersion == sessionVersion &&
            ReferenceEquals(client, ClangdLanguageClient.Current);

        private void InvalidateInspection()
        {
            var sequence = Interlocked.Increment(ref _inspectionSequence);
            var previous = Interlocked.Exchange(ref _inspectionCancellation, null);
            previous?.Cancel();
            previous?.Dispose();
            var window = _inspectionWindow;
            if (window == null)
                return;
            // Settings callbacks can arrive from a watcher thread. Do not touch WPF there.
            var dispatcher = (window.Content as System.Windows.UIElement)?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            void Clear()
            {
                if (sequence == Volatile.Read(ref _inspectionSequence))
                    window.InvalidateResult();
            }
            if (dispatcher.CheckAccess())
                Clear();
            else
                dispatcher.BeginInvoke(new Action(Clear));
        }

        private static async Task CompleteInspectionAsync(Task<Newtonsoft.Json.Linq.JToken> pending,
            CancellationToken token, Func<Action, Task> onMainThread, Func<bool> isCurrent,
            ClangdInspectionToolWindow window, string title)
        {
            Newtonsoft.Json.Linq.JToken result = null;
            Exception failure = null;
            try { result = await pending; }
            catch (Exception ex) { failure = ex; }
            await onMainThread(() =>
            {
                // The transport may complete (or fail) after cancellation. Gate both paths on the UI thread.
                if (token.IsCancellationRequested || !isCurrent())
                    return;
                if (failure == null)
                    window.ShowResult(title, result, isCurrent);
                else
                {
                    ClangdLog.WriteLine($"inspect: {title} request failed: {failure}");
                    window.ShowFailure(title, failure);
                }
            });
        }

        private async Task InspectClangdAsync(int commandId)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Volatile.Read(ref _featureSettingsDisposed) != 0 || !IsInspectionEnabled(commandId))
                return;

            InvalidateInspection();
            _inspectionCommandId = commandId;
            var sequence = Volatile.Read(ref _inspectionSequence);
            var request = new CancellationTokenSource();
            var token = request.Token;
            Interlocked.Exchange(ref _inspectionCancellation, request);

            var dte = await GetServiceAsync(typeof(DTE)) as DTE;
            var document = dte?.ActiveDocument;
            if (document == null || !IsCppFile(document.FullName))
                return;
            var selection = document.Selection as TextSelection;
            if (selection == null)
                return;
            var position = selection.ActivePoint;
            var line = Math.Max(0, position.Line - 1);
            var character = Math.Max(0, position.LineCharOffset - 1);
            var documentPath = document.FullName;
            var uri = new Uri(documentPath).AbsoluteUri;
            var title = commandId == 0x1104 ? "AST" : commandId == 0x1105 ? "Symbol Info" : "Type Hierarchy";
            var toolWindow = await FindToolWindowAsync(typeof(ClangdInspectionToolWindow), 0, true,
                                                       CancellationToken.None) as ClangdInspectionToolWindow;
            if (toolWindow == null)
                return;
            if (token.IsCancellationRequested || sequence != Volatile.Read(ref _inspectionSequence) ||
                !IsInspectionEnabled(commandId) || !ReferenceEquals(document, dte.ActiveDocument))
                return;
            _inspectionWindow = toolWindow;
            ((IVsWindowFrame)toolWindow.Frame).Show();

            var client = _languageClient ?? await ResolveMefClientAsync();
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (token.IsCancellationRequested || sequence != Volatile.Read(ref _inspectionSequence) ||
                !IsInspectionEnabled(commandId) || !ReferenceEquals(document, dte.ActiveDocument))
                return;
            if (client == null || !client.IsRunning)
            {
                toolWindow.ShowFailure(title, new InvalidOperationException("clangd 当前未运行。"));
                return;
            }
            var sessionVersion = client.SessionVersion;
            bool IsCurrent()
            {
                if (token.IsCancellationRequested || sequence != Volatile.Read(ref _inspectionSequence) ||
                    Volatile.Read(ref _featureSettingsDisposed) != 0)
                    return false;
                try { return IsInspectionContextCurrent(commandId, document, dte.ActiveDocument, client, sessionVersion); }
                catch (COMException) { return false; } // A closed DTE document may have disconnected its RCW.
            }
            if (!IsCurrent())
                return;
            var pending = commandId == 0x1104 ? client.RequestAstAsync(uri, line, character, token) :
                          commandId == 0x1105 ? client.RequestSymbolInfoAsync(uri, line, character, token) :
                          client.RequestTypeHierarchyAsync(uri, line, character, token);
            await CompleteInspectionAsync(pending, token,
                async publish => { await JoinableTaskFactory.SwitchToMainThreadAsync(); publish(); },
                IsCurrent, toolWindow, title);
        }

        /// <summary>clangd.switchheadersource - see switch-source-header.ts.</summary>
        private async Task SwitchHeaderSourceAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense ||
                !ClangdSettings.Instance.EnableNavigation)
                return;

            var dte = (DTE)Package.GetGlobalService(typeof(DTE));
            var document = dte?.ActiveDocument;
            if (document == null || !IsCppFile(document.FullName))
            {
                return;
            }

            var client = await GetLanguageClientAsync();
            if (client == null)
            {
                ShowMessage("无法加载 clangd 客户端；请查看 clangd 日志中的 MEF / broker 错误。");
                return;
            }

            var textDocument = document.Object("TextDocument") as EnvDTE.TextDocument;
            string documentText = null;
            try
            {
                // Full text: StartPoint → EndPoint. The old code's GetText(null) had unclear semantics and
                // could return empty for large documents, making clangd report a non-added document.
                documentText = textDocument?.StartPoint?.CreateEditPoint()?.GetText(textDocument.EndPoint);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"switch header: read document text failed: {ex.Message}");
            }

            var target = await client.SwitchSourceHeaderAsync(document.FullName, documentText);
            if (string.IsNullOrEmpty(target))
            {
                ShowMessage("未找到对应的头/源文件。");
                return;
            }

            // The LSP uri may be file:///C:/...
            var path = target;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
            }

            if (!File.Exists(path))
            {
                ShowMessage($"对应文件“{path}”不存在。");
                return;
            }

            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindCode);
        }

        /// <summary>clangd.restart - restarts the language server.</summary>
        private async Task RestartClangdAsync()
        {
            if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
                return;

            var client = await GetLanguageClientAsync();
            if (client == null)
            {
                ShowMessage("无法加载 clangd 客户端；请查看 clangd 日志中的 MEF / broker 错误。");
                return;
            }

            ShowMessage("正在重启 clangd，等待初始化……");
            try
            {
                await client.RestartAsync();
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowMessage("clangd 已重启并完成初始化。");
            }
            catch (Exception ex)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ClangdLog.WriteLine($"clangd: manual restart failed: {ex}");
                ShowMessage($"clangd 重启失败：{ex.Message}");
            }
        }

        /// <summary>
        /// Master gate for navigation takeover: takeover on + active document is C++. QueryStatus hot path, no logging.
        /// </summary>
        internal bool ShouldHandleNavigation()
        {
            try
            {
                if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense ||
                    !ClangdSettings.Instance.EnableNavigation)
                {
                    return false;
                }

                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var path = dte?.ActiveDocument?.FullName;
                return !string.IsNullOrEmpty(path) && IsCppFile(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Execution of Edit.GoToDefinition (shared by the menu and view chains).</summary>
        internal int ExecGotoCommand(ref Guid pguidCmdGroup, uint nCmdID, Guid commandSet, uint gotoDefnId)
        {
            if (pguidCmdGroup != commandSet || nCmdID != gotoDefnId || !ShouldHandleNavigation())
            {
                return ClangdNavigationTarget.NotHandled;
            }

            try
            {
                // UI thread: Run + Join with a message pump; the local loopback is usually <100ms.
                // On successful navigation return S_OK to take it exclusively; when the query finds nothing return NOTFOUND so later chains can retry.
                var navigated = JoinableTaskFactory.Run(GoToDefinitionAtCaretAsync);
                var hr = navigated ? VSConstants.S_OK : ClangdNavigationTarget.NotHandled;
                ClangdLog.WriteLine($"nav: goto Exec completed navigated={navigated} hr=0x{hr:X8}.");
                return hr;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"nav: goto failed: {ex.Message}");
                return ClangdNavigationTarget.NotHandled;
            }
        }

        /// <summary>
        /// For the priority command target: query the definition at the current caret over the direct
        /// channel and navigate with DTE. Returning true means it was handled (a jump happened); false
        /// passes it to later chains. LSP lines/columns are 0-based, DTE is 1-based; LineCharOffset is a
        /// character offset (not a display column), matching LSP's UTF-16 offset convention (no tab ambiguity).
        /// </summary>
        internal async Task<bool> GoToDefinitionAtCaretAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            string path = null;
            int line = 0, character = 0;
            try
            {
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var document = dte?.ActiveDocument;
                if (document == null)
                {
                    return false;
                }

                path = document.FullName;
                if (string.IsNullOrEmpty(path) || !IsCppFile(path))
                {
                    return false;
                }

                if (!(document.Selection is EnvDTE.TextSelection selection))
                {
                    return false;
                }

                line = selection.ActivePoint.Line - 1;
                character = selection.ActivePoint.LineCharOffset - 1;
                if (line < 0 || character < 0)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"nav: caret read failed: {ex.Message}");
                return false;
            }

            return await GoToDefinitionAtPositionAsync(path, line, character, CancellationToken.None,
                                                       ShouldHandleNavigation);
        }

        internal async Task<bool> GoToDefinitionAtPositionAsync(string path, int line, int character,
                                                                CancellationToken cancellationToken,
                                                                Func<bool> validity)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || !validity() ||
                !ClangdSettings.Instance.EnableNavigation)
                return false;
            var uri = new Uri(path).AbsoluteUri;
            var client = await GetLanguageClientAsync().WithCancellation(cancellationToken);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (!validity() || !ClangdSettings.Instance.EnableNavigation)
                return false;
            if (client == null)
            {
                ShowMessage("无法加载 clangd 客户端；请查看 clangd 日志中的 MEF / broker 错误。");
                return false;
            }

            var token = await client.GetDefinitionAsync(uri, line, character, cancellationToken);
            if (!TryGetDefinitionTarget(token, out var targetUri, out var targetLine, out var targetChar))
            {
                ClangdLog.WriteLine("nav: definition empty or invalid.");
                return false;
            }

            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested || !validity() ||
                    !ClangdSettings.Instance.EnableNavigation)
                    return false;
                var targetPath = new Uri(targetUri).LocalPath;
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                EnvDTE.Document target = null;
                foreach (EnvDTE.Document candidate in dte.Documents)
                {
                    string candidatePath = null;
                    try
                    {
                        candidatePath = candidate.FullName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.Equals(candidatePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        target = candidate;
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested || !validity())
                    return false;
                target?.Activate();
                if (target == null)
                {
                    dte.ItemOperations.OpenFile(targetPath);
                    foreach (EnvDTE.Document candidate in dte.Documents)
                    {
                        string candidatePath = null;
                        try
                        {
                            candidatePath = candidate.FullName;
                        }
                        catch
                        {
                            continue;
                        }

                        if (string.Equals(candidatePath, targetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            target = candidate;
                            break;
                        }
                    }
                }

                if (!(target?.Selection is EnvDTE.TextSelection targetSelection))
                {
                    return false;
                }

                targetSelection.MoveToLineAndOffset(targetLine + 1, targetChar + 1);
                ClangdLog.WriteLine($"nav: goto {targetPath}({targetLine + 1},{targetChar + 1}).");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"nav: navigate failed: {ex.Message}");
                return false;
            }
        }

        internal static bool TryGetDefinitionTarget(Newtonsoft.Json.Linq.JToken token, out string uri, out int line,
                                                    out int character)
        {
            uri = null;
            line = character = 0;
            try
            {
                var first = token is Newtonsoft.Json.Linq.JArray array
                                ? array.OfType<Newtonsoft.Json.Linq.JObject>().FirstOrDefault()
                                : token as Newtonsoft.Json.Linq.JObject;
                if (first == null)
                    return false;
                var uriToken = first["targetUri"] ?? first["uri"];
                var range = first["targetSelectionRange"] ?? first["targetRange"] ?? first["range"];
                var start = range?["start"];
                if (uriToken?.Type != Newtonsoft.Json.Linq.JTokenType.String ||
                    start?["line"]?.Type != Newtonsoft.Json.Linq.JTokenType.Integer ||
                    start?["character"]?.Type != Newtonsoft.Json.Linq.JTokenType.Integer)
                    return false;
                uri = (string)uriToken;
                line = (int)start["line"];
                character = (int)start["character"];
                return line >= 0 && character >= 0 && Uri.TryCreate(uri, UriKind.Absolute, out var target) &&
                       target.IsFile;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"nav: definition parse failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Toggles the native IntelliSense takeover on/off.</summary>
        private async Task ToggleTakeoverAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var sequence = Interlocked.Increment(ref _takeoverSequence);
            ClangdLog.WriteLine("takeover: toggle invoked.");
            var enabled = !ClangdSettings.Instance.TakeOverFromNativeIntelliSense;
            ClangdSettings.Instance.TakeOverFromNativeIntelliSense = enabled;
            if (!enabled)
                InvalidateInspection();
            ClangdSettings.Instance.Save();
            ClangdLog.WriteLine($"takeover: new state = {(enabled ? "enabled" : "disabled")}, settings saved.");
            await ApplyTakeoverSettingAsync(enabled, sequence);
        }

        private void OnTakeoverSettingChanged(bool enabled)
        {
            if (!enabled)
                InvalidateInspection();
            if (_brokerLoadGate == null)
                return;
            var sequence = Interlocked.Increment(ref _takeoverSequence);
            JoinableTaskFactory.RunAsync(() => ApplyTakeoverSettingAsync(enabled, sequence));
        }

        private void OnFeatureSettingsChanged(bool diagnosticsEnabled, bool diagnosticsChanged,
                                             bool inactiveRegionsChanged, bool semanticHighlightingChanged,
                                             bool takeoverChanged)
        {
            if (!IsInspectionEnabled(_inspectionCommandId))
                InvalidateInspection();
            if (_brokerLoadGate == null || Volatile.Read(ref _featureSettingsDisposed) != 0)
                return;
            InlayHintCoordinator.SettingsChanged();
            if (!ClangdSettings.Instance.EnableInactiveRegions)
                InactiveRegionServices.Store.ClearAll();
            _featureSettingsApply.Request(diagnosticsEnabled, diagnosticsChanged, inactiveRegionsChanged,
                                          semanticHighlightingChanged, takeoverChanged);
            JoinableTaskFactory.RunAsync(() => _featureSettingsApply.RunAsync(
                TakeoverTransitionSerializer.ExecuteAsync,
                async (snapshot, cancellationToken) =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    if (snapshot.ClearDiagnostics)
                    {
                        try
                        {
                            var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                            cancellationToken.ThrowIfCancellationRequested();
                            var cleared = componentModel != null &&
                                          await VsTakeoverDiagnostics.ClearClangdDiagnosticsAsync(componentModel,
                                                                                                  cancellationToken);
                            if (!cleared)
                                ClangdLog.WriteLine(
                                    $"features: failed to clear clangd reports after disabling diagnostics (version={snapshot.Version}).");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // Cleanup failure must be visible, and must not prevent
                            // capability renegotiation or later queued settings.
                            ClangdLog.WriteLine($"features: failed to clear clangd reports: {ex}");
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (snapshot.RestartRequired &&
                        ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
                    {
                        var client = _languageClient ?? await ResolveMefClientAsync();
                        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                        // Settings can change during service lookup. Never start a
                        // feature restart after takeover was switched off.
                        if (!ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
                            return;
                        if (client != null)
                        {
                            // Keep the serializer until RestartAsync actually ends;
                            // cancelling this await would let takeover overlap it.
                            await client.RestartForFeatureSettingsAsync();
                            cancellationToken.ThrowIfCancellationRequested();
                            ClangdLog.WriteLine(
                                $"features: feature settings apply completed (version={snapshot.Version}).");
                        }
                        else
                        {
                            ClangdLog.WriteLine("features: saved feature settings; clangd is not running.");
                        }
                    }
                },
                ex => ClangdLog.WriteLine($"features: failed to apply feature settings: {ex}")));
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && Interlocked.Exchange(ref _featureSettingsDisposed, 1) == 0)
                {
                    InvalidateInspection();
                    ClangdSettings.FeatureSettingsChanged -= OnFeatureSettingsChanged;
                    ClangdSettings.TakeoverSettingChanged -= OnTakeoverSettingChanged;
                    _featureSettingsApply.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        private Task ApplyTakeoverSettingAsync(bool snapshot, long snapshotSequence) =>
            TakeoverRuntimeController.ApplySequencedAsync(() => ApplyTakeoverSequencedAsync(snapshot, snapshotSequence));

        private async Task ApplyTakeoverSequencedAsync(bool snapshot, long snapshotSequence)
        {
            if (TakeoverApplyCoordinator.ShouldSkip(snapshotSequence, Volatile.Read(ref _takeoverSequence)))
            {
                ClangdLog.WriteLine("takeover: superseded transition skipped; the latest request will apply.");
                return;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var enabled = ClangdSettings.Instance.TakeOverFromNativeIntelliSense;
            if (enabled != snapshot)
            {
                ClangdLog.WriteLine(
                    $"takeover: snapshot expired (snapshot={snapshot}, latest={enabled}); applying latest.");
            }

            var settingsOk = ApplyTakeover(enabled);
            var autoClient = await ResolveMefClientAsync();
            try
            {
                // Route the live buffer before stopping the old server so the
                // host can complete its didClose/diagnostic lifecycle first.
                await SwitchOpenBuffersContentTypeAsync(enabled);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"takeover: content-type routing failed: {ex.Message}");
            }

            bool runtimeOk;
            try
            {
                runtimeOk = await TakeoverRuntimeController.ApplyAsync(
                    enabled,
                    () =>
                        TakeoverShutdownCoordinator.ShutdownTrackedOrAutoAsync(() => _languageClient, () => autoClient),
                    () =>
                    {
                        var client = _languageClient ?? autoClient;
                        if (client == null)
                        {
                            throw new InvalidOperationException("clangd client has not been created yet.");
                        }

                        return TakeoverRuntimeController.RestartAsync(client);
                    },
                    async () =>
                    {
                        if (!await _brokerLoadGate.EnsureLoadedAsync(LoadClangdClientManuallyAsync))
                        {
                            throw new InvalidOperationException("clangd broker load failed.");
                        }
                    });
            }
            catch (FeatureSettingsApplyDeferredException)
            {
                // The outer helper releases the serializer before waiting.
                throw;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"takeover: runtime transition failed: {ex}");
                runtimeOk = false;
            }

            var diagnosticsOk = false;
            try
            {
                var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                diagnosticsOk = componentModel != null &&
                                await VsTakeoverDiagnostics.ClearRetiredProviderAsync(componentModel, enabled,
                                                                                      CancellationToken.None);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"takeover: retired diagnostic cleanup failed: {ex}");
            }

            runtimeOk = runtimeOk && diagnosticsOk;
            var ok = settingsOk && runtimeOk;
            ShowMessage(enabled ? TakeoverStatusMessages.GetEnabledMessage(ok)
                                : TakeoverStatusMessages.GetDisabledMessage(settingsOk, runtimeOk));

            ClangdLog.WriteLine($"takeover: runtime transition completed (success={ok}).");
        }

        private async Task<ClangdLanguageClient> ResolveMefClientAsync()
        {
            try
            {
                var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                return componentModel?.DefaultExportProvider.GetExportedValueOrDefault<ClangdLanguageClient>();
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"takeover: MEF client resolution failed: {ex.Message}");
                return null;
            }
        }

        private async Task<int> SwitchOpenBuffersContentTypeAsync(bool enabled)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var componentModelObject = await GetServiceAsync(typeof(SComponentModel));
                var componentModel = componentModelObject as IComponentModel;
                if (componentModel == null)
                {
                    ClangdLog.WriteLine("takeover: content-type routing skipped (component model unavailable).");
                    return 0;
                }

                var registry = componentModel.GetService<IContentTypeRegistryService>();
                if (registry == null)
                {
                    ClangdLog.WriteLine("takeover: content-type routing skipped (registry unavailable).");
                    return 0;
                }

                var textManagerObject = await GetServiceAsync(typeof(SVsTextManager));
                var textManager = textManagerObject as IVsTextManager;
                var rdt = await GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                if (textManager == null || rdt == null)
                {
                    ClangdLog.WriteLine("takeover: content-type routing skipped (editor service unavailable).");
                    return 0;
                }

                var adapters =
                    componentModel.GetService<Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService>();
                if (adapters == null)
                {
                    ClangdLog.WriteLine("takeover: content-type routing skipped (adapter service unavailable).");
                    return 0;
                }

                var switched = 0;
                for (var pass = 0; pass < 2; pass++)
                {
                    var buffers = CollectRunningDataBuffers(rdt, adapters);
                    if (buffers.Count == 0)
                    {
                        break;
                    }

                    var round =
                        TakeoverContentTypeRouter.SwitchBuffers(buffers, registry, enabled, ClangdLog.WriteLine);
                    switched += round;
                    if (round == 0)
                    {
                        break;
                    }
                }

                ClangdLog.WriteLine($"takeover: content-type routing completed enabled={enabled} switched={switched}.");
                return switched;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"takeover: content-type routing failed: {ex.Message}");
                return 0;
            }
        }

        internal static bool ShouldLoadClangd(bool takeoverEnabled) => takeoverEnabled;

        internal static bool IsClangdCommandEnabled(bool takeoverEnabled, int commandId) =>
            commandId != 0x1100 && commandId != 0x1101 && commandId != 0x1103 || takeoverEnabled;

        private bool ApplyTakeover(bool enabled)
        {
            try
            {
                // UserRegistryRoot is an HKCU-relative RegistryKey; strip the
                // root prefix so IntelliSenseSuppression can re-open it.
                var rootName = UserRegistryRoot.Name;
                const string hkcuParam = "HKEY_CURRENT_USER\\";
                var relative = rootName.StartsWith(hkcuParam, StringComparison.OrdinalIgnoreCase)
                                   ? rootName.Substring(hkcuParam.Length)
                                   : rootName;
                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                return IntelliSenseSuppression.SetSuppressed(enabled, dte, relative);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"Failed to apply IntelliSense takeover: {ex.Message}");
                return false;
            }
        }

        private async Task<ClangdLanguageClient> GetLanguageClientAsync()
        {
            if (!ShouldLoadClangd(ClangdSettings.Instance.TakeOverFromNativeIntelliSense))
                return null;
            return await _brokerLoadGate.EnsureLoadedAsync(LoadClangdClientManuallyAsync) ? _languageClient : null;
        }

        private static void ShowMessage(string message)
        {
            // Status bar + log dual-write: the status bar message flashes by and users often miss it,
            // so the log is the only reliable evidence channel.
            ClangdLog.WriteLine("ui: " + message);
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var bar = (IVsStatusbar)Package.GetGlobalService(typeof(SVsStatusbar));
                bar?.SetText(message);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"ui: status bar write failed: {ex.Message}");
            }
        }
    }

    internal static class TakeoverContentTypeRouter
    {
        internal const string ClangdContentTypeName = "cpp-clangd";

        internal const string NativeContentTypeName = "C/C++";

        internal static string GetTargetContentTypeName(bool takeoverEnabled, IContentType current)
        {
            if (current == null)
            {
                return null;
            }

            try
            {
                if (!takeoverEnabled)
                {
                    return current.IsOfType(ClangdContentTypeName) ? NativeContentTypeName : null;
                }

                return current.IsOfType(NativeContentTypeName) && !current.IsOfType(ClangdContentTypeName)
                           ? ClangdContentTypeName
                           : null;
            }
            catch
            {
                return null;
            }
        }

        internal static int SwitchBuffers(IEnumerable<ITextBuffer> buffers, IContentTypeRegistryService registry,
                                          bool takeoverEnabled, Action<string> log)
        {
            if (buffers == null || registry == null)
            {
                return 0;
            }

            var seen = new HashSet<ITextBuffer>();
            var switched = 0;
            foreach (var buffer in buffers)
            {
                if (buffer == null || !seen.Add(buffer))
                {
                    continue;
                }

                try
                {
                    var targetName = GetTargetContentTypeName(takeoverEnabled, buffer.ContentType);
                    if (targetName == null)
                    {
                        continue;
                    }

                    var target = registry.GetContentType(targetName);
                    if (target == null)
                    {
                        log?.Invoke(
                            $"takeover: content-type {targetName} unavailable, buffer left unchanged; will retry when the document is reopened.");
                        continue;
                    }

                    if (ReferenceEquals(buffer.ContentType, target))
                    {
                        continue;
                    }

                    buffer.ChangeContentType(target, null);
                    switched++;
                    log?.Invoke($"takeover: buffer content-type switched to {targetName}.");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"takeover: buffer content-type switch failed: {ex.Message}");
                }
            }

            return switched;
        }

        internal static bool EnsureBufferRouted(ITextBuffer buffer, IContentTypeRegistryService registry,
                                                bool takeoverEnabled, Action<string> log)
        {
            if (buffer == null || registry == null)
            {
                return false;
            }

            try
            {
                var targetName = GetTargetContentTypeName(takeoverEnabled, buffer.ContentType);
                if (targetName == null)
                {
                    return false;
                }

                var target = registry.GetContentType(targetName);
                if (target == null || ReferenceEquals(buffer.ContentType, target))
                {
                    return false;
                }

                buffer.ChangeContentType(target, null);
                log?.Invoke($"takeover: buffer content-type switched to {targetName}.");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"takeover: buffer content-type switch failed: {ex.Message}");
                return false;
            }
        }
    }

    internal static class TakeoverTransitionSerializer
    {
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        internal static async Task ExecuteAsync(Func<Task> work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            await _gate.WaitAsync();
            try
            {
                await work();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    internal static class TakeoverShutdownCoordinator
    {
        internal static async Task ShutdownTrackedOrAutoAsync(Func<ClangdLanguageClient> tracked,
                                                              Func<ClangdLanguageClient> auto)
        {
            var client = tracked?.Invoke();
            if (client == null)
            {
                client = auto?.Invoke();
            }

            if (client == null)
            {
                return;
            }

            await client.ShutdownIfActiveAsync();
        }
    }

    internal static class TakeoverApplyCoordinator
    {
        internal static bool ShouldSkip(long snapshotSequence,
                                        long latestSequence) => snapshotSequence != latestSequence;
    }

    internal static class TakeoverStatusMessages
    {
        internal static string GetEnabledMessage(bool ok) =>
            ok ? "已接管：原生 IntelliSense 已禁用，由 clangd 提供补全与诊断。" : "clangd 接管失败，详情见日志。";

        internal static string GetDisabledMessage(bool settingsOk, bool runtimeOk)
        {
            if (!settingsOk)
            {
                return "还原不完整：备份缺失、部分设置未还原，详情见日志。";
            }

            if (!runtimeOk)
            {
                return "还原原生 IntelliSense 失败，详情见日志。";
            }

            return "已还原原生 IntelliSense。已打开的 C++ 文档请关闭后重新打开，以使原生服务完全接管。";
        }
    }

    internal static class TakeoverRuntimeController
    {
        internal static async Task ApplySequencedAsync(Func<Task> apply)
        {
            using (var cancelled = new CancellationTokenSource())
            {
                // Off must interrupt readiness rather than wait behind the busy
                // lifecycle. Subscription precedes the settings check to avoid
                // missing an off event at the start of a wait.
                Action<bool> changed = enabled => { if (!enabled) cancelled.Cancel(); };
                ClangdSettings.TakeoverSettingChanged += changed;
                try
                {
                    while (true)
                    {
                        try
                        {
                            await TakeoverTransitionSerializer.ExecuteAsync(apply);
                            return;
                        }
                        catch (FeatureSettingsApplyDeferredException deferred)
                        {
                            if (cancelled.IsCancellationRequested ||
                                !ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
                                return;
                            // Never hold the serializer while waiting for a
                            // manual/cold-start lifecycle owner to finish.
                            await deferred.WaitForReadyAsync(cancelled.Token);
                            if (cancelled.IsCancellationRequested ||
                                !ClangdSettings.Instance.TakeOverFromNativeIntelliSense)
                                return;
                            // Re-enter the sequenced apply, which rechecks both
                            // the request sequence and current settings.
                        }
                    }
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
                {
                }
                finally
                {
                    ClangdSettings.TakeoverSettingChanged -= changed;
                }
            }
        }

        internal static async Task RestartAsync(ClangdLanguageClient client)
        {
            // This entry distinguishes lifecycle busy from a missing Start host.
            // A busy attempt MUST escape to ApplySequencedAsync, never broker load.
            await client.RestartForFeatureSettingsAsync();
            if (ClangdSettings.Instance.TakeOverFromNativeIntelliSense && !client.IsRunning)
            {
                // Feature apply intentionally skips inactive clients; takeover
                // must still start one after shutdown (or a failed old restart).
                await client.RestartAsync();
            }
        }

        internal static async Task<bool> ApplyAsync(bool enabled, Func<Task> shutdown, Func<Task> restart,
                                                    Func<Task> load)
        {
            if (!enabled)
            {
                try
                {
                    if (shutdown != null)
                    {
                        await shutdown();
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine($"takeover: shutdown failed: {ex.Message}");
                    return false;
                }
            }

            if (restart != null)
            {
                try
                {
                    await restart();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // The host may not yet have subscribed to StartAsync.
                    // Fall back to the broker's normal load path.
                }
                catch (TimeoutException ex)
                {
                    ClangdLog.WriteLine($"takeover: restart timed out ({ex.Message}); falling back to broker load.");
                }
            }

            if (load != null)
            {
                try
                {
                    await load();
                    return true;
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine($"takeover: broker load fallback failed: {ex.Message}");
                    return false;
                }
            }

            return false;
        }
    }

    internal sealed class BrokerLoadGate
    {
        private readonly object _gate = new object();
        private readonly JoinableTaskFactory _taskFactory;
        private JoinableTask<bool> _attempt;

        internal BrokerLoadGate(JoinableTaskFactory taskFactory)
        {
            _taskFactory = taskFactory ?? throw new ArgumentNullException(nameof(taskFactory));
        }

        internal async Task<bool> EnsureLoadedAsync(Func<Task<bool>> load)
        {
            if (load == null)
            {
                throw new ArgumentNullException(nameof(load));
            }

            JoinableTask<bool> attempt;
            TaskCompletionSource<bool> published = null;
            lock (_gate)
            {
                // Retain successful attempts. Replace only completed failures;
                // old waiters never reset state belonging to a newer attempt.
                if (_attempt == null || _attempt.IsCompleted && (_attempt.Task.Status != TaskStatus.RanToCompletion ||
                                                                 !_attempt.Task.Result))
                {
                    published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var ready = published.Task;
                    _attempt = _taskFactory.RunAsync(async () =>
                                                     {
                                                         // RunAsync executes the synchronous prefix inline. Defer
                                                         // user code until the task is published and the lock released,
                                                         // so a synchronous reentrant lookup joins this same attempt.
                                                         await ready;
                                                         return await load();
                                                     });
                }

                attempt = _attempt;
            }

            published?.SetResult(true);
            // Awaiting a semaphore or the bare Task hides the owner from a UI
            // JTF.Run waiter. JoinAsync carries the owner's UI dependency.
            return await attempt.JoinAsync();
        }
    }
}
