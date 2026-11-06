using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Microsoft.Win32;
using Moq;

internal static class TakeoverReviewRegression
{
    internal static async Task RunAsync()
    {
        var failures = new List<string>();
        await Check("disable stops an auto-activated server even when the package field is null",
                    DisableStopsAutoActivatedServer);
        await Check("restart timeout falls back to broker load and failure is not reported as success",
                    RestartTimeoutFallsBackToLoad);
        await Check("takeover transition uses a single serializer without a duplicate gate", SingleSerializerGate);
        await Check("concurrent takeover switches resolve to the latest settings", LatestSettingsWin);
        await Check("switching covers RDT documents without views and notes reopen retry", ViewlessDocumentsCovered);
        await Check("direct LSP channel stays silent while takeover is disabled", DirectChannelSilentWhenDisabled);
        await Check("backup file lock degrades within an explicit bound", BackupLockBounded);
        await Check("missing backup warns instead of reporting success", MissingBackupWarns);
        await Check("shutdown does not wait forever for the lifecycle lock", ShutdownLockBounded);
        await Check("C++ buffers are born native so the VC provider adopts them when disabled",
                    NativeBirthForDisabledTakeover);
        await Check("disabled success message guides reopening open documents", DisabledMessageGuidesReopen);
        await Check("inert buffers are never rerouted by the takeover router", InertBuffersUntouched);
        if (failures.Count != 0)
        {
            throw new Exception(string.Join(Environment.NewLine, failures));
        }

        async Task Check(string name, Func<Task> test)
        {
            try
            {
                await test();
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception ex)
            {
                failures.Add(name + " — " + ex.GetBaseException().Message);
            }
        }
    }

