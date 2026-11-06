using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;

namespace ClangdVS
{
    /// <summary>Clicks query the source buffer hit position, never the editor caret.</summary>
    internal sealed class ClangdMouseNavigation : IDisposable
    {
        private readonly IWpfTextView _view;
        private readonly ITextBuffer _source;
        private readonly ITextDocumentFactoryService _documents;
        private readonly ITextStructureNavigator _navigator;
        private readonly IClassifier _classifier;
        private readonly Func<bool> _takeover;
        private readonly Action<Func<Task>> _run;
        private readonly Func<string, int, int, CancellationToken, Func<bool>, Task<bool>> _navigate;
        private readonly Func<ModifierKeys> _modifiers;
        private readonly DispatcherTimer _pendingCheck;
        private Candidate _candidate;
        private CancellationTokenSource _request;
        private long _generation;
        private bool _closed;
        private readonly int _viewId;
        private static int _nextViewId;
        private DateTime _lastDiagnosticUtc;
        private bool _traceGesture;
        private string _hitDiagnostic;
        private string _candidateCleared;

        internal ClangdMouseNavigation(IWpfTextView view, ITextBuffer source, ITextDocumentFactoryService documents,
                                       ITextStructureNavigator navigator, IClassifier classifier, Func<bool> takeover,
                                       Action<Func<Task>> run,
                                       Func<string, int, int, CancellationToken, Func<bool>, Task<bool>> navigate,
                                       Func<ModifierKeys> modifiers = null)
        {
            _view = view;
            _source = source;
            _documents = documents;
            _navigator = navigator;
            _classifier = classifier;
            _takeover = takeover;
            _run = run;
            _navigate = navigate;
            _modifiers = modifiers ?? (() => Keyboard.Modifiers);
            _viewId = Interlocked.Increment(ref _nextViewId);
            // Resolve diagnostic/provider state before subscribing. If creation
            // fails, the next scan must not inherit untracked event handlers.
            _documents.TryGetTextDocument(_source, out var attachedDocument);
            var attachedDiagnostic =
                $"nav: mouse handler attached view={_viewId} path={attachedDocument?.FilePath} " +
                $"wpf={MouseNavigationAttachment.Identity(view)} visual={view.VisualElement.GetType().FullName} navigator={navigator.GetType().FullName} {OptionsDiagnostic()}.";
            _pendingCheck =
                new DispatcherTimer(DispatcherPriority.Background,
                                    view.VisualElement.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            // Observe even handled downs: clicking a nested control also cancels an old query.
            view.VisualElement.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnDown), true);
            view.VisualElement.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(OnUp), true);
            view.VisualElement.PreviewMouseMove += OnMove;
            view.Closed += OnClosed;
            view.LostAggregateFocus += OnLostFocus;
            source.Changed += OnChanged;
            view.Options.OptionChanged += OnOptionChanged;
            _pendingCheck.Tick += OnPendingCheck;
            ClangdLog.WriteLine(attachedDiagnostic);
        }

        private bool Enabled() =>
            !_closed && !_view.IsClosed &&
            _takeover() && _view.Options.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefEnabledId) &&
            _view.Options.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefModifierKeyId) == ModifierKeys.Control &&
            !_view.Options.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefOpensPeekId);

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            var modifiers = _modifiers();
            // One diagnostic gesture per second per view. No mouse-move logging.
            var now = DateTime.UtcNow;
            _traceGesture = (modifiers & ModifierKeys.Control) != 0 && (now - _lastDiagnosticUtc).TotalSeconds >= 1;
            if (_traceGesture)
                _lastDiagnosticUtc = now;
            ObserveButton("down", e, modifiers);
        }

        private void OnUp(object sender, MouseButtonEventArgs e) => ObserveButton("up", e, _modifiers());

        private void ObserveButton(string stage, MouseButtonEventArgs e, ModifierKeys modifiers)
        {
            var handledBefore = e.Handled;
            Point point = default;
            bool surface = false;
            string reason;
            try
            {
                point = e.GetPosition(_view.VisualElement);
                surface = IsTextSurface(e.OriginalSource);
                reason = stage == "down" ? ProcessMouseDown(point, e.ChangedButton, modifiers, e.ClickCount,
                                                            !handledBefore && surface)
                                         : ProcessMouseUp(point, e.ChangedButton, modifiers, e.ClickCount,
                                                          !handledBefore && surface, () => e.Handled = true);
            }
            catch (Exception ex)
            {
                Invalidate("input-error");
                reason = $"input-error/{ex.GetType().Name}: {ex.Message}";
            }
            try
            {
                if (_traceGesture)
                {
                    _documents.TryGetTextDocument(_source, out var document);
                    ClangdLog.WriteLine(
                        $"nav: mouse gesture view={_viewId} stage={stage} reason={(handledBefore ? "already-handled/" : "")}{reason} " +
                        $"path={document?.FilePath} handled={handledBefore} handledAfter={e.Handled} button={e.ChangedButton} " +
                        $"clicks={e.ClickCount} modifiers={modifiers} surface={surface} source={VisualAncestors(e.OriginalSource)} " +
                        $"capture={Mouse.Captured?.GetType().FullName ?? "none"} mouse={Format(point.X)},{Format(point.Y)} " +
                        $"viewport={Format(_view.ViewportLeft)},{Format(_view.ViewportTop)} {OptionsDiagnostic()} hit={_hitDiagnostic ?? "not-tested"}.");
                }
            }
            catch (Exception ex)
            {
                // Diagnostics must not invalidate a successfully captured gesture
                // or a query whose synchronous prefix already opened the target.
                ClangdLog.WriteLine(
                    $"nav: mouse gesture view={_viewId} stage={stage} diagnostic-error={ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (stage == "up")
                    _traceGesture = false;
            }
        }

        private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private string OptionsDiagnostic() =>
            $"takeover={_takeover()} closed={_closed || _view.IsClosed} " +
            $"clickEnabled={_view.Options.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefEnabledId)} " +
            $"configuredModifier={_view.Options.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefModifierKeyId)} " +
            $"peek={_view.Options.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefOpensPeekId)}";

        private static string VisualAncestors(object source)
        {
            var names = new List<string>();
            var current = source as DependencyObject;
            for (var depth = 0; current != null && depth < 8; depth++)
            {
                names.Add(current.GetType().FullName + (current is FrameworkElement element ? "#" + element.Name : ""));
                if (!(current is Visual) && !(current is System.Windows.Media.Media3D.Visual3D))
                    break;
                current = VisualTreeHelper.GetParent(current);
            }
            return names.Count == 0 ? source?.GetType().FullName ?? "null" : string.Join(" > ", names);
        }

        private void OnMove(object sender, MouseEventArgs e) => ProcessMouseMove(e.GetPosition(_view.VisualElement));

        private bool IsTextSurface(object source)
        {
            // VS draws text in a non-hit-testable TextContentLayer. Its content
            // host/manipulation panels receive text clicks instead. Only accept
            // those structural panels; deeper UIElements are adornments/controls.
            var contentHost = (_view.VisualElement as ContentControl)?.Content as Panel;
            // VS's drawing-only selection is hit-testable and can become the up
            // source after native selection on down. Accept only its verified
            // editor-owned chain; arbitrary adornments/controls remain excluded.
            if (IsEditorVisual(source,
                               "Microsoft.VisualStudio.Text.MultiSelection.Implementation.UI.SelectionAdornment"))
            {
                var layer = VisualTreeHelper.GetParent((DependencyObject)source);
                var stack = layer == null ? null : VisualTreeHelper.GetParent(layer);
                if (IsEditorVisual(layer, "Microsoft.VisualStudio.Text.Editor.Implementation.AdornmentLayer") &&
                    IsEditorVisual(stack, "Microsoft.VisualStudio.Text.Editor.Implementation.ViewStack") &&
                    contentHost != null && VisualTreeHelper.GetParent(stack) == contentHost)
                    return true;
            }
            var current = source as DependencyObject;
            while (current != null && current != _view.VisualElement)
            {
                if (current == contentHost)
                    return true;
                if (current is UIElement && !(current is Panel && VisualTreeHelper.GetParent(current) == contentHost))
                    return false;
                if (!(current is Visual) && !(current is System.Windows.Media.Media3D.Visual3D))
                    return false;
                current = VisualTreeHelper.GetParent(current);
            }
            return current == _view.VisualElement;
        }

        private static bool IsEditorVisual(object value, string name)
        {
            var type = value?.GetType();
            return type?.FullName == name && type.Assembly.GetName().Name == "Microsoft.VisualStudio.Platform.VSEditor";
        }

        internal string ProcessMouseDown(Point point, MouseButton button, ModifierKeys modifiers, int clicks,
                                         bool textSurface)
        {
            Invalidate("new-down");
            _hitDiagnostic = null;
            _candidateCleared = null;
            if (!textSurface)
                return "non-text-or-handled";
            if (button != MouseButton.Left)
                return "non-left";
            if (clicks != 1)
                return "non-single-down";
            if (modifiers != ModifierKeys.Control)
                return "modifier-mismatch";
            if (!Enabled())
                return "disabled-or-peek";
            _candidate = Hit(point);
            return _candidate == null ? "hit-rejected" : "candidate";
        }

        internal void ProcessMouseMove(Point point)
        {
            if (_candidate != null && Dragged(_candidate.Mouse, point))
            {
                _candidate = null;
                _candidateCleared = "drag";
            }
        }

        internal string ProcessMouseUp(Point point, MouseButton button, ModifierKeys modifiers, int clicks,
                                       bool textSurface, Action markHandled)
        {
            var down = _candidate;
            _candidate = null;
            if (down == null)
                return "no-candidate/" + (_candidateCleared ?? "down-rejected");
            if (!textSurface)
                return "non-text-or-handled";
            if (button != MouseButton.Left)
                return "non-left";
            if (clicks != 1)
                return "non-single-up";
            if (modifiers != ModifierKeys.Control)
                return "modifier-mismatch";
            if (!Enabled())
                return "disabled-or-peek";
            if (Dragged(down.Mouse, point))
                return "drag";
            if (_source.CurrentSnapshot != down.Point.Snapshot)
                return "snapshot-changed";
            var up = Hit(point);
            if (up == null)
                return "hit-rejected";
            if (up.Point.Snapshot != down.Point.Snapshot || up.Token != down.Token || up.Path != down.Path)
                return "token-or-document-changed";

            var generation = _generation;
            var request = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _request = request;
            var cancellation = request.Token;
            Func<bool> valid = () => generation == _generation && !cancellation.IsCancellationRequested && Enabled() &&
                                     _source.CurrentSnapshot == down.Point.Snapshot &&
                                     _documents.TryGetTextDocument(_source, out var document) &&
                                     document.FilePath == down.Path;
            var line = down.Point.GetContainingLine();
            var character = down.Point.Position - line.Start.Position;
            // Must precede RunAsync's synchronous prefix and native MouseUp Navigate.
            markHandled();
            ClangdLog.WriteLine(
                $"nav: mouse request {down.Path}({line.LineNumber},{character}) view={_viewId} snapshot={down.Point.Snapshot.Version?.VersionNumber}.");
            _pendingCheck.Start();
            _run(async () =>
                 {
                     try
                     {
                         var navigated =
                             valid() && await _navigate(down.Path, line.LineNumber, character, cancellation, valid);
                         ClangdLog.WriteLine(
                             $"nav: mouse result view={_viewId} path={down.Path} navigated={navigated}.");
                     }
                     catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                     {
                         ClangdLog.WriteLine($"nav: mouse result view={_viewId} path={down.Path} cancelled=True.");
                     }
                     catch (Exception ex)
                     {
                         ClangdLog.WriteLine($"nav: mouse query failed view={_viewId} path={down.Path}: {ex.Message}");
                     }
                     finally
                     {
                         if (_request == request)
                         {
                             _request = null;
                             _pendingCheck.Stop();
                         }
                         request.Dispose();
                     }
                 });
            return "query-started";
        }

        private Candidate Hit(Point mouse)
        {
            _hitDiagnostic = "type=" + _source.ContentType.TypeName;
            if (!(_source.ContentType.IsOfType("C/C++") || _source.ContentType.IsOfType("cpp-clangd")))
                return RejectHit("non-cpp-content");
            if (!_documents.TryGetTextDocument(_source, out var document))
                return RejectHit("no-document");
            if (string.IsNullOrEmpty(document.FilePath) || !ClangdFileExtensions.IsCppFile(document.FilePath))
                return RejectHit("non-cpp-path");
            var line = _view.TextViewLines?.GetTextViewLineContainingYCoordinate(mouse.Y + _view.ViewportTop);
            if (line == null)
                return RejectHit("no-formatted-line");
            var visualPoint = line?.GetBufferPositionFromXCoordinate(mouse.X + _view.ViewportLeft, textOnly: true);
            if (!visualPoint.HasValue)
                return RejectHit("no-text-position");
            _hitDiagnostic += " visualPosition=" + visualPoint.Value.Position;
            var mapped = _view.BufferGraph.MapDownToBuffer(visualPoint.Value, PointTrackingMode.Positive, _source,
                                                           PositionAffinity.Successor);
            if (!mapped.HasValue)
                return RejectHit("no-source-mapping");
            if (mapped.Value.Snapshot != _source.CurrentSnapshot)
                return RejectHit("mapped-snapshot-stale");
            var point = mapped.Value;
            _hitDiagnostic += $" sourcePosition={point.Position} snapshot={point.Snapshot.Version?.VersionNumber}";
            if (point.Position >= point.Snapshot.Length || !IdentifierPart(point.GetChar()))
                return RejectHit("non-identifier-character");
            var extent = _navigator.GetExtentOfWord(point);
            _hitDiagnostic +=
                $" extent={extent.Span.Start.Position}:{extent.Span.Length} significant={extent.IsSignificant}";
            if (!extent.IsSignificant || !extent.Span.Contains(point) || extent.Span.Length == 0)
                return RejectHit("invalid-word-extent");
            var word = extent.Span.GetText();
            _hitDiagnostic +=
                " word=" + word.Substring(0, Math.Min(word.Length, 80)).Replace("\r", "\\r").Replace("\n", "\\n");
            if (!(char.IsLetter(word[0]) || word[0] == '_') || !word.All(IdentifierPart))
                return RejectHit("non-identifier-word");
            // Classifier handles C++ comments, strings and preprocessor context;
            // macro identifiers remain eligible, without parsing C++ ourselves.
            foreach (var span in _classifier.GetClassificationSpans(new SnapshotSpan(point, 1)))
            {
                var name = span.ClassificationType.Classification ?? "";
                _hitDiagnostic += " classification=" + name;
                if (span.ClassificationType.IsOfType("comment") || span.ClassificationType.IsOfType("string") ||
                    name.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("literal", StringComparison.OrdinalIgnoreCase) >= 0)
                    return RejectHit("excluded-classification");
            }
            return new Candidate { Mouse = mouse, Point = point, Token = extent.Span, Path = document.FilePath };
        }

        private Candidate RejectHit(string reason)
        {
            _hitDiagnostic += " rejected=" + reason;
            return null;
        }

        private static bool IdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
        private static bool Dragged(Point start, Point end) =>
            Math.Abs(start.X - end.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(start.Y - end.Y) > SystemParameters.MinimumVerticalDragDistance;

        private void Invalidate(string reason = "invalidated")
        {
            if ((_traceGesture && _candidate != null) || _request != null)
                ClangdLog.WriteLine(
                    $"nav: mouse gesture view={_viewId} stage=invalidate reason={reason} candidate={_candidate != null} pending={_request != null}.");
            if (_candidate != null)
                _candidateCleared = reason;
            _generation++;
            _candidate = null;
            var request = _request;
            _request = null;
            _pendingCheck.Stop();
            request?.Cancel(); // The running query owns disposal.
        }

        private void OnPendingCheck(object sender, EventArgs e)
        {
            // Takeover settings can change via an external file watcher, which has
            // no UI event. Poll only while a request is pending; never move focus.
            if (!Enabled())
                Invalidate("settings-changed");
        }
        private void OnChanged(object sender, TextContentChangedEventArgs e) => Invalidate("snapshot-changed");
        private void OnOptionChanged(object sender, EditorOptionChangedEventArgs e)
        {
            if (e.OptionId == DefaultWpfViewOptions.ClickGoToDefEnabledName ||
                e.OptionId == DefaultWpfViewOptions.ClickGoToDefModifierKeyName ||
                e.OptionId == DefaultWpfViewOptions.ClickGoToDefOpensPeekName)
                Invalidate("option-changed/" + e.OptionId);
        }
        private void OnClosed(object sender, EventArgs e) => Dispose();
        private void OnLostFocus(object sender, EventArgs e) => Invalidate("lost-focus");

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            Invalidate("closed");
            _view.VisualElement.RemoveHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnDown));
            _view.VisualElement.RemoveHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(OnUp));
            _view.VisualElement.PreviewMouseMove -= OnMove;
            _view.Closed -= OnClosed;
            _view.LostAggregateFocus -= OnLostFocus;
            _source.Changed -= OnChanged;
            _view.Options.OptionChanged -= OnOptionChanged;
            _pendingCheck.Tick -= OnPendingCheck;
        }

        private sealed class Candidate
        {
            internal Point Mouse;
            internal SnapshotPoint Point;
            internal SnapshotSpan Token;
            internal string Path;
        }
    }
}
