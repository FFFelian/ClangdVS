using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Moq;

internal static class HoverDiagnosticsRegression
{
    internal static async Task RunAsync()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdHoverDiagnostics");
        Assert(type != null, "read-only cold-start hover diagnostics are missing");
        var describe = type.GetMethod("DescribeHostState", BindingFlags.Static | BindingFlags.NonPublic);
        var properties = new PropertyCollection();
        var buffer = new Mock<ITextBuffer>(MockBehavior.Strict);
        var contentType = new Mock<IContentType>();
        contentType.SetupGet(c => c.TypeName).Returns("cpp-clangd");
        contentType.SetupGet(c => c.BaseTypes).Returns(Array.Empty<IContentType>());
        contentType.Setup(c => c.IsOfType("languageserver-base")).Returns(true);
        contentType.Setup(c => c.IsOfType("C/C++")).Returns(true);
        buffer.SetupGet(b => b.ContentType).Returns(contentType.Object);
        buffer.SetupGet(b => b.Properties).Returns(properties);
        buffer.SetupAdd(b => b.ContentTypeChanged += It.IsAny<EventHandler<ContentTypeChangedEventArgs>>());
        var viewProperties = new PropertyCollection();
        var view = new Mock<ITextView>(MockBehavior.Strict);
        view.SetupGet(v => v.Properties).Returns(viewProperties);

        string Capture() => (string)describe.Invoke(null, new object[] { buffer.Object, view.Object });
        var missing = Capture();
        Assert(missing.Contains("lsp=True") && missing.Contains("sources=<not-created>") &&
                   missing.Contains("documentClients=<not-created>"),
               "diagnostics confuse uncreated caches with empty caches: " + missing);
        Assert(properties.PropertyList.Count() == 0 && viewProperties.PropertyList.Count() == 0,
               "diagnostics created host state while inspecting it");

        var client = new FakeClient { Name = "clangd", IsActive = true };
        var collection =
            new FakeClientsCollection { clients = new[] { client }, stateCookie = 7, serversStarted = true };
        var context = new Microsoft.VisualStudio.LanguageServer.Client.LanguageServiceClientManager.PerDocumentContext(
            collection);
        properties.AddProperty(context.GetType(), context);
        var cache = new Microsoft.VisualStudio.Language.Utilities.IntellisenseSourceCache.ViewSourceCache<
            Microsoft.VisualStudio.Language.Intellisense.Implementation.AsyncQuickInfoSession.OrderedSource>();
        cache._bufferCache.Add(buffer.Object,
                               new FakeSources { Sources = new[] { new FakeOrderedSource(new FakeHoverSource()) } });
        viewProperties.AddProperty(cache.GetType(), cache);
        var populated = Capture();
        Assert(populated.Contains("FakeHoverSource") && populated.Contains("cookie=7") &&
                   populated.Contains("started=True") && populated.Contains("name=clangd") &&
                   populated.Contains("active=True") && populated.Contains("uxDisabled=False"),
               "selected source/client state missing: " + populated);
        var hostSource = new Microsoft.VisualStudio.LanguageServer.Client.HoverSource();
        cache._bufferCache[buffer.Object] = new FakeSources { Sources = new[] { new FakeOrderedSource(hostSource) } };
        Assert(Capture().Contains("managerCookie=9"), "source snapshot lost the manager/cache cookie comparison");
        cache._bufferCache[buffer.Object] = new FakeSources { Sources = Array.Empty<FakeOrderedSource>() };
        collection.clients = Array.Empty<FakeClient>();
        var empty = Capture();
        Assert(empty.Contains("sources=<empty>") && empty.Contains("clients=<empty>"),
               "empty caches are not distinguishable: " + empty);
        client.Metadata.ThrowOnRead = true;
        collection.clients = new[] { client };
        Assert(Capture().Contains("uxDisabled=<unavailable>"),
               "host version/getter mismatch must degrade without throwing");
        Assert(properties.PropertyList.Count() == 1 && viewProperties.PropertyList.Count() == 1 &&
                   cache._bufferCache.Count == 1,
               "diagnostics mutated host caches");
        Console.WriteLine("PASS: read-only snapshots distinguish missing/empty/populated source and document-client " +
                          "caches; getter failures degrade");

