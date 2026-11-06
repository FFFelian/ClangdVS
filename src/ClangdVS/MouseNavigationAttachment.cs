using System;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;

namespace ClangdVS
{
    // UI-thread scan boundary. Only successful construction publishes a handler.
    internal sealed class MouseNavigationAttachment
    {
        private readonly ConditionalWeakTable<IWpfTextView, IDisposable> _mouseViews =
            new ConditionalWeakTable<IWpfTextView, IDisposable>();
        private readonly ConditionalWeakTable<IVsTextView, Diagnostic> _diagnostics =
            new ConditionalWeakTable<IVsTextView, Diagnostic>();
        private readonly Func<IWpfTextView, ITextBuffer, ITextDocumentFactoryService, ITextStructureNavigator,
                              IClassifier, IDisposable> _create;
        private readonly Action<string> _log;

        internal MouseNavigationAttachment(Func<IWpfTextView, ITextBuffer, ITextDocumentFactoryService,
                                                ITextStructureNavigator, IClassifier, IDisposable> create,
                                           Action<string> log)
        {
            _create = create;
            _log = log;
        }

        internal static void EnsureViewNavigation(bool existingFilter, Action attachMouse, Action attachFilter)
        {
            attachMouse();
            if (!existingFilter)
                attachFilter();
        }

        internal void AttachMouseNavigation(IVsTextView view, ITextBuffer source,
                                            IVsEditorAdaptersFactoryService adapters, IComponentModel services,
                                            bool existingFilter)
        {
            IWpfTextView wpf = null;
            string path = null;
            var stage = "services";
            try
            {
                if (services == null)
                {
                    Skipped(view, wpf, existingFilter, "component-model-missing");
                    return;
                }
                if (adapters == null)
                {
                    Skipped(view, wpf, existingFilter, "adapters-service-missing");
                    return;
                }
                // Always resolve from this adapter, including restored/active views.
                stage = "get-wpf-view";
                wpf = adapters.GetWpfTextView(view);
                if (wpf == null)
                {
                    Skipped(view, wpf, existingFilter, "wpf-null", WpfNullDiagnostic(view));
                    return;
                }
                if (wpf.IsClosed)
                {
                    Skipped(view, wpf, existingFilter, "wpf-closed");
                    return;
                }
                if (_mouseViews.TryGetValue(wpf, out _))
                {
                    Report(
                        view, "existing/" + Identity(wpf),
                        $"nav: mouse attach existing view={Identity(view)} wpf={Identity(wpf)} existingFilter={existingFilter}.");
                    return;
                }
                if (source == null)
                {
                    Skipped(view, wpf, existingFilter, "source-buffer-null");
                    return;
                }
                stage = "source-buffer";
                if (source.ContentType == null ||
                    !(source.ContentType.IsOfType("C/C++") || source.ContentType.IsOfType("cpp-clangd")))
                {
                    Skipped(view, wpf, existingFilter, "source-not-cpp");
                    return;
                }
                if (wpf.BufferGraph == null ||
                    wpf.BufferGraph.GetTextBuffers(b => ReferenceEquals(b, source)).Count == 0)
                {
                    Skipped(view, wpf, existingFilter, "source-not-in-view-graph");
                    return;
                }
                stage = "documents-service";
                var documents = services.GetService<ITextDocumentFactoryService>();
                if (documents == null)
                {
                    Skipped(view, wpf, existingFilter, "documents-service-missing");
                    return;
                }
                stage = "navigators-service";
                var navigators = services.GetService<ITextStructureNavigatorSelectorService>();
                if (navigators == null)
                {
                    Skipped(view, wpf, existingFilter, "navigators-service-missing");
                    return;
                }
                stage = "classifiers-service";
                var classifiers = services.GetService<IClassifierAggregatorService>();
                if (classifiers == null)
                {
                    Skipped(view, wpf, existingFilter, "classifiers-service-missing");
                    return;
                }
                stage = "source-document";
                if (!documents.TryGetTextDocument(source, out var document) || document == null)
                {
                    Skipped(view, wpf, existingFilter, "no-source-document");
                    return;
                }
                path = document.FilePath;
                stage = "navigator";
                var navigator = navigators.GetTextStructureNavigator(source);
                if (navigator == null)
                {
                    Skipped(view, wpf, existingFilter, "navigator-null", "path=" + path);
                    return;
                }
                stage = "classifier";
                var classifier = classifiers.GetClassifier(source);
                if (classifier == null)
                {
                    Skipped(view, wpf, existingFilter, "classifier-null", "path=" + path);
                    return;
                }
                stage = "handler-construction";
                var handler = _create(wpf, source, documents, navigator, classifier);
                _mouseViews.Add(wpf, handler);
                Report(
                    view, "attached/" + Identity(wpf),
                    $"nav: mouse attach attached view={Identity(view)} wpf={Identity(wpf)} existingFilter={existingFilter} path={path}.");
            }
            catch (Exception ex)
            {
                // Failure is isolated from command attachment and remains retryable.
                Skipped(view, wpf, existingFilter, "exception", $"stage={stage} path={path} error={ex}");
            }
        }

        internal static string Identity(object view) => view == null ? "null"
                                                                     : RuntimeHelpers.GetHashCode(view).ToString("X8");

        private static string WpfNullDiagnostic(IVsTextView view)
        {
            // Same documented host property used by the installed SDK factory;
            // this is evidence only, never a fallback to a different view.
            if (!(view is IVsUserData userData))
                return "hostReason=no-user-data";
            var key = Microsoft.VisualStudio.Editor.DefGuidList.guidIWpfTextViewHost;
            try
            {
                var hr = userData.GetData(ref key, out var value);
                var host = value as IWpfTextViewHost;
                return $"hostReason={(hr < 0 ? "get-data-failed" : value == null ? "host-null" : host == null ? "host-type-mismatch" : host.TextView == null ? "host-text-view-null" : "host-ready-after-lookup")} " +
                       $"hostHr=0x{hr:X8} hostType={value?.GetType().FullName ?? "null"}";
            }
            catch (Exception ex)
            {
                return $"hostReason=get-data-exception error={ex}";
            }
        }

        private void
        Skipped(IVsTextView view, IWpfTextView wpf, bool existingFilter, string reason, string detail = null) => Report(
            view, "skipped/" + reason + "/" + Identity(wpf),
            $"nav: mouse attach skipped view={Identity(view)} wpf={Identity(wpf)} reason={reason} existingFilter={existingFilter} {detail}.");

        private void Report(IVsTextView view, string state, string message)
        {
            var diagnostic = _diagnostics.GetValue(view,
                                                   _ => new Diagnostic());
            // Log state transitions; identical scan results at most once per 30s.
            var now = DateTime.UtcNow;
            if (diagnostic.State == state && now - diagnostic.LastUtc < TimeSpan.FromSeconds(30))
                return;
            diagnostic.State = state;
            diagnostic.LastUtc = now;
            _log(message);
        }

        private sealed class Diagnostic
        {
            internal string State;
            internal DateTime LastUtc;
        }
    }
}
