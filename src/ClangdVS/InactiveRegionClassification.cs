using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClangdVS
{
    internal sealed class InactiveRegionStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<Uri, Tuple<int, int, int, int>[]> _ranges =
            new Dictionary<Uri, Tuple<int, int, int, int>[]>();
        private readonly List<WeakReference<InactiveRegionTagger>> _taggers =
            new List<WeakReference<InactiveRegionTagger>>();

        internal void Register(InactiveRegionTagger tagger)
        {
            lock (_gate)
                _taggers.Add(new WeakReference<InactiveRegionTagger>(tagger));
        }

        internal void Update(Uri uri, IEnumerable<Tuple<int, int, int, int>> ranges)
        {
            if (uri == null)
                return;
            lock (_gate)
                _ranges[uri] = (ranges ?? Enumerable.Empty<Tuple<int, int, int, int>>()).ToArray();
            Notify(uri);
        }

        internal IReadOnlyList<Tuple<int, int, int, int>> Get(Uri uri)
        {
            if (uri == null)
                return Array.Empty<Tuple<int, int, int, int>>();
            lock (_gate)
                return _ranges.TryGetValue(uri, out var ranges)
                           ? (IReadOnlyList<Tuple<int, int, int, int>>)ranges.ToArray()
                           : Array.Empty<Tuple<int, int, int, int>>();
        }

        internal void Remove(Uri uri)
        {
            if (uri == null)
                return;
            lock (_gate)
                _ranges.Remove(uri);
            Notify(uri);
        }

        internal void ClearAll()
        {
            Uri[] uris;
            lock (_gate)
            {
                uris = _ranges.Keys.ToArray();
                _ranges.Clear();
            }
            foreach (var uri in uris)
                Notify(uri);
        }

        private void Notify(Uri uri)
        {
            List<InactiveRegionTagger> listeners;
            lock (_gate)
            {
                listeners = new List<InactiveRegionTagger>();
                for (var index = _taggers.Count - 1; index >= 0; index--)
                {
                    if (_taggers[index].TryGetTarget(out var tagger))
                        listeners.Add(tagger);
                    else
                        _taggers.RemoveAt(index);
                }
            }
            foreach (var tagger in listeners)
                tagger.OnStoreChanged(uri);
        }
    }

    internal static class InactiveRegionServices
    {
        internal static readonly InactiveRegionStore Store = new InactiveRegionStore();

        [Export]
        [Name("clangd.inactiveRegion")]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition InactiveRegionType;

        [Export(typeof(EditorFormatDefinition))]
        [ClassificationType(ClassificationTypeNames = "clangd.inactiveRegion")]
        [Name("clangd.inactiveRegion")]
        [UserVisible(true)]
        [Order(After = Priority.Default, Before = Priority.High)]
        internal sealed class InactiveRegionFormat : ClassificationFormatDefinition
        {
            internal InactiveRegionFormat()
            {
                DisplayName = "clangd 非活动代码";
                ForegroundOpacity = 0.55;
            }
        }
    }

    [Export(typeof(ITaggerProvider))]
    [ContentType("cpp-clangd")]
    [TagType(typeof(ClassificationTag))]
    internal sealed class InactiveRegionTaggerProvider : ITaggerProvider
    {
        [Import]
        internal IClassificationTypeRegistryService ClassificationTypes = null;

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (buffer == null)
                return null;
            return InactiveRegionTagger.GetOrCreate(
                       buffer, ClassificationTypes.GetClassificationType("clangd.inactiveRegion")) as ITagger<T>;
        }
    }

    internal sealed class InactiveRegionTagger : ITagger<ClassificationTag>, IDisposable
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ITextBuffer, InactiveRegionTagger>
            Taggers = new System.Runtime.CompilerServices.ConditionalWeakTable<ITextBuffer, InactiveRegionTagger>();

        private readonly ITextBuffer _buffer;
        private readonly IClassificationType _classification;
        private readonly Uri _uri;

        internal InactiveRegionTagger(ITextBuffer buffer, IClassificationType classification)
        {
            _buffer = buffer;
            _classification = classification;
            var document = buffer.Properties.PropertyList.Select(pair => pair.Value).OfType<ITextDocument>()
                                  .FirstOrDefault();
            if (document != null && Uri.TryCreate(document.FilePath, UriKind.Absolute, out var uri))
                _uri = uri;
            InactiveRegionServices.Store.Register(this);
            buffer.Changed += OnBufferChanged;
        }

        internal static InactiveRegionTagger GetOrCreate(ITextBuffer buffer, IClassificationType classification)
        {
            lock (Taggers)
            {
                if (!Taggers.TryGetValue(buffer, out var tagger))
                {
                    tagger = new InactiveRegionTagger(buffer, classification);
                    Taggers.Add(buffer, tagger);
                }
                return tagger;
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_uri == null || !ClangdSettings.Instance.EnableInactiveRegions)
                yield break;
            var first = spans.FirstOrDefault();
            var snapshot = first.Snapshot == null ? _buffer.CurrentSnapshot : first.Snapshot;
            foreach (var range in InactiveRegionServices.Store.Get(_uri))
            {
                var start = ToOffset(snapshot, range.Item1, range.Item2);
                var end = ToOffset(snapshot, range.Item3, range.Item4);
                if (start < 0 || end <= start)
                    continue;
                var tagSpan = new SnapshotSpan(snapshot, start, Math.Min(end - start, snapshot.Length - start));
                if (spans.Any(span => span.IntersectsWith(tagSpan)))
                    yield return new TagSpan<ClassificationTag>(tagSpan, new ClassificationTag(_classification));
            }
        }

        private static int ToOffset(ITextSnapshot snapshot, int line, int character)
        {
            if (line < 0 || line >= snapshot.LineCount)
                return -1;
            var snapshotLine = snapshot.GetLineFromLineNumber(line);
            return snapshotLine.Start.Position + Math.Max(0, Math.Min(character, snapshotLine.Length));
        }

        internal void OnStoreChanged(Uri uri)
        {
            if (_uri == null || !Equals(uri, _uri))
                return;
            RaiseChanged();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs args) => RaiseChanged();

        private void RaiseChanged()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var snapshot = _buffer.CurrentSnapshot;
                if (snapshot.Length > 0)
                    TagsChanged?.Invoke(this,
                                        new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
            });
        }

        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            if (_uri != null)
                InactiveRegionServices.Store.Remove(_uri);
        }
    }

    internal static class InactiveRegionNotification
    {
        public static void OnInactiveRegions(JObject payload)
        {
            if (payload == null || !Uri.TryCreate((string)(payload["textDocument"]?["uri"] ?? payload["uri"]),
                                                UriKind.Absolute, out var uri) ||
                !uri.IsFile)
                return;
            var ranges = new List<Tuple<int, int, int, int>>();
            if (payload["regions"] is JArray values)
            {
                foreach (var value in values.OfType<JObject>())
                {
                    if (TryPosition(value["start"], out var startLine, out var startCharacter) &&
                        TryPosition(value["end"], out var endLine, out var endCharacter))
                    {
                        ranges.Add(Tuple.Create(startLine, startCharacter, endLine, endCharacter));
                    }
                }
            }
            InactiveRegionServices.Store.Update(uri, ranges);
        }

        private static bool TryPosition(JToken token, out int line, out int character)
        {
            line = -1;
            character = -1;
            return token is JObject position && int.TryParse((string)position["line"], out line) &&
                   int.TryParse((string)position["character"], out character) && line >= 0 && character >= 0;
        }
    }
}
