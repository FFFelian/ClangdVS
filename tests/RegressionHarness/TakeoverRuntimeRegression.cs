using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Moq;

internal static class TakeoverRuntimeRegression
{
    internal static async Task RunAsync()
    {
        var packageType = typeof(ClangdVSPackage);
        var shouldLoad = packageType.GetMethod("ShouldLoadClangd", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(shouldLoad != null, "clangd load policy is missing");
        Assert((bool)shouldLoad.Invoke(null, new object[] { false }) == false,
               "disabled takeover must block automatic and manual clangd loads");
        Assert((bool)shouldLoad.Invoke(null, new object[] { true }), "enabled takeover must allow clangd loads");

        var commandGate = packageType.GetMethod("IsClangdCommandEnabled", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(commandGate != null, "clangd command gate is missing");
        foreach (var commandId in new[] { 0x1100, 0x1101, 0x1103 })
            Assert(!(bool)commandGate.Invoke(null, new object[] { false, commandId }),
                   $"clangd command 0x{commandId:X4} remained enabled with takeover disabled");
        Assert((bool)commandGate.Invoke(null, new object[] { false, 0x1102 }),
               "takeover toggle command must remain available while takeover is disabled");
        var contentTypeAttribute = (Microsoft.VisualStudio.Utilities.ContentTypeAttribute)Attribute.GetCustomAttribute(
            typeof(ClangdLanguageClient), typeof(Microsoft.VisualStudio.Utilities.ContentTypeAttribute));
        Assert(contentTypeAttribute?.ContentTypes == "cpp-clangd",
               "clangd client must not automatically match native C/C++ buffers");

        var controller = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.TakeoverRuntimeController");
        var apply = controller?.GetMethod("ApplyAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(apply != null, "takeover lifecycle routing is missing");
        var shutdowns = 0;
        var restarts = 0;
        var loads = 0;
        Func<Task> shutdown = () =>
        {
            shutdowns++;
            return Task.CompletedTask;
        };
        Func<Task> restart = () =>
        {
            restarts++;
            return Task.CompletedTask;
        };
        Func<Task> load = () =>
        {
            loads++;
            return Task.CompletedTask;
        };
        await(Task) apply.Invoke(null, new object[] { false, shutdown, restart, load });
        Assert(shutdowns == 1 && restarts == 0 && loads == 0,
               "disabling takeover must stop clangd without restarting or loading it");
        await(Task) apply.Invoke(null, new object[] { true, shutdown, restart, load });
        Assert(shutdowns == 1 && restarts == 1 && loads == 0,
               "enabling takeover must restart an already-loaded clangd client");
        var uninitializedRestart = new Func<Task>(() =>
                                                  {
                                                      restarts++;
                                                      throw new InvalidOperationException("host has not subscribed");
                                                  });
        await(Task) apply.Invoke(null, new object[] { true, shutdown, uninitializedRestart, load });
        Assert(restarts == 2 && loads == 1,
               "enabling takeover must broker-load when the existing client cannot restart yet");
        await(Task) apply.Invoke(null, new object[] { true, null, null, load });
        Assert(loads == 2, "enabling takeover must load a client that has not yet been created");

        var settings = ClangdSettings.Instance;
        var original = settings.TakeOverFromNativeIntelliSense;
        try
        {
            settings.TakeOverFromNativeIntelliSense = false;
            var client = new ClangdLanguageClient();
            var connection = await client.ActivateAsync(CancellationToken.None);
            Assert(connection == null, "client activation started while takeover was disabled");
            await client.OnLoadedAsync();
            var loaded =
                typeof(ClangdLanguageClient).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert((int)loaded.GetValue(client) == 0,
                   "disabled broker loading must remain retryable when takeover is enabled later");
        }
        finally
        {
            settings.TakeOverFromNativeIntelliSense = original;
        }

        await TakeoverContentTypeRoutingAsync();
        await TakeoverTransitionSerializationAsync();
        await TakeoverLifecycleGatingAsync();
    }

    private static async Task TakeoverContentTypeRoutingAsync()
    {
        var assembly = typeof(ClangdVSPackage).Assembly;
        var router = assembly.GetType("ClangdVS.TakeoverContentTypeRouter");
        Assert(router != null, "takeover content-type router is missing");
        var clangdName =
            (string)router
                .GetField("ClangdContentTypeName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(null);
        var nativeName =
            (string)router
                .GetField("NativeContentTypeName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(null);
        Assert(clangdName == "cpp-clangd", "bridged content-type name differs from cpp-clangd");
        Assert(nativeName == "C/C++", "native content-type name differs from C/C++");
        var getTarget = router.GetMethod("GetTargetContentTypeName",
                                         BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(getTarget != null, "content-type target policy is missing");
        Func<bool, Mock<IContentType>, string> target = (enabled, content) =>
            (string)getTarget.Invoke(null, new object[] { enabled, content?.Object });

        var bridged = ContentType("cpp-clangd", isCppClangd: true, isNative: true);
        var native = ContentType("C/C++", isCppClangd: false, isNative: true);
        var inert = ContentType("inert", isCppClangd: false, isNative: false);
        Assert(target(false, bridged) == "C/C++", "opened cpp-clangd buffer was not routed back to C/C++");
        Assert(target(false, native) == null, "native buffer was incorrectly rerouted while disabled");
        Assert(target(false, inert) == null, "unrelated buffer was incorrectly rerouted while disabled");
        Assert(target(false, null) == null, "missing content type was not ignored");
        Assert(target(true, native) == "cpp-clangd", "opened C/C++ buffer was not routed to cpp-clangd");
        Assert(target(true, bridged) == null, "bridged buffer was incorrectly rerouted while enabled");

        var switchBuffers =
            router.GetMethod("SwitchBuffers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(switchBuffers != null, "bulk content-type switch is missing");
        var ensureRouted =
            router.GetMethod("EnsureBufferRouted", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(ensureRouted != null, "new-buffer content-type routing is missing");

        var nativeContent = ContentType("C/C++", isCppClangd: false, isNative: true);
        var bridgedContent = ContentType("cpp-clangd", isCppClangd: true, isNative: true);
        var registry = new Mock<IContentTypeRegistryService>(MockBehavior.Strict);
        registry.Setup(r => r.GetContentType("C/C++")).Returns(nativeContent.Object);
        registry.Setup(r => r.GetContentType("cpp-clangd")).Returns(bridgedContent.Object);
        var logs = new List<string>();
        Action<string> log = message => logs.Add(message);

        var opened = new Mock<ITextBuffer>(MockBehavior.Strict);
        opened.SetupGet(b => b.ContentType).Returns(bridgedContent.Object);
        opened.Setup(b => b.ChangeContentType(nativeContent.Object, It.IsAny<object>()));
        var count = (int)switchBuffers.Invoke(null, new object[] {
            new[] { opened.Object },
            registry.Object,
            false,
            (Action<string>)log,
        });
        Assert(count == 1, "disabling takeover did not switch the opened cpp-clangd buffer");
        opened.Verify(b => b.ChangeContentType(nativeContent.Object, It.IsAny<object>()), Times.Once);

        var same = new Mock<ITextBuffer>(MockBehavior.Strict);
        same.SetupGet(b => b.ContentType).Returns(bridgedContent.Object);
        same.Setup(b => b.ChangeContentType(nativeContent.Object, It.IsAny<object>()));
        count = (int)switchBuffers.Invoke(null, new object[] {
            new ITextBuffer[] { same.Object, same.Object },
            registry.Object,
            false,
            (Action<string>)log,
        });
        Assert(count == 1, "duplicate views of one buffer switched more than once");
        same.Verify(b => b.ChangeContentType(It.IsAny<IContentType>(), It.IsAny<object>()), Times.Once);

        var failing = new Mock<ITextBuffer>(MockBehavior.Strict);
        failing.SetupGet(b => b.ContentType).Returns(bridgedContent.Object);
        failing.Setup(b => b.ChangeContentType(It.IsAny<IContentType>(), It.IsAny<object>()))
            .Throws(new InvalidOperationException("edit in progress"));
        var next = new Mock<ITextBuffer>(MockBehavior.Strict);
        next.SetupGet(b => b.ContentType).Returns(bridgedContent.Object);
        next.Setup(b => b.ChangeContentType(nativeContent.Object, It.IsAny<object>()));
        count = (int)switchBuffers.Invoke(null, new object[] {
            new[] { failing.Object, next.Object },
            registry.Object,
            false,
            (Action<string>)log,
        });
        Assert(count == 1, "one failing buffer blocked the remaining takeover restore");
        next.Verify(b => b.ChangeContentType(nativeContent.Object, It.IsAny<object>()), Times.Once);

        var plain = new Mock<ITextBuffer>(MockBehavior.Strict);
        plain.SetupGet(b => b.ContentType).Returns(nativeContent.Object);
        count = (int)switchBuffers.Invoke(null, new object[] {
            new[] { plain.Object },
            registry.Object,
            false,
            (Action<string>)log,
        });
        Assert(count == 0, "native buffer was switched while takeover was disabled");
        plain.Verify(b => b.ChangeContentType(It.IsAny<IContentType>(), It.IsAny<object>()), Times.Never);

        var missingRegistry = new Mock<IContentTypeRegistryService>(MockBehavior.Strict);
        missingRegistry.Setup(r => r.GetContentType(It.IsAny<string>())).Returns((IContentType)null);
        var missing = new Mock<ITextBuffer>(MockBehavior.Strict);
        missing.SetupGet(b => b.ContentType).Returns(bridgedContent.Object);
        count = (int)switchBuffers.Invoke(null, new object[] {
            new[] { missing.Object },
            missingRegistry.Object,
            false,
            (Action<string>)log,
        });
        Assert(count == 0, "missing registry content type was not skipped");

        var fresh = new Mock<ITextBuffer>(MockBehavior.Strict);
        fresh.SetupGet(b => b.ContentType).Returns(bridgedContent.Object);
        fresh.Setup(b => b.ChangeContentType(nativeContent.Object, It.IsAny<object>()));
        var routed = (bool)ensureRouted.Invoke(null, new object[] {
            fresh.Object,
            registry.Object,
            false,
            (Action<string>)log,
        });
        Assert(routed, "new cpp-clangd buffer stayed bridged while takeover was disabled");
        fresh.Verify(b => b.ChangeContentType(nativeContent.Object, It.IsAny<object>()), Times.Once);

        var reopened = new Mock<ITextBuffer>(MockBehavior.Strict);
        reopened.SetupGet(b => b.ContentType).Returns(nativeContent.Object);
        reopened.Setup(b => b.ChangeContentType(bridgedContent.Object, It.IsAny<object>()));
        routed = (bool)ensureRouted.Invoke(null, new object[] {
            reopened.Object,
            registry.Object,
            true,
            (Action<string>)log,
        });
        Assert(routed, "enabling takeover did not route the C/C++ buffer to cpp-clangd");
        var loads = (bool)typeof(ClangdVSPackage)
                        .GetMethod("ShouldLoadClangd", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { true });
        Assert(loads, "enabled takeover must allow clangd loads after routing");
        Assert(logs.Any(message => message.Contains("takeover")), "content-type routing left no takeover log");
        await Task.CompletedTask;
    }

    private static Mock<IContentType> ContentType(string typeName, bool isCppClangd, bool isNative)
    {
        var content = new Mock<IContentType>();
        content.SetupGet(c => c.TypeName).Returns(typeName);
        content.Setup(c => c.IsOfType("cpp-clangd")).Returns(isCppClangd);
        content.Setup(c => c.IsOfType("C/C++")).Returns(isNative);
        return content;
    }

    private static async Task TakeoverTransitionSerializationAsync()
    {
        var serializer = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.TakeoverTransitionSerializer");
        Assert(serializer != null, "takeover transition serializer is missing");
        var execute =
            serializer.GetMethod("ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                                 null, new[] { typeof(Func<Task>) }, null);
        Assert(execute != null, "serialized takeover execution is missing");
        var gate = typeof(ClangdVSPackage).GetField("_takeoverGate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(gate == null, "duplicate takeover gate _takeoverGate still exists alongside the serializer");

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = 0;
        var first = (Task)execute.Invoke(null, new object[] {
            new Func<Task>(async () =>
                           {
                               entered.SetResult(true);
                               await release.Task;
                           }),
        });
        await entered.Task;
        var second = (Task)execute.Invoke(null, new object[] {
            new Func<Task>(() =>
                           {
                               Interlocked.Exchange(ref secondStarted, 1);
                               return Task.CompletedTask;
                           }),
        });
        await Task.Delay(200);
        Assert(secondStarted == 0 && !second.IsCompleted,
               "takeover transitions overlapped menu/options concurrent toggle");
        release.SetResult(true);
        await first;
        await second;
        Assert(secondStarted == 1, "serialized takeover transition did not run the queued switch");
    }

    private static async Task TakeoverLifecycleGatingAsync()
    {
        var clientType = typeof(ClangdLanguageClient);
        var allowRestart = clientType.GetMethod("ShouldAllowRestart",
                                                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(allowRestart != null, "restart takeover gate is missing");
        Assert((bool)allowRestart.Invoke(null, new object[] { false }) == false,
               "restart policy allowed clangd while takeover was disabled");
        Assert((bool)allowRestart.Invoke(null, new object[] { true }),
               "restart policy blocked clangd while takeover was enabled");
        var allowPostInit = clientType.GetMethod("ShouldRunPostInitializeWork",
                                                 BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(allowPostInit != null, "post-initialize takeover gate is missing");
        Assert((bool)allowPostInit.Invoke(null, new object[] { false }) == false,
               "post-initialize work ran while takeover was disabled");
        Assert((bool)allowPostInit.Invoke(null, new object[] { true }),
               "post-initialize work was skipped while takeover was enabled");

        var settings = ClangdSettings.Instance;
        var originalTakeover = settings.TakeOverFromNativeIntelliSense;
        var originalRecovery = settings.RestartAfterCrash;
        try
        {
            settings.TakeOverFromNativeIntelliSense = false;
            var restartClient = new ClangdLanguageClient();
            try
            {
                await restartClient.RestartAsync();
                Assert(false, "manual restart started while takeover was disabled");
            }
            catch (InvalidOperationException ex)
            {
                Assert(ex.Message.Contains("takeover") || ex.Message.Contains("TakeOver") ||
                           ex.Message.Contains("disabled"),
                       "manual restart was not rejected for disabled takeover: " + ex.Message);
            }

            var contextField = typeof(Microsoft.VisualStudio.Shell.ThreadHelper)
                                   .GetField("_joinableTaskContextCache", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(contextField != null, "SDK ThreadHelper context adapter is unavailable");
            var previousContext = contextField.GetValue(null);
            using (var context = new Microsoft.VisualStudio.Threading.JoinableTaskContext())
            {
                contextField.SetValue(null, context);
                try
                {
                    var initializedClient = new ClangdLanguageClient();
                    SetClientField(initializedClient, "_initializationPending", true);
                    SetClientField(initializedClient, "_stopping", false);
                    Assert(ClientField<int>(initializedClient, "_initCount") == 0,
                           "precondition for post-initialize gating failed");
                    await initializedClient.OnServerInitializedAsync();
                    Assert(ClientField<int>(initializedClient, "_initCount") == 0,
                           "post-initialize work started while takeover was disabled");
                }
                finally
                {
                    contextField.SetValue(null, previousContext);
                }
            }

            settings.RestartAfterCrash = true;
            var crashedClient = new ClangdLanguageClient();
            var process = new System.Diagnostics.Process();
            try
            {
                SetClientField(crashedClient, "_serverProcess", process);
                SetClientField(crashedClient, "_expectingExit", false);
                SetClientField(crashedClient, "_crashCount", 0);
                typeof(ClangdLanguageClient)
                    .GetMethod("OnServerExited", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(crashedClient, new object[] { process, 1 });
                Assert(ClientField<int>(crashedClient, "_crashCount") == 0,
                       "crash recovery restarted clangd while takeover was disabled");
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
        }
        finally
        {
            settings.TakeOverFromNativeIntelliSense = originalTakeover;
            settings.RestartAfterCrash = originalRecovery;
        }
    }

    private static object ClientField(ClangdLanguageClient client, string name) =>
        typeof(ClangdLanguageClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);

    private static T ClientField<T>(ClangdLanguageClient client, string name) => (T)ClientField(client, name);

    private static void SetClientField(ClangdLanguageClient client, string name,
                                       object value) => typeof(ClangdLanguageClient)
                                                            .GetField(name,
                                                                      BindingFlags.Instance | BindingFlags.NonPublic)
                                                            .SetValue(client, value);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
