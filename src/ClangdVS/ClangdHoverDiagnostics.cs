using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ClangdVS
{
    // Host diagnostics and the narrowly-scoped cold-start document binding repair.
    // Private host shapes are best-effort and verified against VS 18.10; missing or
    // changed shapes fail closed and leave the original behavior untouched.
    internal static class ClangdHoverDiagnostics
    {
        private static readonly ConditionalWeakTable<ITextBuffer, BufferObservation> Buffers =
            new ConditionalWeakTable<ITextBuffer, BufferObservation>();
        // Key by the actual host document-client instance, not the buffer or
        // extension client: a replacement session must get its own opportunity.
        private static readonly ConditionalWeakTable<object, HostOpenAttempt> HostOpenAttempts =
            new ConditionalWeakTable<object, HostOpenAttempt>();
        private sealed class HostOpenAttempt
        {
            internal int Reserved;
        }
        private static string _phase = "not-loaded";
        private static int _epoch;
        private static int _sequence;
        internal static int Epoch => Volatile.Read(ref _epoch);
        internal static string Phase => $"epoch={Epoch} phase={Volatile.Read(ref _phase)}";

        internal static void Lifecycle(string phase, object client, bool initialized = false)
        {
            if (initialized)
                Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _phase, phase);
            ClangdLog.WriteLine(
                $"hover-diag: lifecycle seq={Interlocked.Increment(ref _sequence)} client={Identity(client)} {Phase}.");
        }

        internal static void Observe(ITextBuffer buffer, ITextView view, string reason)
        {
            if (buffer == null)
                return;
            try
            {
                Buffers.GetValue(buffer, b => new BufferObservation(b)).Capture(buffer, view, reason);
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"hover-diag: observation unavailable: {ex.Message}");
            }
        }

        private sealed class BufferObservation
        {
            private string _last;
            private WeakReference<ITextView> _view;

            internal BufferObservation(ITextBuffer buffer)
            {
                buffer.ContentTypeChanged += OnContentTypeChanged;
            }

            internal void Capture(ITextBuffer buffer, ITextView view, string reason)
            {
                if (view != null)
                    _view = new WeakReference<ITextView>(view);
                else
                    _view?.TryGetTarget(out view);
                var state = $"{Phase} {DescribeHostState(buffer, view)}";
                if (state == _last)
                    return;
                _last = state;
                ClangdLog.WriteLine(
                    $"hover-diag: buffer reason={reason} buffer={Identity(buffer)} view={Identity(view)} {state}");
            }

            private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs e)
            {
                try
                {
                    ITextView view = null;
                    _view?.TryGetTarget(out view);
                    Capture((ITextBuffer)sender, view,
                            $"content-type:{e.BeforeContentType.TypeName}->{e.AfterContentType.TypeName}");
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine($"hover-diag: content-type observation unavailable: {ex.Message}");
                }
            }
        }

        internal static string DescribeHostState(ITextBuffer buffer, ITextView view)
        {
            try
            {
                var type = buffer.ContentType;
                var bases = string.Join(",", type.BaseTypes.Select(b => b.TypeName));
                var document =
                    buffer.Properties.PropertyList.Select(p => p.Value).OfType<ITextDocument>().FirstOrDefault();
                var sources = DescribeSources(buffer, view);
                var clients = DescribeDocumentClients(buffer);
                return $"file={document?.FilePath ?? "<unavailable>"} type={type.TypeName} bases=[{bases}] cpp={type.IsOfType("C/C++")} " +
                       $"lsp={type.IsOfType("languageserver-base")} sources={sources} documentClients={clients}";
            }
            catch (Exception ex)
            {
                return $"state=<unavailable:{ex.GetType().Name}>";
            }
        }

        private static string DescribeSources(ITextBuffer buffer, ITextView view)
        {
            if (view == null)
                return "<no-view>";
            var cache =
                view.Properties.PropertyList
                    .FirstOrDefault(
                        p => p.Key is Type t &&
                             t.FullName.StartsWith(
                                 "Microsoft.VisualStudio.Language.Utilities.IntellisenseSourceCache+ViewSourceCache`",
                                 StringComparison.Ordinal) &&
                             t.IsGenericType &&
                             t.GetGenericArguments().Any(a => a.FullName ==
                                                              "Microsoft.VisualStudio.Language.Intellisense." +
                                                                  "Implementation.AsyncQuickInfoSession+OrderedSource"))
                    .Value;
            if (cache == null)
                return "<not-created>";
            if (!(Read(cache, "_bufferCache") is IDictionary buffers))
                return "<unavailable>";
            if (!buffers.Contains(buffer))
                return "<not-created>";
            if (!(Read(buffers[buffer], "Sources") is IEnumerable sources))
                return "<unavailable>";
            var names = new List<string>();
            foreach (var ordered in sources)
            {
                var source = Read(ordered, "Source");
                var name = source == null ? "<unavailable>" : source.GetType().FullName;
                if (name == "Microsoft.VisualStudio.LanguageServer.Client.HoverSource")
                    name += $"(managerCookie={Value(Read(source, "documentClientManager"), "languageClientsCookie")})";
                names.Add(name);
            }
            return names.Count == 0 ? "<empty>" : "[" + string.Join(",", names) + "]";
        }

        private static string DescribeDocumentClients(ITextBuffer buffer)
        {
            var context = buffer.Properties.PropertyList
                              .FirstOrDefault(p => p.Key is Type t &&
                                                   t.FullName == "Microsoft.VisualStudio.LanguageServer.Client." +
                                                                     "LanguageServiceClientManager+PerDocumentContext")
                              .Value;
            if (context == null)
                return "<not-created>";
            var collection = Read(context, "DocumentClientsCollection");
            if (collection == null)
                return "<unavailable>";
            var names = new List<string>();
            if (!(Read(collection, "clients") is IEnumerable clients))
                return "<unavailable>";
            foreach (var client in clients)
            {
                var metadata = Read(client, "Metadata");
                var token = Read(client, "ShutdownToken");
                names.Add(
                    $"name={Value(client, "Name")} active={Value(client, "IsActive")} disposed={Value(client, "IsDisposed")} " +
                    $"shutdown={(token is CancellationToken ct ? ct.IsCancellationRequested.ToString() : "<unavailable>")} " +
                    $"uxDisabled={Value(metadata, "IsUserExperienceDisabled")} clientName={Value(metadata, "ClientName")} " +
                    $"documentOpen={Value(client, "IsDocumentOpen")}");
            }
            return $"[cookie={Value(collection, "stateCookie")} started={Value(collection, "serversStarted")} " +
                   $"clients={(names.Count == 0 ? "<empty>" : string.Join(";", names))}]";
        }

        // Reads only already-existing host fields/properties. Never call GetOrCreate,
        // GetApplicableClients or EnsureInitialization: those can repair the race.
        private static object Read(object value, string name)
        {
            if (value == null)
                return null;
            try
            {
                var type = value.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var property = type.GetProperty(name, flags);
                if (property != null)
                    return property.GetValue(value);
                return type.GetField(name, flags)?.GetValue(value);
            }
            catch
            {
                return null;
            }
        }

        private static string Value(object value, string name)
        {
            var result = Read(value, name);
            return result?.ToString() ??
                   (name == "ClientName" && value != null ? "<null-or-unavailable>" : "<unavailable>");
        }

        internal static string Identity(object value) => value == null
                                                             ? "<null>"
                                                             : RuntimeHelpers.GetHashCode(value).ToString("X8");

        /// <summary>
        /// Repairs only the VS LSP host's per-document didOpen binding. The direct
        /// clangd resend cannot update LanguageServiceClient.openedDocumentUri, so
        /// VS can show a HoverSource while TryCreateMessageParams still rejects
        /// every hover request. This uses the host's own internal notification
        /// factory and the existing client's queue; no new host client is created.
        /// </summary>
        internal static async Task<bool> RepairHostDocumentBindingsAsync(object extensionClient, int initCount,
                                                                         int lifecycleVersion,
                                                                         Func<bool> isCurrentInitialization)
        {
            if (initCount != 1 || isCurrentInitialization == null || !isCurrentInitialization())
                return false;
            try
            {
                for (var attempt = 1; attempt <= 6; attempt++)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!isCurrentInitialization())
                    {
                        ClangdLog.WriteLine(
                            $"hover-diag: host-didOpen repair skipped stale initialization attempt={attempt} " +
                            $"init={initCount} version={lifecycleVersion}.");
                        return false;
                    }
                    var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                    var adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
                    var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                    var textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
                    var queued = 0;
                    var buffers = GetCppBuffers(adapters, rdt, textManager);
                    foreach (var buffer in buffers)
                    {
                        if (!isCurrentInitialization())
                            return false;
                        if (QueueHostDidOpen(buffer, extensionClient))
                        {
                            queued++;
                            ClangdLog.WriteLine(
                                $"hover-diag: host-didOpen repair queued attempt={attempt} buffer={Identity(buffer)} {DescribeHostState(buffer, null)}.");
                        }
                    }

                    ClangdLog.WriteLine($"hover-diag: host-didOpen repair scan attempt={attempt} queued={queued}.");
                    await Task.Delay(250);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!isCurrentInitialization())
                        return false;
                    var currentBuffers = GetCppBuffers(adapters, rdt, textManager);
                    if (currentBuffers.Count != 0 &&
                        currentBuffers.All(buffer => GetHostDocumentOpenState(buffer, extensionClient) == true))
                    {
                        ClangdLog.WriteLine($"hover-diag: host-didOpen repair complete attempt={attempt}.");
                        return true;
                    }
                }

                ClangdLog.WriteLine("hover-diag: host-didOpen repair retries exhausted.");
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"hover-diag: host-didOpen repair failed: {ex}");
            }
            return false;
        }

        private static bool IsCppBuffer(ITextBuffer buffer)
        {
            var type = buffer?.ContentType;
            return type != null && (type.IsOfType("C/C++") || type.IsOfType("cpp-clangd"));
        }

        private static List<ITextBuffer> GetCppBuffers(IVsEditorAdaptersFactoryService adapters,
                                                       IVsRunningDocumentTable rdt, IVsTextManager textManager)
        {
            var result = new List<ITextBuffer>();
            if (adapters == null || rdt == null || textManager == null)
                return result;
            var seen = new HashSet<ITextBuffer>();
            foreach (var view in ClangdVSPackage.EnumerateDocumentViews(rdt, textManager))
            {
                if (view == null || view.GetBuffer(out var lines) != 0 || lines == null)
                    continue;
                var buffer = adapters.GetDataBuffer(lines);
                if (IsCppBuffer(buffer) && seen.Add(buffer))
                    result.Add(buffer);
            }
            return result;
        }

        private static object FindHostDocumentClient(ITextBuffer buffer, object extensionClient)
        {
            var context = buffer.Properties.PropertyList
                              .FirstOrDefault(p => p.Key is Type t &&
                                                   t.FullName == "Microsoft.VisualStudio.LanguageServer.Client." +
                                                                     "LanguageServiceClientManager+PerDocumentContext")
                              .Value;
            if (!(Read(Read(context, "DocumentClientsCollection"), "clients") is IEnumerable clients))
                return null;
            foreach (var client in clients)
            {
                if (ReferenceEquals(Read(client, "Client"), extensionClient) &&
                    Read(client, "IsActive") is bool active && active && Read(client, "IsDisposed") is bool disposed &&
                    !disposed && Read(client, "ShutdownToken") is CancellationToken token &&
                    !token.IsCancellationRequested)
                    return client;
            }
            return null;
        }

        internal static bool? GetHostDocumentOpenState(ITextBuffer buffer, object extensionClient)
        {
            return Read(FindHostDocumentClient(buffer, extensionClient), "IsDocumentOpen") as bool ? ;
        }

        private static bool QueueHostDidOpen(ITextBuffer buffer, object extensionClient)
        {
            var client = FindHostDocumentClient(buffer, extensionClient);
            if (!(Read(client, "IsDocumentOpen") is bool open) || open)
                return false;
            // Resolve from this client's assembly, never from an unrelated loaded
            // assembly with a similarly named type. This also keeps the SDK adapter
            // independent of MEF export-provider implementation details.
            var factoryType = client.GetType().Assembly.GetType(
                "Microsoft.VisualStudio.LanguageServer.Client.TextDocumentDidOpenNotificationFactory");
            if (factoryType == null || factoryType.BaseType == null || !factoryType.BaseType.IsGenericType)
                return false;
            var snapshot = buffer.CurrentSnapshot;
            var factory = Activator.CreateInstance(factoryType,
                                                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                                   null, new object[] { snapshot }, null);
            var messageType = factoryType.BaseType.GetGenericArguments()[0];
            var notify =
                client.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "EnqueueNotification" && m.IsGenericMethodDefinition &&
                                         m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2 &&
                                         m.GetParameters()[1].ParameterType == typeof(CancellationToken));
            if (notify == null)
                return false;
            if (!(Read(client, "ShutdownToken") is CancellationToken shutdownToken) ||
                shutdownToken.IsCancellationRequested)
                return false;
            var attempt = HostOpenAttempts.GetValue(client,
                                                    _ => new HostOpenAttempt());
            if (Interlocked.CompareExchange(ref attempt.Reserved, 1, 0) != 0)
                return false;
            // EnqueueNotification returns before queue consumption/TryOpenDocument.
            // Keep the reservation even if Invoke throws: acceptance may already
            // have happened, and retrying would be an ambiguous duplicate enqueue.
            // Unsupported shapes return before reservation and remain retryable.
            notify.MakeGenericMethod(messageType).Invoke(client, new[] { factory, (object)shutdownToken });
            return true;
        }
    }

    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ClangdHoverViewTrace : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView textView)
        {
            ClangdHoverDiagnostics.Observe(textView.TextBuffer, textView, "view-created");
            var epoch = -1;
            var count = 0;
            EventHandler<MouseHoverEventArgs> hover = (sender, args) =>
            {
                var current = ClangdHoverDiagnostics.Epoch;
                if (epoch != current)
                {
                    epoch = current;
                    count = 0;
                }
                if (++count > 3)
                    return;
                ClangdLog.WriteLine($"hover-diag: mouse-hover view={ClangdHoverDiagnostics.Identity(textView)} " +
                                    $"position={args.Position} {ClangdHoverDiagnostics.Phase} " +
                                    ClangdHoverDiagnostics.DescribeHostState(textView.TextBuffer, textView));
            };
            textView.MouseHover += hover;
            textView.Closed += (sender, args) => textView.MouseHover -= hover;
        }
    }

    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("ClangdHoverTrace")]
    [ContentType("C/C++")]
    [Order(Before = "LSPHoverProvider")]
    internal sealed class ClangdHoverTraceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            ClangdHoverDiagnostics.Observe(textBuffer, null, "quickinfo-source-created");
            return new TraceSource(textBuffer);
        }

        private sealed class TraceSource : IAsyncQuickInfoSource
        {
            private readonly ITextBuffer _buffer;
            private readonly object _gate = new object();
            private int _epoch = -1;
            private int _invocations;
            private bool _disposed;
            internal TraceSource(ITextBuffer buffer)
            {
                _buffer = buffer;
            }

            public Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session,
                                                             CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return Task.FromResult<QuickInfoItem>(null);
                    var epoch = ClangdHoverDiagnostics.Epoch;
                    if (_epoch != epoch)
                    {
                        _epoch = epoch;
                        _invocations = 0;
                    }
                    if (++_invocations > 3)
                        return Task.FromResult<QuickInfoItem>(null);
                }
                try
                {
                    var snapshot = _buffer.CurrentSnapshot;
                    var point = session.GetTriggerPoint(snapshot);
                    var view = session.TextView;
                    ClangdLog.WriteLine(
                        $"hover-diag: quickinfo-invoke buffer={ClangdHoverDiagnostics.Identity(_buffer)} " +
                        $"{ClangdHoverDiagnostics.Phase} trigger={(point.HasValue ? point.Value.Position.ToString() : "<unmapped>")} " +
                        ClangdHoverDiagnostics.DescribeHostState(_buffer, view));
                    // Capture again after the LSP source has run. The pre-source
                    // snapshot alone cannot distinguish a lazy cache from a route gap.
                    EventHandler<QuickInfoSessionStateChangedEventArgs> finished = null;
                    finished = (sender, args) =>
                    {
                        if (args.NewState != QuickInfoSessionState.Visible &&
                            args.NewState != QuickInfoSessionState.Dismissed)
                            return;
                        session.StateChanged -= finished;
                        ClangdLog.WriteLine(
                            $"hover-diag: quickinfo-finished buffer={ClangdHoverDiagnostics.Identity(_buffer)} " +
                            $"state={args.NewState} {ClangdHoverDiagnostics.Phase} " +
                            ClangdHoverDiagnostics.DescribeHostState(_buffer, view));
                    };
                    session.StateChanged += finished;
                }
                catch (Exception ex)
                {
                    ClangdLog.WriteLine($"hover-diag: quickinfo observation unavailable: {ex.Message}");
                }
                return Task.FromResult<QuickInfoItem>(null);
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                }
            }
        }
    }
}
