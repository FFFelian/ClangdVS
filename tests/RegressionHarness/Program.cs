using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using System.ComponentModel.Composition.Hosting;
using System.Runtime.InteropServices;
using Moq;
using ClangdVS;
using Microsoft.VisualStudio.OLE.Interop;
using System.Runtime.Serialization;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            if (Environment.GetEnvironmentVariable("CLANGDVS_VERSION_TEST_MODE") == "hang")
            {
                File.WriteAllText(Environment.GetEnvironmentVariable("CLANGDVS_VERSION_TEST_PID_FILE"),
                                  Process.GetCurrentProcess().Id.ToString());
                Console.Out.Write("ready");
                await Task.Delay(TimeSpan.FromSeconds(12));
                return 0;
            }
            if (Environment.GetEnvironmentVariable("CLANGDVS_VERSION_TEST_MODE") == "flood")
            {
                File.WriteAllText(Environment.GetEnvironmentVariable("CLANGDVS_VERSION_TEST_PID_FILE"),
                                  Process.GetCurrentProcess().Id.ToString());
                Console.Out.Write(new string('o', 2000000));
                Console.Error.Write(new string('e', 2000000));
                return 0;
            }
        }
        var testLogDirectory =
            Path.Combine(Path.GetTempPath(), "ClangdVS-RegressionHarness", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLANGDVS_LOG_DIRECTORY", testLogDirectory);
        Console.WriteLine("Harness log directory: " + testLogDirectory);
        var logType = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdLog");
        var actualLogDirectory =
            (string)logType.GetProperty("LogDirectory", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        if (actualLogDirectory != testLogDirectory)
        {
            Console.WriteLine("FAIL: harness logger ignored isolated log directory");
            return 1;
        }
        if (args.Contains("--log-only"))
        {
            logType.GetMethod("WriteLine").Invoke(null, new object[] { "harness-isolation-check" });
            var log = File.ReadAllText(Path.Combine(testLogDirectory, "clangd.log"));
            if (!log.Contains("harness-isolation-check") || !log.Contains("pid="))
                return 1;
            Console.WriteLine("PASS: harness logs isolated with process identity");
            return 0;
        }
        if (args.Length == 2 && args[0] == "cache")
        {
            using (var stream = File.OpenRead(args[1]))
            {
                var composition =
                    await new CachedComposition().LoadRuntimeCompositionAsync(stream, Resolver.DefaultInstance);
                foreach (var part in composition.Parts)
                {
                    foreach (var export in part.Exports.Where(e => e.ContractName.Contains("ILanguageClient") ||
                                                                   e.DeclaringTypeRef.FullName.Contains("Clangd")))
                    {
                        Console.WriteLine(
                            $"{export.DeclaringTypeRef.FullName} | {export.ContractName} | {export.ExportedValueTypeRef.FullName} | {export.ExportedValueTypeRef.AssemblyName} | token={export.ExportedValueTypeRef.MetadataToken:X8}");
                        foreach (var metadata in export.Metadata)
                            Console.WriteLine($"  {metadata.Key}: {metadata.Value}");
                        if (export.DeclaringTypeRef.FullName.Contains("ClangdLanguageClient"))
                            foreach (var baseType in export.ExportedValueTypeRef.BaseTypes)
                                Console.WriteLine(
                                    $"  base: {baseType.FullName} | {baseType.AssemblyName} | token={baseType.MetadataToken:X8}");
                    }
                }
            }
            return 0;
        }
        if (args.Length == 2 && args[0] == "stderr-flood")
        {
            var payload = new string('x', int.Parse(args[1]));
            Console.Error.Write(payload);
            return 0;
        }
        var failures = 0;
        await Check("initialize capability injection preserves LSP framing", LspInitializeRegression.RunAsync);
        await Check("inlay hints negotiate editor space and retire stale requests", InlayHintRegression.RunAsync);
        await Check("inspection window selects results and renders hierarchy roots", InspectionWindowRegression.RunAsync);
        await Check("feature settings merge and serialize lifecycle changes", FeatureSettingsApplyRegression.RunAsync);
        await Check("clangd feature switches expose compatible defaults", () =>
        {
            ClangdFeatureSettingsRegression.Run();
            return Task.CompletedTask;
        });
        await Check("clangd LSP feature gate intercepts only configured methods",
                    ClangdFeatureMiddleLayerRegression.RunAsync);
        await Check("clangd framed LSP stream filters only disabled server notifications",
                    () =>
                    {
                        ClangdLspMessageFilterRegression.Run();
                        return Task.CompletedTask;
                    });
        await Check("inactive-region updates replace old ranges, including with an empty set",
                    () =>
                    {
                        InactiveRegionRegression.Run();
                        return Task.CompletedTask;
                    });
        await Check("clangd inspection requests honor their default-disabled settings",
                    () =>
                    {
                        ClangdExtensionRequestsRegression.Run();
                        return Task.CompletedTask;
                    });
        if (args.Contains("--features-only"))
            return failures == 0 ? 0 : 1;
        await Check("real clangd extension protocol and inactive regions", ClangdRealProtocolRegression.RunAsync);
        if (args.Contains("--real-clangd-only"))
            return failures == 0 ? 0 : 1;
        await Check("stderr is drained even when stderr logging is disabled", ProcessStreamRegression.RunAsync);
        if (args.Contains("--process-stream-only"))
            return failures == 0 ? 0 : 1;
        await Check("takeover restore leaves unmodified settings untouched", IntelliSenseRestoreRegression.RunAsync);
        if (args.Contains("--takeover-restore-only"))
            return failures == 0 ? 0 : 1;
        await Check("takeover state gates broker loading and language-client activation",
                    TakeoverRuntimeRegression.RunAsync);
        if (args.Contains("--takeover-runtime-only"))
            return failures == 0 ? 0 : 1;
        await Check("takeover transitions retire only the old provider diagnostics",
                    TakeoverDiagnosticsRegression.RunAsync);
        if (args.Contains("--takeover-diagnostics-only"))
            return failures == 0 ? 0 : 1;
        await Check("takeover review fixes (shutdown/timeout/serialization/concurrency/routing/gates/backup/lock)",
                    TakeoverReviewRegression.RunAsync);
        if (args.Contains("--takeover-review-only"))
            return failures == 0 ? 0 : 1;
        await Check("cold-start binding repair targets only this active unopened host client",
                    HostDocumentBindingRegression.RunAsync);
        await Check("cold-start hover diagnostics observe host caches without changing routing",
                    HoverDiagnosticsRegression.RunAsync);
        if (args.Contains("--hover-diag-only"))
            return failures == 0 ? 0 : 1;
        await Check("independent mouse attachment retries and diagnoses SDK failures",
                    MouseAttachmentRegression.RunAsync);
        if (args.Contains("--attach-only"))
            return failures == 0 ? 0 : 1;
        await Check("mouse navigation uses hit position and rejects stale or unrelated gestures",
                    MouseNavigationRegression.RunAsync);
        if (args.Contains("--mouse-only"))
            return failures == 0 ? 0 : 1;
        await Check(
            "navigation target matches the SDK GoToDefinition command",
            async () =>
            {
                var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdNavigationTarget");
                var group =
                    (Guid)type.GetField("CommandSet", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                var command = (uint)type.GetField("CmdGotoDefn", BindingFlags.Static | BindingFlags.NonPublic)
                                  .GetRawConstantValue();
                Assert(group == Microsoft.VisualStudio.VSConstants.GUID_VSStandardCommandSet97,
                       "navigation command group differs from the SDK standard command set");
                Assert(command == (uint)Microsoft.VisualStudio.VSConstants.VSStd97CmdID.GotoDefn,
                       "navigation command ID differs from SDK GoToDefinition");
                await Task.CompletedTask;
            });
        await Check("MEF interface and concrete contracts share the same client",
                    async () =>
                    {
                        using (var catalog = new TypeCatalog(typeof(ClangdLanguageClient))) using (
                            var container = new CompositionContainer(catalog))
                        {
                            var client = container.GetExportedValue<ClangdLanguageClient>();
                            Assert(ReferenceEquals(client, container.GetExportedValue<ILanguageClient>()),
                                   "MEF created a second client");
                            await Task.CompletedTask;
                        }
                    });
        await Check("broker load preserves JTF dependencies, retries and concurrent state",
                    GateThreadingRegression.RunAsync);
        await Check(
            "mixed command status preserves downstream commands",
            async () =>
            {
                var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdNavigationTarget");
                var method = type.GetMethod("QueryNavigationStatus", BindingFlags.Static | BindingFlags.NonPublic);
                Assert(method != null, "batch-aware command status handler missing");
                var group = Microsoft.VisualStudio.VSConstants.GUID_VSStandardCommandSet97;
                var commands =
                    new[] { new OLECMD { cmdID = (uint)Microsoft.VisualStudio.VSConstants.VSStd97CmdID.GotoDefn },
                            new OLECMD { cmdID = (uint)Microsoft.VisualStudio.VSConstants.VSStd97CmdID.Copy } };
                var next = new Mock<IOleCommandTarget>(MockBehavior.Strict);
                next.Setup(x => x.QueryStatus(ref group, 2, commands, IntPtr.Zero))
                    .Callback(() =>
                              {
                                  commands[0].cmdf = 1;
                                  commands[1].cmdf = 3;
                              })
                    .Returns(0);
                Func<bool> enabled = () => true;
                var argsForQuery = new object[] { group, 2u, commands, IntPtr.Zero, enabled, next.Object };
                Assert((int)method.Invoke(null, argsForQuery) == 0, "mixed view query did not succeed");
                Assert(commands[0].cmdf == 3 && commands[1].cmdf == 3,
                       "navigation override lost downstream Copy status");
                next.VerifyAll();
                commands[0].cmdf = commands[1].cmdf = 0;
                argsForQuery[5] = null;
                Assert((int)method.Invoke(null, argsForQuery) == unchecked((int)0x80040104),
                       "priority target swallowed a partial batch");
                Assert(commands.All(c => c.cmdf == 0), "priority target altered a batch it cannot complete");
                await Task.CompletedTask;
            });
        await Check(
            "RDT scan enumerates only valid buffers and includes all split views",
            async () =>
            {
                var method = typeof(ClangdVSPackage)
                                 .GetMethod("EnumerateDocumentViews", BindingFlags.Static | BindingFlags.NonPublic);
                Assert(method != null, "buffer-based enumeration missing");
                var rdt = new Mock<IVsRunningDocumentTable>();
                var docs = new Mock<IEnumRunningDocuments>();
                uint docsFetched = 1;
                var docStep = 0;
                docs.Setup(x => x.Next(1, It.IsAny<uint[]>(), out docsFetched))
                    .Callback(new NextDocument((uint count, uint[] cookies, out uint fetched) =>
                                               {
                                                   cookies[0] = ++docStep <= 2 ? (uint)docStep : 0;
                                                   fetched = docStep <= 2 ? 1u : 0u;
                                               }))
                    .Returns(() => docStep <= 2 ? 0 : 1);
                IEnumRunningDocuments docEnum = docs.Object;
                rdt.Setup(x => x.GetRunningDocumentsEnum(out docEnum)).Returns(0);
                var buffer = new Mock<IVsTextLines>();
                var provider = new Mock<IVsTextBufferProvider>();
                IVsTextLines providedLines = buffer.Object;
                provider.Setup(x => x.GetTextBuffer(out providedLines)).Returns(0);
                uint flags = 0, read = 0, edit = 0, item = 0;
                string path = "restored.cpp";
                IVsHierarchy hierarchy = null;
                var pointer = Marshal.GetIUnknownForObject(buffer.Object);
                var providerPointer = Marshal.GetIUnknownForObject(provider.Object);
                try
                {
                    rdt.Setup(x => x.GetDocumentInfo(1, out flags, out read, out edit, out path, out hierarchy,
                                                     out item, out It.Ref<IntPtr>.IsAny))
                        .Callback(new DocumentInfo((uint cookie, out uint f, out uint r, out uint e, out string p,
                                                    out IVsHierarchy h, out uint i, out IntPtr data) =>
                                                   {
                                                       f = r = e = i = 0;
                                                       p = path;
                                                       h = null;
                                                       Marshal.AddRef(pointer);
                                                       data = pointer;
                                                   }))
                        .Returns(0);
                    rdt.Setup(x => x.GetDocumentInfo(2, out flags, out read, out edit, out path, out hierarchy,
                                                     out item, out It.Ref<IntPtr>.IsAny))
                        .Callback(new DocumentInfo((uint cookie, out uint f, out uint r, out uint e, out string p,
                                                    out IVsHierarchy h, out uint i, out IntPtr data) =>
                                                   {
                                                       f = r = e = i = 0;
                                                       p = "nontext.bin";
                                                       h = null;
                                                       data = IntPtr.Zero;
                                                   }))
                        .Returns(0);
                    var manager = new Mock<IVsTextManager>(MockBehavior.Strict);
                    var views = new Mock<IVsEnumTextViews>();
                    var view1 = new Mock<IVsTextView>().Object;
                    var view2 = new Mock<IVsTextView>().Object;
                    var viewStep = 0;
                    views.Setup(x => x.Next(1, It.IsAny<IVsTextView[]>(), ref It.Ref<uint>.IsAny))
                        .Callback(new NextView((uint count, IVsTextView[] result, ref uint fetched) =>
                                               {
                                                   result[0] = viewStep++ == 0 ? view1 : view2;
                                                   fetched = viewStep <= 2 ? 1u : 0u;
                                               }))
                        .Returns(() => viewStep <= 2 ? 0 : 1);
                    IVsEnumTextViews viewEnum = views.Object;
                    manager.Setup(x => x.EnumViews((IVsTextBuffer)buffer.Object, out viewEnum)).Returns(0);
                    var result = ((System.Collections.Generic.IEnumerable<IVsTextView>)method.Invoke(
                                      null, new object[] { rdt.Object, manager.Object }))
                                     .ToArray();
                    Assert(result.SequenceEqual(new[] { view1, view2 }), "restored/split views lost");
                    manager.Verify(x => x.EnumViews(It.IsAny<IVsTextBuffer>(), out viewEnum), Times.Once);

                    // The next scan sees provider-backed doc data and the same buffer
                    // under another cookie. Dedup must preserve both split views.
                    rdt.Setup(x => x.GetDocumentInfo(2, out flags, out read, out edit, out path, out hierarchy,
                                                     out item, out It.Ref<IntPtr>.IsAny))
                        .Callback(new DocumentInfo((uint cookie, out uint f, out uint r, out uint e, out string p,
                                                    out IVsHierarchy h, out uint i, out IntPtr data) =>
                                                   {
                                                       f = r = e = i = 0;
                                                       p = path;
                                                       h = null;
                                                       Marshal.AddRef(providerPointer);
                                                       data = providerPointer;
                                                   }))
                        .Returns(0);
                    docStep = viewStep = 0;
                    result = ((System.Collections.Generic.IEnumerable<IVsTextView>)method.Invoke(
                                  null, new object[] { rdt.Object, manager.Object }))
                                 .ToArray();
                    Assert(result.SequenceEqual(new[] { view1, view2 }), "provider-backed buffer or dedup lost views");
                    manager.Verify(x => x.EnumViews(It.IsAny<IVsTextBuffer>(), out viewEnum), Times.Exactly(2));
                    provider.Verify(x => x.GetTextBuffer(out providedLines), Times.Once);
                }
                finally
                {
                    Marshal.Release(pointer);
                    Marshal.Release(providerPointer);
                }
                await Task.CompletedTask;
            });
        await Check(
            "unrelated commands forward unchanged to the next view target",
            async () =>
            {
                var package = (ClangdVSPackage)FormatterServices.GetUninitializedObject(typeof(ClangdVSPackage));
                var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdViewCommandFilter");
                var filter = (IOleCommandTarget)Activator.CreateInstance(type, new object[] { package });
                var next = new Mock<IOleCommandTarget>(MockBehavior.Strict);
                var group = Guid.NewGuid();
                var commands = new[] { new OLECMD { cmdID = 42, cmdf = 7 } };
                next.Setup(x => x.QueryStatus(ref group, 1, commands, new IntPtr(3))).Returns(123);
                next.Setup(x => x.Exec(ref group, 42, 8, new IntPtr(4), new IntPtr(5))).Returns(456);
                type.GetMethod("SetNextTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(filter, new object[] { next.Object });
                Assert(filter.QueryStatus(ref group, 1, commands, new IntPtr(3)) == 123 && commands[0].cmdf == 7,
                       "QueryStatus forwarding changed");
                Assert(filter.Exec(ref group, 42, 8, new IntPtr(4), new IntPtr(5)) == 456, "Exec forwarding changed");
                next.VerifyAll();
                await Task.CompletedTask;
            });
        await Check("restart requires this initialization and propagates failure", RestartRegression.RunAsync);
        return failures == 0 ? 0 : 1;

        async Task Check(string name, Func<Task> test)
        {
            try
            {
                await test();
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL: " + name + " — " + ex);
            }
        }
    }

    private delegate void NextDocument(uint count, uint[] cookies, out uint fetched);
    private delegate void NextView(uint count, IVsTextView[] views, ref uint fetched);
    private delegate void DocumentInfo(uint cookie, out uint flags, out uint read, out uint edit, out string path,
                                       out IVsHierarchy hierarchy, out uint item, out IntPtr data);
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