    private static async Task DisableStopsAutoActivatedServer()
    {
        var assembly = typeof(ClangdVSPackage).Assembly;
        var coordinator = assembly.GetType("ClangdVS.TakeoverShutdownCoordinator");
        Assert(coordinator != null, "takeover shutdown coordinator is missing");
        var shutdownTrackedOrAuto = coordinator.GetMethod(
            "ShutdownTrackedOrAutoAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(shutdownTrackedOrAuto != null, "tracked-or-auto shutdown routing is missing");
        var clientType = typeof(ClangdLanguageClient);
        var hasActive = clientType.GetMethod("HasActiveServer",
                                             BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var shutdownIfActive = clientType.GetMethod(
            "ShutdownIfActiveAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(hasActive != null && shutdownIfActive != null, "client active-server shutdown entry is missing");

        var idle = new ClangdLanguageClient();
        var idleStopped = await(Task<bool>) shutdownIfActive.Invoke(idle, null);
        Assert(idleStopped == false, "shutdown reported stopping a server that was never started");
        Assert((bool)clientType.GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(idle) ==
                   false,
               "idle shutdown poisoned the stopping state");

        var auto = new ClangdLanguageClient();
        var process = new Process();
        try
        {
            SetField(auto, "_serverProcess", process);
            SetField(auto, "_expectingExit", false);
            Assert((bool)hasActive.Invoke(auto, null), "live server was not detected");
            await(Task) shutdownTrackedOrAuto.Invoke(
                null, new object[] { (Func<ClangdLanguageClient>)null, (Func<ClangdLanguageClient>)(() => auto) });
            Assert((bool)hasActive.Invoke(auto, null) == false, "auto-activated server survived disable");
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }

        await(Task) shutdownTrackedOrAuto.Invoke(
            null, new object[] { (Func<ClangdLanguageClient>)null, (Func<ClangdLanguageClient>)(() => null) });
    }

    private static async Task RestartTimeoutFallsBackToLoad()
    {
        var controller = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.TakeoverRuntimeController");
        var apply = controller?.GetMethod("ApplyAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(apply != null && apply.ReturnType == typeof(Task<bool>),
               "takeover lifecycle routing must report success");
        var loads = 0;
        Func<Task> restartTimeout = () => Task.FromException(
            new TimeoutException("clangd restart timed out waiting for initialization (30 seconds)."));
        Func<Task> loadOk = () =>
        {
            loads++;
            return Task.CompletedTask;
        };
        var ok = await(Task<bool>) apply.Invoke(null, new object[] { true, null, restartTimeout, loadOk });
        Assert(loads == 1, "restart TimeoutException did not fall back to broker load");
        Assert(ok, "successful fallback load was not reported as success");

        Func<Task> loadFail = () => Task.FromException(new InvalidOperationException("broker load failed"));
        ok = await(Task<bool>) apply.Invoke(null, new object[] { true, null, restartTimeout, loadFail });
        Assert(ok == false, "failed takeover was reported as success");

        ok = await(Task<bool>) apply.Invoke(null, new object[] { false, null, null, null });
        Assert(ok, "disable with no running server must succeed");
    }

    private static Task SingleSerializerGate()
    {
        var gate = typeof(ClangdVSPackage).GetField("_takeoverGate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(gate == null, "duplicate takeover gate _takeoverGate still exists alongside the serializer");
        var serializer = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.TakeoverTransitionSerializer");
        Assert(serializer != null, "takeover transition serializer is missing");
        return Task.CompletedTask;
    }

    private static Task LatestSettingsWin()
    {
        var assembly = typeof(ClangdVSPackage).Assembly;
        var policy = assembly.GetType("ClangdVS.TakeoverApplyCoordinator");
        Assert(policy != null, "takeover apply coordinator is missing");
        var shouldSkip =
            policy.GetMethod("ShouldSkip", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(shouldSkip != null, "stale snapshot detection is missing");
        Assert((bool)shouldSkip.Invoke(null, new object[] { 1L, 2L }), "stale snapshot was not superseded");
        Assert(!(bool)shouldSkip.Invoke(null, new object[] { 2L, 2L }), "latest snapshot was incorrectly skipped");
        var sequence =
            typeof(ClangdVSPackage).GetField("_takeoverSequence", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(sequence != null && sequence.FieldType == typeof(long), "takeover request sequencing is missing");
        var apply = typeof(ClangdVSPackage)
                        .GetMethod("ApplyTakeoverSettingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(apply != null, "sequenced takeover apply is missing");
        var parameters = apply.GetParameters();
        Assert(parameters.Length == 2 && parameters[0].ParameterType == typeof(bool) &&
                   parameters[1].ParameterType == typeof(long),
               "takeover apply does not carry the request sequence");
        return Task.CompletedTask;
    }

    private static async Task ViewlessDocumentsCovered()
    {
        var packageType = typeof(ClangdVSPackage);
        var enumerate = packageType.GetMethod("EnumerateRunningTextBuffers",
                                              BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(enumerate != null, "viewless RDT buffer enumeration is missing");
        var rdt = new Mock<IVsRunningDocumentTable>();
        var docs = new Mock<IEnumRunningDocuments>();
        uint docsFetched = 1;
        var docStep = 0;
        docs.Setup(x => x.Next(1, It.IsAny<uint[]>(), out docsFetched))
            .Callback(new NextDoc((uint count, uint[] cookies, out uint fetched) =>
                                  {
                                      cookies[0] = ++docStep <= 3 ? (uint)docStep : 0;
                                      fetched = docStep <= 3 ? 1u : 0u;
                                  }))
            .Returns(() => docStep <= 3 ? 0 : 1);
        IEnumRunningDocuments docEnum = docs.Object;
        rdt.Setup(x => x.GetRunningDocumentsEnum(out docEnum)).Returns(0);
        var direct = new Mock<IVsTextLines>();
        var provided = new Mock<IVsTextLines>();
        var bridged = new Mock<IVsTextBufferProvider>();
        IVsTextLines providedLines = provided.Object;
        bridged.Setup(x => x.GetTextBuffer(out providedLines)).Returns(0);
        uint flags = 0, read = 0, edit = 0, item = 0;
        string path = "viewless.cpp";
        IVsHierarchy hierarchy = null;
        var directPointer = Marshal.GetIUnknownForObject(direct.Object);
        var bridgedPointer = Marshal.GetIUnknownForObject(bridged.Object);
        try
        {
            SetupDoc(rdt, 1, directPointer);
            rdt.Setup(x => x.GetDocumentInfo(2, out flags, out read, out edit, out path, out hierarchy, out item,
                                             out It.Ref<IntPtr>.IsAny))
                .Callback(new DocInfo((uint cookie, out uint f, out uint r, out uint e, out string p,
                                       out IVsHierarchy h, out uint i, out IntPtr data) =>
                                      {
                                          f = r = e = i = 0;
                                          p = path;
                                          h = null;
                                          Marshal.AddRef(bridgedPointer);
                                          data = bridgedPointer;
                                      }))
                .Returns(0);
            rdt.Setup(x => x.GetDocumentInfo(3, out flags, out read, out edit, out path, out hierarchy, out item,
                                             out It.Ref<IntPtr>.IsAny))
                .Callback(new DocInfo((uint cookie, out uint f, out uint r, out uint e, out string p,
                                       out IVsHierarchy h, out uint i, out IntPtr data) =>
                                      {
                                          f = r = e = i = 0;
                                          p = "nontext.bin";
                                          h = null;
                                          data = IntPtr.Zero;
                                      }))
                .Returns(0);
            var buffers = ((System.Collections.Generic.IEnumerable<IVsTextBuffer>)enumerate.Invoke(null,
                                                                                                   new object[] {
                                                                                                       rdt.Object,
                                                                                                   }))
                              .ToArray();
            Assert(buffers.Length == 2, "viewless or provider-backed RDT buffer lost (got " + buffers.Length + ")");
        }
        finally
        {
            Marshal.Release(directPointer);
            Marshal.Release(bridgedPointer);
        }

        var router = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.TakeoverContentTypeRouter");
        var switchBuffers =
            router?.GetMethod("SwitchBuffers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(switchBuffers != null, "bulk content-type switch is missing");
        var content = new Mock<IContentType>();
        content.SetupGet(c => c.TypeName).Returns("cpp-clangd");
        content.Setup(c => c.IsOfType("cpp-clangd")).Returns(true);
        content.Setup(c => c.IsOfType("C/C++")).Returns(true);
        var buffer = new Mock<ITextBuffer>(MockBehavior.Strict);
        buffer.SetupGet(b => b.ContentType).Returns(content.Object);
        var registry = new Mock<IContentTypeRegistryService>(MockBehavior.Strict);
        registry.Setup(r => r.GetContentType(It.IsAny<string>())).Returns((IContentType)null);
        var logs = new List<string>();
        var switched = (int)switchBuffers.Invoke(null, new object[] {
            new[] { buffer.Object },
            registry.Object,
            false,
            (Action<string>)logs.Add,
        });
        Assert(switched == 0 && logs.Any(message => message.Contains("reopen")),
               "missing content type has no reopen retry note");
        await Task.CompletedTask;
    }

    private static async Task DirectChannelSilentWhenDisabled()
    {
        var settings = ClangdSettings.Instance;
        var original = settings.TakeOverFromNativeIntelliSense;
        var logDirectory = Environment.GetEnvironmentVariable("CLANGDVS_LOG_DIRECTORY");
        try
        {
            settings.TakeOverFromNativeIntelliSense = false;
            var client = new ClangdLanguageClient();
            var before = ReadLog(logDirectory);
            var switched = await client.SwitchSourceHeaderAsync(@"C:\x\a.cpp", "int x;");
            var definition = await client.GetDefinitionAsync("file:///C:/x/a.cpp", 0, 0);
            var send = typeof(ClangdLanguageClient)
                           .GetMethod("SendDidOpenAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(send != null, "direct didOpen sender is missing");
            var didOpen =
                await(Task<string>) send.Invoke(client, new object[] { @"C:\x\a.cpp", "int x;", "review-probe" });
            Assert(switched == null && definition == null && didOpen == null,
                   "direct channel emitted while takeover was disabled");
            var after = ReadLog(logDirectory);
            Assert(after.Length > before.Length && after.Contains("takeover"),
                   "disabled-takeover skip left no takeover log");

            settings.TakeOverFromNativeIntelliSense = true;
            Assert(await client.SwitchSourceHeaderAsync(@"C:\x\a.cpp", "int x;") == null &&
                       await client.GetDefinitionAsync("file:///C:/x/a.cpp", 0, 0) == null,
                   "enabled direct channel changed without a server");
        }
        finally
        {
            settings.TakeOverFromNativeIntelliSense = original;
        }
    }

    private static async Task BackupLockBounded()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.IntelliSenseSuppression");
        var timeout = type?.GetField("BackupMutexTimeoutMilliseconds",
                                     BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(timeout != null, "explicit backup lock timeout is missing");
        Assert((int)timeout.GetValue(null) <= 1000, "backup lock timeout still blocks UI for seconds");
        var root = @"Software\ClangdVS-RegressionHarness\" + Guid.NewGuid().ToString("N");
        using (var holder = new Mutex(false, @"Local\ClangdVS-Takeover-Backup"))
        {
            var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHolder = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holderTask = Task.Run(() =>
                                      {
                                          holder.WaitOne();
                                          acquired.SetResult(true);
                                          releaseHolder.Task.GetAwaiter().GetResult();
                                          holder.ReleaseMutex();
                                      });
            Assert(await Task.WhenAny(acquired.Task, Task.Delay(5000)) == acquired.Task,
                   "test backup mutex was not acquired");
            var watch = Stopwatch.StartNew();
            type.GetMethod("SetSuppressed", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { true, null, root });
            watch.Stop();
            releaseHolder.SetResult(true);
            await holderTask;
            Assert(watch.Elapsed < TimeSpan.FromSeconds(8),
                   "backup lock blocked the caller far beyond the explicit bound");
        }

        try
        {
            type.GetMethod("SetSuppressed", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { false, null, root });
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
        }
    }

    private static Task MissingBackupWarns()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.IntelliSenseSuppression");
        var tryRevert =
            type?.GetMethod("TryRevert", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(tryRevert != null, "status-aware revert is missing");
        var restore = type.GetMethod("RestoreProperty", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(restore != null, "backup-aware restore policy is missing");
        var property = new Mock<EnvDTE.Property>(MockBehavior.Strict);
        property.SetupGet(p => p.Name).Returns("DisableIntelliSense");
        property.SetupGet(p => p.Value).Returns(false);
        var restored = restore.Invoke(null, new object[] { null, "dte:DisableIntelliSense", property.Object });
        Assert(restored is bool && (bool)restored == false, "restore did not report a backup-missing skip");

        var root = @"Software\ClangdVS-RegressionHarness\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(root + @"\Text Editor\C/C++-Specific"))
                key.SetValue("DisableIntelliSense", 1, RegistryValueKind.DWord);
            var dirty = (bool)tryRevert.Invoke(null, new object[] { null, root });
            Assert(dirty == false, "missing backup with suppression residue reported success");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
        }

        var cleanRoot = @"Software\ClangdVS-RegressionHarness\" + Guid.NewGuid().ToString("N");
        try
        {
            var clean = (bool)tryRevert.Invoke(null, new object[] { null, cleanRoot });
            Assert(clean, "clean revert without backup should succeed");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(cleanRoot, false);
        }

        return Task.CompletedTask;
    }

    private static async Task ShutdownLockBounded()
    {
        var field =
            typeof(ClangdLanguageClient)
                .GetField("ShutdownLockTimeout", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(field != null, "bounded shutdown lock wait is missing");
        var previous = (TimeSpan)field.GetValue(null);
        try
        {
            field.SetValue(null, TimeSpan.FromMilliseconds(150));
            var client = new ClangdLanguageClient();
            var gate = (SemaphoreSlim) typeof(ClangdLanguageClient)
                           .GetField("_lifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic)
                           .GetValue(client);
            await gate.WaitAsync();
            try
            {
                var shutdown = client.ShutdownAsync();
                var finished = await Task.WhenAny(shutdown, Task.Delay(4000));
                Assert(ReferenceEquals(finished, shutdown), "shutdown waited past its explicit bound");
                try
                {
                    await shutdown;
                    Assert(false, "contended shutdown should time out");
                }
                catch (TimeoutException ex)
                {
                    Assert(ex.Message.Contains("Shutdown") || ex.Message.Contains("shutdown") ||
                               ex.Message.Contains("lifecycle"),
                           "shutdown timeout did not explain the lifecycle lock");
                }
            }
            finally
            {
                gate.Release();
            }

            await client.ShutdownAsync();
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }

    private static Task NativeBirthForDisabledTakeover()
    {
        var bridge = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdContentTypeBridge");
        Assert(bridge != null, "content-type bridge is missing");
        var extensionMapped =
            bridge.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(field => field.GetCustomAttributes(typeof(FileExtensionAttribute), false).Length != 0)
                .Select(field => field.Name)
                .ToArray();
        Assert(extensionMapped.Length == 0, "static file-extension mapping still hijacks C++ buffers at birth: " +
                                                string.Join(",", extensionMapped));
        var definition = bridge.GetField("CppClangdContentTypeDefinition",
                                         BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(definition != null, "bridged cpp-clangd content-type definition is missing");
        var bases = definition.GetCustomAttributes(typeof(BaseDefinitionAttribute), false).Length;
        Assert(bases == 2, "bridged content type lost a base definition");
        return Task.CompletedTask;
    }

    private static Task DisabledMessageGuidesReopen()
    {
        var messages = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.TakeoverStatusMessages");
        Assert(messages != null, "takeover status messages are missing");
        var disabled = messages.GetMethod("GetDisabledMessage",
                                          BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(disabled != null, "disabled takeover messaging is missing");
        var restored = (string)disabled.Invoke(null, new object[] { true, true });
        Assert(restored.Contains("重新打开"), "disabled success hides the reopen step for open documents: " + restored);
        var incomplete = (string)disabled.Invoke(null, new object[] { false, true });
        Assert(incomplete.Contains("备份缺失"), "missing backup no longer warns: " + incomplete);
        var failed = (string)disabled.Invoke(null, new object[] { true, false });
        Assert(failed.Contains("失败"), "failed restore reported success: " + failed);
        return Task.CompletedTask;
    }

    private static Task InertBuffersUntouched()
    {
        var router = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.TakeoverContentTypeRouter");
        Assert(router != null, "takeover content-type router is missing");
        var getTarget = router.GetMethod("GetTargetContentTypeName",
                                         BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(getTarget != null, "content-type target policy is missing");
        foreach (var typeName in new[] { "inert", "plaintext" })
        {
            var content = new Mock<IContentType>();
            content.SetupGet(c => c.TypeName).Returns(typeName);
            content.Setup(c => c.IsOfType("cpp-clangd")).Returns(false);
            content.Setup(c => c.IsOfType("C/C++")).Returns(false);
            foreach (var enabled in new[] { false, true })
            {
                var target = (string)getTarget.Invoke(null, new object[] { enabled, content.Object });
                Assert(target == null, $"takeover router rerouted {typeName} (enabled={enabled}) to {target}");
            }
        }

        return Task.CompletedTask;
    }

    private static void SetupDoc(Mock<IVsRunningDocumentTable> rdt, uint cookie, IntPtr pointer)
    {
        uint flags = 0, read = 0, edit = 0, item = 0;
        string path = "viewless.cpp";
        IVsHierarchy hierarchy = null;
        rdt.Setup(x => x.GetDocumentInfo(cookie, out flags, out read, out edit, out path, out hierarchy, out item,
                                         out It.Ref<IntPtr>.IsAny))
            .Callback(new DocInfo((uint c, out uint f, out uint r, out uint e, out string p, out IVsHierarchy h,
                                   out uint i, out IntPtr data) =>
                                  {
                                      f = r = e = i = 0;
                                      p = path;
                                      h = null;
                                      Marshal.AddRef(pointer);
                                      data = pointer;
                                  }))
            .Returns(0);
    }

    private static string ReadLog(string directory)
    {
        try
        {
            var file = Path.Combine(directory, "clangd.log");
            return File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SetField(ClangdLanguageClient client, string name,
                                 object value) => typeof(ClangdLanguageClient)
                                                      .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                                                      .SetValue(client, value);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private delegate void NextDoc(uint count, uint[] cookies, out uint fetched);

    private delegate void DocInfo(uint cookie, out uint flags, out uint read, out uint edit, out string path,
                                  out IVsHierarchy hierarchy, out uint item, out IntPtr data);
}