        var providerType = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdHoverTraceProvider");
        Assert(providerType != null, "early QuickInfo trace provider missing");
        var provider = (IAsyncQuickInfoSourceProvider)Activator.CreateInstance(providerType, true);
        var source = provider.TryCreateQuickInfoSource(buffer.Object);
        var session = new Mock<IAsyncQuickInfoSession>(MockBehavior.Strict);
        session.SetupGet(s => s.TextView).Returns(view.Object);
        session.Setup(s => s.GetTriggerPoint(It.IsAny<ITextSnapshot>())).Returns((SnapshotPoint?)null);
        session.SetupAdd(s => s.StateChanged += It.IsAny<EventHandler<QuickInfoSessionStateChangedEventArgs>>());
        session.SetupRemove(s => s.StateChanged -= It.IsAny<EventHandler<QuickInfoSessionStateChangedEventArgs>>());
        buffer.SetupGet(b => b.CurrentSnapshot).Returns((ITextSnapshot)null);
        for (var i = 0; i < 10; i++)
            Assert(await source.GetQuickInfoItemAsync(session.Object, CancellationToken.None) == null,
                   "trace source contributed hover UI");
        session.Raise(s => s.StateChanged += null,
                      new QuickInfoSessionStateChangedEventArgs(QuickInfoSessionState.Calculating,
                                                                QuickInfoSessionState.Dismissed));
        source.Dispose();
        Assert(await source.GetQuickInfoItemAsync(session.Object, CancellationToken.None) == null,
               "disposed trace source contributed UI");
        buffer.VerifyNoOtherCallsExceptReads();
        var logDir = Environment.GetEnvironmentVariable("CLANGDVS_LOG_DIRECTORY");
        var logs = System.IO.File.ReadAllLines(System.IO.Path.Combine(logDir, "clangd.log"));
        Assert(logs.Count(l => l.Contains("hover-diag: quickinfo-invoke")) == 3,
               "QuickInfo logs are not bounded to first three invocations per initialization");
        Assert(logs.Any(l => l.Contains("quickinfo-finished") && l.Contains("state=Dismissed")),
               "post-LSP cache snapshot was not captured");
        var lifecycle = type.GetMethod("Lifecycle", BindingFlags.Static | BindingFlags.NonPublic);
        source = provider.TryCreateQuickInfoSource(buffer.Object);
        for (var i = 0; i < 10; i++)
            await source.GetQuickInfoItemAsync(session.Object, CancellationToken.None);
        lifecycle.Invoke(null, new object[] { "initialized", new object(), true });
        await source.GetQuickInfoItemAsync(session.Object, CancellationToken.None);
        source.Dispose();
        logs = System.IO.File.ReadAllLines(System.IO.Path.Combine(logDir, "clangd.log"));
        Assert(logs.Count(l => l.Contains("hover-diag: quickinfo-invoke")) == 7,
               "existing source did not reset diagnostic budget on initialization");
        Console.WriteLine(
            "PASS: QuickInfo observer returns no item, never queries a server, and bounds invocation logs");

