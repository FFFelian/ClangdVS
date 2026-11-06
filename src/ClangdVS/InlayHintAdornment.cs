using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace ClangdVS
{
    // Participate while the document is still native C/C++; cpp-clangd derives
    // from it. Do not gate creation on takeover: routing happens after view birth.
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("C/C++")]
    [TagType(typeof(IntraTextAdornmentTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class InlayHintTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal ITextDocumentFactoryService Documents = null;

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (typeof(T) != typeof(IntraTextAdornmentTag) || !(textView is IWpfTextView view) ||
                view.IsClosed || !ReferenceEquals(buffer, view.TextDataModel.DocumentBuffer) ||
                !Documents.TryGetTextDocument(buffer, out var document))
                return null;
            return InlayHintCoordinator.GetOrCreate(view, buffer, document) as ITagger<T>;
        }
    }

    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class InlayHintViewListener : IWpfTextViewCreationListener
    {
        [Import]
        internal ITextDocumentFactoryService Documents = null;

        public void TextViewCreated(IWpfTextView textView)
        {
            if (textView == null || textView.IsClosed)
                return;
            var source = textView.TextDataModel.DocumentBuffer;
            if (source.ContentType.IsOfType("C/C++") && Documents.TryGetTextDocument(source, out var document))
                InlayHintCoordinator.GetOrCreate(textView, source, document);
        }
    }

    /// <summary>
    /// One source-buffer tagger per view. The VS intra-text aggregator maps these
    /// zero-length tags and performs space negotiation, measuring and placement.
    /// All editor state and TagsChanged notifications are confined to the view dispatcher.
    /// </summary>
    internal sealed class InlayHintCoordinator : ITagger<IntraTextAdornmentTag>
    {
        private static readonly object CoordinatorsGate = new object();
        private static readonly List<WeakReference<InlayHintCoordinator>> Coordinators =
            new List<WeakReference<InlayHintCoordinator>>();
        private static long _sessionGeneration;
        private readonly IWpfTextView _view;
        private readonly Dispatcher _dispatcher;
        private readonly ITextBuffer _source;
        private readonly ITextDocument _document;
        private readonly Func<bool> _enabled;
        private readonly Func<object> _connection;
        private readonly Func<object, string, int, int, CancellationToken, Task<JToken>> _requestHints;
        private readonly List<ITagSpan<IntraTextAdornmentTag>> _tags = new List<ITagSpan<IntraTextAdornmentTag>>();
        private NormalizedSnapshotSpanCollection _coverage = new NormalizedSnapshotSpanCollection();
        private const int PrefetchMarginLines = 256;
        private const int CacheMarginLines = 512;
        private const int RefillThresholdLines = 128;
        private Request _scheduled;
        private Request _pending;
        private Request _published;
        private bool _attached;
        private bool _debounceEdits;

        internal static InlayHintCoordinator GetOrCreate(IWpfTextView view, ITextBuffer source,
                                                         ITextDocument document) =>
            view.Properties.GetOrCreateSingletonProperty(() => {
                var coordinator = new InlayHintCoordinator(view, source, document,
                    () => ClangdSettings.Instance.EnableInlayHints &&
                          ClangdSettings.Instance.TakeOverFromNativeIntelliSense &&
                          source.ContentType.IsOfType("cpp-clangd"),
                    () => ClangdLanguageClient.Current is ClangdLanguageClient client && client.IsRunning
                              ? client : null,
                    (connection, uri, start, end, token) =>
                        ((ClangdLanguageClient)connection).RequestInlayHintsAsync(uri, start, end, token));
                coordinator.Attach();
                return coordinator;
            });

        internal InlayHintCoordinator(IWpfTextView view, ITextBuffer source, ITextDocument document,
                                       Func<bool> enabled, Func<object> connection,
                                       Func<object, string, int, int, CancellationToken, Task<JToken>> requestHints)
        {
            _view = view;
            _dispatcher = view.VisualElement.Dispatcher;
            _source = source;
            _document = document;
            _enabled = enabled;
            _connection = connection;
            _requestHints = requestHints;
            lock (CoordinatorsGate)
                Coordinators.Add(new WeakReference<InlayHintCoordinator>(this));
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        // Both entry points may be called from any thread. Increment before posting
        // so an already queued response cannot win against a lifecycle notification.
        internal static void SettingsChanged() => NotifyCoordinators();

        // Call after Current/IsRunning/RPC changes, including reconnects on the SAME
        // client instance. Client reference alone cannot identify an RPC connection.
        internal static void SessionChanged() => NotifyCoordinators();

        private static void NotifyCoordinators()
        {
            Interlocked.Increment(ref _sessionGeneration);
            List<InlayHintCoordinator> current;
            lock (CoordinatorsGate)
            {
                current = new List<InlayHintCoordinator>();
                for (var index = Coordinators.Count - 1; index >= 0; index--)
                {
                    if (Coordinators[index].TryGetTarget(out var coordinator))
                        current.Add(coordinator);
                    else
                        Coordinators.RemoveAt(index);
                }
            }
            foreach (var coordinator in current)
            {
                var dispatcher = coordinator._dispatcher;
                if (dispatcher.HasShutdownStarted)
                    continue;
                Action refresh = () => {
                    if (!coordinator._attached)
                        return;
                    coordinator.Reset();
                    coordinator.Schedule();
                };
                if (dispatcher.CheckAccess())
                    refresh();
                else
                    dispatcher.BeginInvoke(refresh);
            }
        }

        internal void Attach()
        {
            if (_attached || _view.IsClosed)
                return;
            _attached = true;
            _view.LayoutChanged += OnLayoutChanged;
            _source.Changed += OnBufferChanged;
            _source.ContentTypeChanged += OnContentTypeChanged;
            _view.Closed += OnClosed;
            Schedule();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs args) => Schedule();

        private void OnBufferChanged(object sender, TextContentChangedEventArgs args)
        {
            Reset(debounceEdits: true);
            Schedule();
        }

        private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs args)
        {
            Reset();
            Schedule();
        }

        private void Schedule()
        {
            if (!_attached || _view.IsClosed)
                return;
            var connection = _connection();
            if (!_enabled() || connection == null ||
                !Uri.TryCreate(_document.FilePath, UriKind.Absolute, out var uri))
            {
                Reset();
                return;
            }
            var snapshot = _source.CurrentSnapshot;
            // A new viewport does not invalidate published adornments. Only the
            // document/session identity does; GetTags applies the same guard.
            if ((_published != null && !HasCurrentIdentity(_published)) ||
                (_scheduled != null && !HasCurrentIdentity(_scheduled)))
                Reset();
            var visible = VisibleSourceSpans(snapshot);
            if (visible.Count == 0)
            {
                CancelPending();
                return;
            }
            var generation = Volatile.Read(ref _sessionGeneration);
            var first = visible[0].Start.GetContainingLine().LineNumber;
            var lastSpan = visible[visible.Count - 1];
            // End is exclusive except at EOF, where it can denote the visible
            // empty last line of a newline-terminated document.
            var lastPosition = lastSpan.Length == 0 || lastSpan.End.Position == snapshot.Length
                                   ? lastSpan.End.Position : lastSpan.End.Position - 1;
            var last = snapshot.GetLineFromPosition(lastPosition).LineNumber;
            // Keep useful prefetch alive even when the viewport is already cached.
            // Query coverage, not just its original viewport, determines reuse.
            if (_scheduled != null && Covers(_scheduled.Query, visible))
            {
                _scheduled.ViewportStartLine = first;
                _scheduled.ViewportEndLine = last;
                return; // TagsChanged/layout must not create a request loop.
            }
            var naturalSource = ReferenceEquals(_view.VisualSnapshot, snapshot);
            // Refill before visible hints run out, with hysteresis between the
            // 128-line guard and the 256-line query. Projections keep exact coverage.
            var guard = naturalSource ? new NormalizedSnapshotSpanCollection(new SnapshotSpan(
                snapshot.GetLineFromLineNumber(Math.Max(0, first - RefillThresholdLines)).Start,
                snapshot.GetLineFromLineNumber(Math.Min(snapshot.LineCount - 1, last + RefillThresholdLines)).End))
                : visible;
            if (Covers(_coverage, guard))
            {
                CancelPending();
                return;
            }

            CancelPending();
            var query = visible;
            var start = first;
            var end = last;
            // Only expand a natural source view. A projection's RPC envelope can
            // contain gaps which are neither valid tags nor successful coverage.
            if (naturalSource)
            {
                start = Math.Max(0, first - PrefetchMarginLines);
                end = Math.Min(snapshot.LineCount - 1, last + PrefetchMarginLines);
                query = new NormalizedSnapshotSpanCollection(new SnapshotSpan(
                    snapshot.GetLineFromLineNumber(start).Start, snapshot.GetLineFromLineNumber(end).End));
            }
            var request = new Request(snapshot, query, connection, generation, uri.AbsoluteUri,
                                      start, end, first, last, _debounceEdits);
            _scheduled = _pending = request;
            _ = RefreshAsync(request);
        }

        private NormalizedSnapshotSpanCollection VisibleSourceSpans(ITextSnapshot snapshot)
        {
            var lines = _view.TextViewLines;
            if (lines == null || !lines.IsValid || lines.Count == 0 ||
                lines.FirstVisibleLine == null || lines.LastVisibleLine == null)
                return new NormalizedSnapshotSpanCollection();
            var formatted = new SnapshotSpan(lines.FirstVisibleLine.Start, lines.LastVisibleLine.End);
            if (ReferenceEquals(formatted.Snapshot.TextBuffer, _source))
                return new NormalizedSnapshotSpanCollection(formatted.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive));
            return new NormalizedSnapshotSpanCollection(_view.BufferGraph
                .MapDownToBuffer(formatted, SpanTrackingMode.EdgeInclusive, _source)
                .Select(span => span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive)));
        }

        private static bool Covers(NormalizedSnapshotSpanCollection coverage,
                                   NormalizedSnapshotSpanCollection spans) =>
            spans.All(span => coverage.Any(covered => covered.Start <= span.Start && span.End <= covered.End));

        private bool HasCurrentIdentity(Request request) =>
            _attached && !_view.IsClosed && _enabled() &&
            request.Generation == Volatile.Read(ref _sessionGeneration) &&
            ReferenceEquals(request.Connection, _connection()) &&
            ReferenceEquals(request.Snapshot, _source.CurrentSnapshot) &&
            request.Snapshot.Version.VersionNumber == _source.CurrentSnapshot.Version.VersionNumber &&
            Uri.TryCreate(_document.FilePath, UriKind.Absolute, out var uri) && request.Uri == uri.AbsoluteUri;

        private bool IsCurrent(Request request, CancellationToken token) =>
            !token.IsCancellationRequested && ReferenceEquals(request, _scheduled) && HasCurrentIdentity(request);

        private async Task RefreshAsync(Request request)
        {
            var token = request.Cancellation.Token;
            var dispatcher = _dispatcher;
            var published = false;
            try
            {
                if (request.Debounce)
                    await Task.Delay(150, token).ConfigureAwait(false);
                Task<JToken> pending = null;
                await dispatcher.InvokeAsync(() => {
                    if (IsCurrent(request, token))
                    {
                        _debounceEdits = false;
                        pending = _requestHints(request.Connection, request.Uri, request.StartLine, request.EndLine, token);
                    }
                }).Task.ConfigureAwait(false);
                if (pending == null)
                    return;
                var response = await pending.ConfigureAwait(false);
                await dispatcher.InvokeAsync(() => {
                    if (IsCurrent(request, token))
                    {
                        Publish(request, response);
                        published = response is JArray;
                    }
                }).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"inlay: request failed: {ex.Message}");
                if (!dispatcher.HasShutdownStarted)
                    await dispatcher.InvokeAsync(() => {
                        if (ReferenceEquals(_scheduled, request))
                            CancelPending(); // Retry later without discarding published hints.
                    }).Task.ConfigureAwait(false);
            }
            finally
            {
                // Cancelled requests own their CTS until the transport has finished.
                // CancelPending removes _pending before cancelling it, so it cannot touch a
                // disposed CTS even when another response is already queued.
                try
                {
                    if (!dispatcher.HasShutdownStarted)
                        await dispatcher.InvokeAsync(() => {
                            if (ReferenceEquals(_pending, request))
                                _pending = null;
                            // Completed/cancelled transport work is not coverage.
                            // Successful responses are remembered by _coverage.
                            if (ReferenceEquals(_scheduled, request))
                            {
                                _scheduled = null;
                                // A reused query can finish after scrolling has
                                // exhausted its guard. Empty successful results
                                // also need this check: they raise no TagsChanged.
                                // Do not retry failures or touch a newer request.
                                if (published && HasCurrentIdentity(request))
                                    Schedule();
                            }
                        }).Task.ConfigureAwait(false);
                }
                finally
                {
                    request.Cancellation.Dispose();
                }
            }
        }

        private void Publish(Request request, JToken response)
        {
            if (!(response is JArray hints))
            {
                CancelPending(); // Not a successful empty result; leave coverage unchanged.
                return;
            }
            var replaced = _tags.Where(tag => request.Query.Any(span =>
                span.Start <= tag.Span.Start && tag.Span.Start <= span.End)).ToList();
            var next = _tags.Except(replaced).ToList();
            foreach (var hint in hints.OfType<JObject>())
            {
                if (!TryPosition(request.Snapshot, hint["position"], out var offset) ||
                    !request.Query.Any(span => span.Start.Position <= offset && offset <= span.End.Position))
                    continue;
                var label = ReadLabel(hint["label"]);
                if (string.IsNullOrEmpty(label))
                    continue;
                if ((bool?)hint["paddingLeft"] == true)
                    label = " " + label;
                if ((bool?)hint["paddingRight"] == true)
                    label += " ";
                var point = new SnapshotPoint(request.Snapshot, offset);
                // Cache query-valid source hints even while collapsed/unmapped.
                // Coverage survives projection changes; GetTags checks live mapping.
                // Match once per occurrence, including multiple hints at one point.
                var existing = replaced.FirstOrDefault(tag => tag.Span.Start == point &&
                    ((TextBlock)tag.Tag.Adornment).Text == label);
                if (existing != null)
                {
                    replaced.Remove(existing);
                    next.Add(existing);
                    continue;
                }
                var element = new TextBlock {
                    Text = label,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(185, 145, 145, 145)),
                    Margin = new Thickness(3, 0, 2, 0),
                    IsHitTestVisible = false,
                };
                // SDK XML: non-null affinity is required for insertion (zero-length)
                // tags. VS measures DesiredSize and reserves horizontal space.
                var tag = new IntraTextAdornmentTag(element, null, PositionAffinity.Predecessor);
                next.Add(new TagSpan<IntraTextAdornmentTag>(new SnapshotSpan(point, 0), tag));
            }
            // Retain a bounded neighborhood, not every viewport ever visited.
            // The current viewport is always protected, even if unusually large.
            var snapshot = request.Snapshot;
            var first = snapshot.GetLineFromLineNumber(Math.Max(0, request.ViewportStartLine - CacheMarginLines)).Start;
            var last = snapshot.GetLineFromLineNumber(Math.Min(snapshot.LineCount - 1,
                                                               request.ViewportEndLine + CacheMarginLines)).End;
            next = next.Where(tag => first <= tag.Span.Start && tag.Span.Start <= last)
                       .OrderBy(tag => tag.Span.Start.Position).ToList();
            var changed = _tags.Except(next).Concat(next.Except(_tags)).ToArray();
            _coverage = new NormalizedSnapshotSpanCollection(_coverage.Concat(request.Query)
                .Where(span => span.End >= first && span.Start <= last)
                .Select(span => new SnapshotSpan(span.Start < first ? first : span.Start,
                                                 span.End > last ? last : span.End)));
            _tags.Clear();
            _tags.AddRange(next);
            _published = request;
            // Commit coverage and objects before notification: VS may synchronously
            // query tags or raise LayoutChanged while negotiating adornment space.
            if (changed.Length != 0)
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot,
                    changed.Min(tag => tag.Span.Start.Position),
                    changed.Max(tag => tag.Span.Start.Position) - changed.Min(tag => tag.Span.Start.Position))));
        }

        private SnapshotPoint? MapToView(SnapshotPoint point)
        {
            var snapshot = _view.VisualSnapshot;
            return ReferenceEquals(point.Snapshot, snapshot) ? point :
                _view.BufferGraph.MapUpToSnapshot(point, PointTrackingMode.Positive,
                                                  PositionAffinity.Predecessor, snapshot);
        }

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (!_dispatcher.CheckAccess() || spans.Count == 0 || _published == null ||
                !ReferenceEquals(spans[0].Snapshot, _published.Snapshot) ||
                !HasCurrentIdentity(_published))
                return Enumerable.Empty<ITagSpan<IntraTextAdornmentTag>>();
            // Keep source coordinates for the VS aggregator, which maps all occurrences
            // in a projection. Reject points no longer present in this view's graph.
            return _tags.Where(tag => spans.Any(span => span.Start <= tag.Span.Start && tag.Span.Start <= span.End) &&
                                      MapToView(tag.Span.Start).HasValue).ToArray();
        }

        private static string ReadLabel(JToken label)
        {
            if (label?.Type == JTokenType.String)
                return (string)label;
            return label is JArray parts ? string.Concat(parts.OfType<JObject>()
                .Where(part => part["value"]?.Type == JTokenType.String).Select(part => (string)part["value"])) : null;
        }

        private static bool TryPosition(ITextSnapshot snapshot, JToken token, out int offset)
        {
            offset = -1;
            if (!(token is JObject position) || position["line"]?.Type != JTokenType.Integer ||
                position["character"]?.Type != JTokenType.Integer ||
                !int.TryParse((string)position["line"], out var line) ||
                !int.TryParse((string)position["character"], out var character) ||
                line < 0 || line >= snapshot.LineCount || character < 0)
                return false;
            var snapshotLine = snapshot.GetLineFromLineNumber(line);
            if (character > snapshotLine.Length)
                return false; // Never silently move a malformed position to the EOL.
            offset = snapshotLine.Start.Position + character;
            return true;
        }

        private void CancelPending()
        {
            _scheduled = null;
            var pending = _pending;
            _pending = null;
            pending?.Cancellation.Cancel();
        }

        private void Reset(bool debounceEdits = false)
        {
            CancelPending();
            // Set before TagsChanged: editor layout may reenter Schedule inline.
            _debounceEdits = debounceEdits;
            _published = null;
            _coverage = new NormalizedSnapshotSpanCollection();
            if (_tags.Count == 0)
                return;
            _tags.Clear();
            RaiseTagsChanged();
        }

        private void RaiseTagsChanged()
        {
            var snapshot = _source.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        private void OnClosed(object sender, EventArgs args)
        {
            if (!_attached)
                return;
            _attached = false;
            _view.LayoutChanged -= OnLayoutChanged;
            _source.Changed -= OnBufferChanged;
            _source.ContentTypeChanged -= OnContentTypeChanged;
            _view.Closed -= OnClosed;
            Reset();
            _view.Properties.RemoveProperty(typeof(InlayHintCoordinator));
            lock (CoordinatorsGate)
                Coordinators.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, this));
        }

        private sealed class Request
        {
            internal readonly ITextSnapshot Snapshot;
            internal readonly NormalizedSnapshotSpanCollection Query;
            internal readonly object Connection;
            internal readonly long Generation;
            internal readonly string Uri;
            internal readonly int StartLine, EndLine;
            internal int ViewportStartLine, ViewportEndLine;
            internal readonly bool Debounce;
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();

            internal Request(ITextSnapshot snapshot, NormalizedSnapshotSpanCollection query, object connection,
                             long generation, string uri, int startLine, int endLine,
                             int viewportStartLine, int viewportEndLine, bool debounce)
            {
                Snapshot = snapshot;
                Query = query;
                Connection = connection;
                Generation = generation;
                Uri = uri;
                StartLine = startLine;
                EndLine = endLine;
                ViewportStartLine = viewportStartLine;
                ViewportEndLine = viewportEndLine;
                Debounce = debounce;
            }
        }
    }
}
