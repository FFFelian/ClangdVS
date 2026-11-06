using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Moq;

internal static class MouseAttachmentRegression
{
    internal static Task RunAsync()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.MouseNavigationAttachment");
        Assert(type != null, "independent mouse attachment/diagnostic abstraction is missing");
        var sequence = new List<string>();
        var ensure = type.GetMethod("EnsureViewNavigation", BindingFlags.Static | BindingFlags.NonPublic);
        ensure.Invoke(
            null, new object[] { true, (Action)(() => sequence.Add("mouse")), (Action)(() => sequence.Add("filter")) });
        Assert(string.Join(",", sequence) == "mouse", "existingFilter=true skipped mouse attachment");
        sequence.Clear();
        ensure.Invoke(null, new object[] { false, (Action)(() => sequence.Add("mouse")),
                                           (Action)(() => sequence.Add("filter")) });
        Assert(string.Join(",", sequence) == "mouse,filter",
               "new view did not independently attach mouse and command filter");
        Console.WriteLine("PASS: existingFilter=true still calls mouse attach");

        foreach (var failure in new[] { "wpf-null", "documents-service-missing", "navigators-service-missing",
                                        "classifiers-service-missing", "source-not-in-view-graph", "navigator-null",
                                        "classifier-null", "no-source-document", "exception" })
        {
            var f = new Fixture(type) { Failure = failure };
            f.Attach();
            f.Attach();
            Assert(f.Created == 0, failure + " published a mouse handler");
            Assert(f.Logs.Count == 1 && f.Logs[0].Contains("nav: mouse attach skipped view=") &&
                       f.Logs[0].Contains("reason=" + failure) && f.Logs[0].Contains("existingFilter=True"),
                   failure + " missing bounded identity diagnostic");
            if (failure == "exception")
                Assert(f.Logs[0].Contains("adapter unavailable"), "attachment exception was swallowed");
            f.Failure = null;
            f.Attach();
            Assert(f.Created == 1 && f.AdapterCalls == 3, failure + " did not retry the same IVsTextView");
            f.Attach();
            Assert(f.Created == 1, "same WPF view attached twice");
            var split = new Mock<IVsTextView>().Object;
            f.Attach(split);
            Assert(f.Created == 1, "two adapters mapping to one WPF view attached twice");
            f.AssertSameAdapterRetried();
            Console.WriteLine("PASS: " + failure + " diagnostic once, same-view retry, WPF weak-table dedup");
        }
        return Task.CompletedTask;
    }

    private sealed class Fixture
    {
        internal string Failure;
        internal int Created, AdapterCalls;
        internal readonly List<string> Logs = new List<string>();
        private readonly object attachment;
        private readonly MethodInfo attach;
        private readonly IVsTextView view = new Mock<IVsTextView>().Object;
        private readonly Mock<ITextBuffer> source = new Mock<ITextBuffer>();
        private readonly Mock<IVsEditorAdaptersFactoryService> adapters = new Mock<IVsEditorAdaptersFactoryService>();
        private readonly Mock<IComponentModel> services = new Mock<IComponentModel>();

        internal Fixture(Type type)
        {
            var wpf = new Mock<IWpfTextView>();
            var graph = new Mock<IBufferGraph>();
            wpf.SetupGet(v => v.BufferGraph).Returns(graph.Object);
            graph.Setup(g => g.GetTextBuffers(It.IsAny<Predicate<ITextBuffer>>()))
                .Returns<Predicate<ITextBuffer>>(p => new System.Collections.ObjectModel.Collection<ITextBuffer>(
                                                     Failure == "source-not-in-view-graph" || !p(source.Object)
                                                         ? new List<ITextBuffer>()
                                                         : new List<ITextBuffer> { source.Object }));
            adapters.Setup(a => a.GetWpfTextView(It.IsAny<IVsTextView>()))
                .Returns<IVsTextView>(v =>
                                      {
                                          AdapterCalls++;
                                          if (Failure == "exception")
                                              throw new InvalidOperationException("adapter unavailable");
                                          return Failure == "wpf-null" ? null : wpf.Object;
                                      });
            var content = new Mock<IContentType>();
            content.Setup(c => c.IsOfType("C/C++")).Returns(true);
            source.SetupGet(b => b.ContentType).Returns(content.Object);
            var docs = new Mock<ITextDocumentFactoryService>();
            ITextDocument document = new Mock<ITextDocument>().Object;
            docs.Setup(d => d.TryGetTextDocument(source.Object, out document))
                .Returns(() => Failure != "no-source-document");
            var navigators = new Mock<ITextStructureNavigatorSelectorService>();
            navigators.Setup(n => n.GetTextStructureNavigator(source.Object))
                .Returns(() => Failure == "navigator-null" ? null : new Mock<ITextStructureNavigator>().Object);
            var classifiers = new Mock<IClassifierAggregatorService>();
            classifiers.Setup(c => c.GetClassifier(source.Object))
                .Returns(() => Failure == "classifier-null" ? null : new Mock<IClassifier>().Object);
            services.Setup(s => s.GetService<ITextDocumentFactoryService>())
                .Returns(() => Failure == "documents-service-missing" ? null : docs.Object);
            services.Setup(s => s.GetService<ITextStructureNavigatorSelectorService>())
                .Returns(() => Failure == "navigators-service-missing" ? null : navigators.Object);
            services.Setup(s => s.GetService<IClassifierAggregatorService>())
                .Returns(() => Failure == "classifiers-service-missing" ? null : classifiers.Object);
            Func<IWpfTextView, ITextBuffer, ITextDocumentFactoryService, ITextStructureNavigator, IClassifier,
                 IDisposable> create = (v, b, d, n, c) =>
            {
                Created++;
                return new Mock<IDisposable>().Object;
            };
            attachment =
                Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                         null, new object[] { create, (Action<string>)Logs.Add }, null);
            attach = type.GetMethod("AttachMouseNavigation", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        internal void Attach(IVsTextView adapter = null) => attach.Invoke(attachment, new object[] {
            adapter ?? view, source.Object, adapters.Object, services.Object, true
        });
        internal void AssertSameAdapterRetried() => adapters.Verify(a => a.GetWpfTextView(view), Times.Exactly(4));
    }

    private static void Assert(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
}