        var listenerType = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdHoverViewTrace");
        var listener = (IWpfTextViewCreationListener)Activator.CreateInstance(listenerType, true);
        var wpf = new Mock<IWpfTextView>();
        wpf.SetupGet(v => v.TextBuffer).Returns(buffer.Object);
        wpf.SetupGet(v => v.Properties).Returns(viewProperties);
        wpf.SetupGet(v => v.TextSnapshot).Returns(new Mock<ITextSnapshot>().Object);
        var hoverArgs = new MouseHoverEventArgs(wpf.Object, 0, new Mock<IMappingPoint>().Object);
        listener.TextViewCreated(wpf.Object);
        for (var i = 0; i < 10; i++)
            wpf.Raise(v => v.MouseHover += null, hoverArgs);
        lifecycle.Invoke(null, new object[] { "initialized", new object(), true });
        wpf.Raise(v => v.MouseHover += null, hoverArgs);
        wpf.Raise(v => v.Closed += null, EventArgs.Empty);
        wpf.Raise(v => v.MouseHover += null, hoverArgs);
        logs = System.IO.File.ReadAllLines(System.IO.Path.Combine(logDir, "clangd.log"));
        Assert(logs.Count(l => l.Contains("hover-diag: mouse-hover")) == 4,
               "mouse observation budget/reset/close unsubscription failed");
        var observe = type.GetMethod("Observe", BindingFlags.Static | BindingFlags.NonPublic);
        observe.Invoke(null, new object[] { buffer.Object, view.Object, "repeat" });
        observe.Invoke(null, new object[] { buffer.Object, view.Object, "repeat" });
        logs = System.IO.File.ReadAllLines(System.IO.Path.Combine(logDir, "clangd.log"));
        Assert(logs.Count(l => l.Contains("buffer reason=repeat")) == 1, "unchanged scan state was logged repeatedly");
        Console.WriteLine(
            "PASS: mouse observations bounded per initialization, detach on close, repeated buffer state suppressed");
    }

    private static void VerifyNoOtherCallsExceptReads(this Mock<ITextBuffer> buffer)
    {
        Assert(buffer.Invocations.All(i => i.Method.Name == "get_Properties" || i.Method.Name == "get_ContentType" ||
                                           i.Method.Name == "get_CurrentSnapshot" ||
                                           i.Method.Name == "add_ContentTypeChanged"),
               "trace observer called a buffer mutator");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    internal sealed class FakeClientsCollection
    {
        internal FakeClient[] clients;
        internal int stateCookie;
        internal bool serversStarted;
    }
    internal sealed class FakeClient
    {
        public string Name { get; set; }
        public object Client { get; set; }
        public bool IsActive { get; set; }
        public bool IsDisposed { get; set; }
        public CancellationToken ShutdownToken { get; set; }
        private bool _isDocumentOpen;
        public bool ThrowOnOpenRead { get; set; }
        public bool IsDocumentOpen
        {
            get => ThrowOnOpenRead ? throw new InvalidOperationException("host changed") : _isDocumentOpen;
            set => _isDocumentOpen = value;
        }
        public int Notifications { get; private set; }
        public ITextSnapshot OpenSnapshot { get; private set; }
        public bool DeferOpen { get; set; }
        public bool ThrowAfterEnqueue { get; set; }
        public void EnqueueNotification<T>(
            Microsoft.VisualStudio.LanguageServer.Client.NotificationFactoryBase<T> factory,
            CancellationToken cancellationToken)
        {
            lock (this)
            {
                Assert(!cancellationToken.IsCancellationRequested, "host adapter used a canceled token");
                OpenSnapshot = ((Microsoft.VisualStudio.LanguageServer.Client.TextDocumentDidOpenNotificationFactory)(
                                    object)factory)
                                   .Snapshot;
                Notifications++;
                if (!DeferOpen)
                    IsDocumentOpen = true;
                if (ThrowAfterEnqueue)
                    throw new InvalidOperationException("accepted before failing");
            }
        }
        public FakeMetadata Metadata { get; } = new FakeMetadata();
    }
    internal sealed class FakeMetadata
    {
        internal bool ThrowOnRead;
        public bool IsUserExperienceDisabled =>
            ThrowOnRead ? throw new InvalidOperationException("host changed") : false;
        public string ClientName => null;
    }
    internal sealed class FakeSources
    {
        public FakeOrderedSource[] Sources { get; set; }
    }
    internal sealed class FakeOrderedSource
    {
        public object Source { get; }
        internal FakeOrderedSource(object source)
        {
            Source = source;
        }
    }
    internal sealed class FakeHoverSource
    {
    }
}

namespace Microsoft.VisualStudio.Language.Utilities
{
    internal static class IntellisenseSourceCache
    {
        internal sealed class ViewSourceCache<T>
        {
            internal readonly IDictionary _bufferCache = new Hashtable();
        }
    }
}

namespace Microsoft.VisualStudio.Language.Intellisense.Implementation
{
    internal sealed class AsyncQuickInfoSession
    {
        internal sealed class OrderedSource
        {
        }
    }
}

namespace Microsoft.VisualStudio.LanguageServer.Client
{
    internal abstract class NotificationFactoryBase<T>
    {
    }
    internal sealed class TextDocumentDidOpenNotificationFactory : NotificationFactoryBase<object>
    {
        public ITextSnapshot Snapshot { get; }
        public TextDocumentDidOpenNotificationFactory(ITextSnapshot snapshot)
        {
            Snapshot = snapshot;
        }
    }
    internal sealed class LanguageServiceClientManager
    {
        internal readonly int languageClientsCookie = 9;
        internal sealed class PerDocumentContext
        {
            public HoverDiagnosticsRegression.FakeClientsCollection DocumentClientsCollection { get; }
            internal PerDocumentContext(HoverDiagnosticsRegression.FakeClientsCollection collection)
            {
                DocumentClientsCollection = collection;
            }
        }
    }
    internal sealed class HoverSource
    {
        internal readonly LanguageServiceClientManager documentClientManager = new LanguageServiceClientManager();
    }
}
