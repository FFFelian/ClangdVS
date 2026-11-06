using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using Microsoft.VisualStudio.Threading;

internal static class TakeoverDiagnosticsRegression
{
    private static readonly Uri DocumentUri = new Uri("file:///C:/Work/a.cpp");

    internal static async Task RunAsync()
    {
        var assembly = typeof(ClangdVSPackage).Assembly;
        var sourceType = assembly.GetType("ClangdVS.TakeoverDiagnosticSource");
        var cleanupType = assembly.GetType("ClangdVS.TakeoverDiagnosticCleanup");
        Assert(sourceType != null && cleanupType != null,
               "takeover diagnostic cleanup and provider descriptions are missing");
        var clear = cleanupType.GetMethod("ClearRetiredProviderAsync",
                                          BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var fromClient = sourceType.GetMethod("FromClient",
                                              BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(clear != null && fromClient != null,
               "takeover diagnostic cleanup does not expose its provider-aware async entry");

        var native = Source(sourceType, "原生本地化客户端", remoteOnly: true);
        var clangd = Source(sourceType, "clangd", remoteOnly: false);
        var nativeId = "C:/Work/a.cpp原生本地化客户端";
        var clangdId = "C:/Work/a.cppclangd";
        var buildId = "Build";
        var similarlyNamedId = clangdId + "-extra";
        var reports = new[] {
            Report(nativeId),
            Report(nativeId),
            Report(clangdId),
            Report(buildId),
            Report(similarlyNamedId),
        };

        var nativeCalls = new List<ClearCall>();
        var cleared = await InvokeClear(clear, true, new[] { native }, clangd, reports,
                                        (remoteOnly, id, token) =>
                                        {
                                            nativeCalls.Add(new ClearCall(remoteOnly, id));
                                            return Task.CompletedTask;
                                        });
        Assert(cleared == 1, "duplicate native reports were not coalesced into one generator cleanup");
        Assert(nativeCalls.Count == 1 && nativeCalls[0].RemoteOnly &&
                   nativeCalls[0].GeneratorId == nativeId,
               "native retirement did not use the original broadcast diagnostic manager and actual client name");
        Assert(nativeCalls.All(call => call.GeneratorId != clangdId &&
                                      call.GeneratorId != buildId &&
                                      call.GeneratorId != similarlyNamedId),
               "native retirement touched clangd, Build, or a similarly named provider");

        var clangdCalls = new List<ClearCall>();
        cleared = await InvokeClear(clear, false, new[] { native }, clangd, reports,
                                    (remoteOnly, id, token) =>
                                    {
                                        clangdCalls.Add(new ClearCall(remoteOnly, id));
                                        return Task.CompletedTask;
                                    });
        Assert(cleared == 1 && clangdCalls.Count == 1 && !clangdCalls[0].RemoteOnly &&
                   clangdCalls[0].GeneratorId == clangdId,
               "clangd retirement did not use the ordinary diagnostic manager exactly once");
        Assert(clangdCalls.All(call => call.GeneratorId != nativeId && call.GeneratorId != buildId),
               "clangd retirement touched native or Build diagnostics");

        var noStaleCalls = 0;
        cleared = await InvokeClear(clear, false, new[] { native }, clangd,
                                    new[] { Report(nativeId), Report(buildId) },
                                    (remoteOnly, id, token) =>
                                    {
                                        noStaleCalls++;
                                        return Task.CompletedTask;
                                    });
        Assert(cleared == 0 && noStaleCalls == 0,
               "a transition with no stale clangd reports still cleared a diagnostic source");

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = InvokeClear(clear, false, new[] { native }, clangd, new[] { Report(clangdId) },
                                  async (remoteOnly, id, token) =>
                                  {
                                      started.SetResult(true);
                                      await release.Task;
                                  });
        await started.Task;
        Assert(!pending.IsCompleted, "diagnostic cleanup was fire-and-forget instead of awaited");
        release.SetResult(true);
        Assert(await pending == 1, "awaited diagnostic cleanup did not report its completed source removal");

        var attemptedAfterFailure = new List<string>();
        var secondNativeId = "C:/Work/b.cpp原生本地化客户端";
        try
        {
            await InvokeClear(clear, true, new[] { native }, clangd,
                              new[] { Report(nativeId), Report(secondNativeId) },
                              (remoteOnly, id, token) =>
                              {
                                  attemptedAfterFailure.Add(id);
                                  return id == nativeId
                                             ? Task.FromException(new InvalidOperationException("RPC failed"))
                                             : Task.CompletedTask;
                              });
            Assert(false, "a failed diagnostic cleanup was reported as successful");
        }
        catch (AggregateException ex)
        {
            Assert(ex.InnerExceptions.Count == 1 &&
                       ex.InnerExceptions[0] is InvalidOperationException,
                   "diagnostic cleanup did not preserve the original RPC failure");
        }
        Assert(attemptedAfterFailure.SequenceEqual(new[] { nativeId, secondNativeId }),
               "one failed generator cleanup prevented the remaining stale documents from being cleared");

        var source = fromClient.Invoke(null, new object[] { new NativeClientStub() });
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Assert((string)sourceType.GetProperty("Name", instanceFlags).GetValue(source) == "原生本地化客户端",
               "provider discovery did not read ILanguageClient.Name dynamically");
        Assert((bool)sourceType.GetProperty("RemoteOnly", instanceFlags).GetValue(source),
               "DisableUserExperienceAttribute did not select the broadcast diagnostic manager");
    }

    private static object Source(Type type, string name, bool remoteOnly) =>
        Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                 null, new object[] { name, remoteOnly }, null);

    private static DiagnosticReport Report(string generatorId) =>
        new DiagnosticReport(1, generatorId, 1,
                             new[] {
                                 new DiagnosticCollection(DocumentUri, 1, Array.Empty<Diagnostic>(), null, null, null),
                             });

    private static async Task<int> InvokeClear(MethodInfo clear, bool enabled, object[] native, object clangd,
                                               IEnumerable<DiagnosticReport> reports,
                                               Func<bool, string, CancellationToken, Task> clearGenerator)
    {
        var nativeSources = native.Select(item => item).ToArray();
        var typedNative = Array.CreateInstance(native[0].GetType(), nativeSources.Length);
        Array.Copy(nativeSources, typedNative, nativeSources.Length);
        Func<CancellationToken, Task<IReadOnlyList<DiagnosticReport>>> getReports = token =>
            Task.FromResult<IReadOnlyList<DiagnosticReport>>(reports.ToArray());
        try
        {
            return await (Task<int>)clear.Invoke(null, new object[] {
                enabled,
                typedNative,
                clangd,
                getReports,
                clearGenerator,
                CancellationToken.None,
            });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private readonly struct ClearCall
    {
        internal ClearCall(bool remoteOnly, string generatorId)
        {
            RemoteOnly = remoteOnly;
            GeneratorId = generatorId;
        }

        internal bool RemoteOnly { get; }

        internal string GeneratorId { get; }
    }

    [DisableUserExperience(true)]
    private sealed class NativeClientStub : ILanguageClient
    {
        public event AsyncEventHandler<EventArgs> StartAsync { add { } remove { } }
        public event AsyncEventHandler<EventArgs> StopAsync { add { } remove { } }
        public string Name => "原生本地化客户端";
        public IEnumerable<string> ConfigurationSections => null;
        public object InitializationOptions => null;
        public IEnumerable<string> FilesToWatch => null;
        public bool ShowNotificationOnInitializeFailed => false;
        public Task OnLoadedAsync() => Task.CompletedTask;
        public Task<Connection> ActivateAsync(CancellationToken token) => Task.FromResult<Connection>(null);
        public Task OnServerInitializedAsync() => Task.CompletedTask;
        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(
            ILanguageClientInitializationInfo info) => Task.FromResult<InitializationFailureContext>(null);
    }
}
