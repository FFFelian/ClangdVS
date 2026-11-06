using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

// Focused client regressions: no VS host, production build or server process required.
internal static class InspectionClientSessionRegression
{
    internal static async Task RunAsync(string group = null)
    {
        var failures = new List<string>();
        if (group == null || group == "stop")
            foreach (var boundary in new[] { "version", "inlay", "running", "gate" })
            {
                var check = boundary;
                await Check(failures, "Stop before host await/" + check, () => StopBoundaryAsync(check));
            }
        if (group == null || group == "hierarchy")
        {
            foreach (var boundary in new[] { "prepare", "super", "sub", "final-sub" })
            foreach (var change in new[] { "version", "rpc", "stopping", "cancel" })
            {
                var stage = boundary;
                var reason = change;
                await Check(failures, stage + "/" + reason,
                            () => HierarchyBoundaryAsync(stage, reason));
            }
            await Check(failures, "fresh multi-root hierarchy stays on one RPC", FreshHierarchyAsync);
            await Check(failures, "pre-cancelled hierarchy does not dispatch", PreCancelledAsync);
            foreach (var boundary in new[] { "prepare", "super" })
            {
                var stage = boundary;
                await Check(failures, stage + "/disabled", () => HierarchyBoundaryAsync(stage, "disabled"));
            }
        }
        if (group == null || group == "extension")
            foreach (var method in new[] { "RequestAstAsync", "RequestSymbolInfoAsync" })
                foreach (var change in new[] { "version", "rpc", "stopping", "cancel" })
                {
                    var request = method;
                    var reason = change;
                    await Check(failures, request + "/" + reason,
                                () => ExtensionBoundaryAsync(request, reason));
                }
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static async Task StopBoundaryAsync(string check)
    {
        using (var fixture = new ClientFixture())
        {
            var client = fixture.Client;
            var release = Signal<bool>();
            var entered = Signal<bool>();
            var gate = Field<object>(client, "_initializationGate");
            var inlay = typeof(ClangdLanguageClient).Assembly.GetType("ClangdVS.InlayHintCoordinator");
            var generation = inlay.GetField("_sessionGeneration", BindingFlags.Static | BindingFlags.NonPublic);
            var beforeGeneration = (long)generation.GetValue(null);
            var beforeVersion = Version(client);
            var liveProcess = Process.GetCurrentProcess();
            Set(client, "_serverProcess", liveProcess);
            client.StopAsync += async (sender, args) =>
            {
                entered.TrySetResult(true);
                await release.Task;
            };
            // A real channel is required for IsRunning, but never used by this test.
            using (var pair = await RpcPair.CreateAsync(new HierarchyTarget("none")))
            {
                Set(client, "_rpc", pair.Rpc);
                var stop = (Task)Method("StopServerAsync").Invoke(client, null);
                try
                {
                    await Within(entered.Task);
                    Assert(!stop.IsCompleted, "host Stop was not held open");
                    if (check == "version")
                        Assert(Version(client) > beforeVersion, "session remained current while host Stop was pending");
                    if (check == "inlay")
                        Assert((long)generation.GetValue(null) > beforeGeneration,
                               "Inlay was not notified before host Stop");
                    if (check == "running")
                        Assert(!(bool)typeof(ClangdLanguageClient).GetProperty("IsRunning",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client),
                        "stopping client still reports IsRunning");
                    if (check == "gate") await Within(Task.Run(() => { lock (gate) { } }));
                }
                finally
                {
                    // Stop cleanup must never kill the harness itself.
                    Set(client, "_serverProcess", null);
                    liveProcess.Dispose();
                    release.TrySetResult(true);
                    await Within(stop);
                }
            }
        }
    }

    private static async Task HierarchyBoundaryAsync(string boundary, string change)
    {
        using (var fixture = new ClientFixture())
        using (var old = await RpcPair.CreateAsync(new HierarchyTarget(boundary)))
        using (var replacement = await RpcPair.CreateAsync(new HierarchyTarget("none")))
        using (var cancellation = new CancellationTokenSource())
        {
            Set(fixture.Client, "_rpc", old.Rpc);
            var pending = Request(fixture.Client, "RequestTypeHierarchyAsync", cancellation.Token);
            await Within(old.Target.Started.Task);
            Change(fixture.Client, replacement.Rpc, cancellation, change);
            old.Target.Release.TrySetResult(true); // Server deliberately returns even after invalidation.
            var retired = await Retired(pending, change == "cancel", cancellation.Token);
            Assert(replacement.Target.Calls.Count == 0, "old hierarchy item reached replacement RPC");
            var expected = boundary == "prepare" ? 1 : boundary == "super" ? 2 : boundary == "sub" ? 3 : 5;
            Assert(old.Target.Calls.Count == expected, "hierarchy dispatched another RPC after " + boundary);
            Assert(retired, "obsolete hierarchy result was returned for publication");
        }
    }

    private static async Task ExtensionBoundaryAsync(string method, string change)
    {
        using (var fixture = new ClientFixture())
        using (var old = await RpcPair.CreateAsync(new HierarchyTarget("extension")))
        using (var replacement = await RpcPair.CreateAsync(new HierarchyTarget("none")))
        using (var cancellation = new CancellationTokenSource())
        {
            Set(fixture.Client, "_rpc", old.Rpc);
            var pending = Request(fixture.Client, method, cancellation.Token);
            await Within(old.Target.Started.Task);
            Change(fixture.Client, replacement.Rpc, cancellation, change);
            old.Target.Release.TrySetResult(true);
            Assert(await Retired(pending, change == "cancel", cancellation.Token),
                   "obsolete extension result was returned for publication");
        }
    }

    private static async Task FreshHierarchyAsync()
    {
        using (var fixture = new ClientFixture())
        using (var pair = await RpcPair.CreateAsync(new HierarchyTarget("none")))
        {
            Set(fixture.Client, "_rpc", pair.Rpc);
            var result = (JArray)await Request(fixture.Client, "RequestTypeHierarchyAsync", CancellationToken.None);
            Assert(result.Count == 2 && (string)result[0]["item"]["name"] == "RootA" &&
                   (string)result[1]["item"]["name"] == "RootB", "prepared roots were lost");
            Assert((string)result[0]["supertypes"][0]["name"] == "Base" &&
                   (string)result[1]["subtypes"][0]["name"] == "Derived", "hierarchy children were lost");
            Assert(string.Join(",", pair.Target.Calls) == "prepare,super,sub,super,sub", "incorrect RPC sequence");
        }
    }

    private static async Task PreCancelledAsync()
    {
        using (var fixture = new ClientFixture())
        using (var pair = await RpcPair.CreateAsync(new HierarchyTarget("none")))
        using (var cancellation = new CancellationTokenSource())
        {
            Set(fixture.Client, "_rpc", pair.Rpc);
            cancellation.Cancel();
            try
            {
                await Request(fixture.Client, "RequestTypeHierarchyAsync", cancellation.Token);
                throw new InvalidOperationException("caller cancellation was swallowed");
            }
            catch (OperationCanceledException) { }
            Assert(pair.Target.Calls.Count == 0, "pre-cancelled request reached RPC");
        }
    }

    private static void Change(ClangdLanguageClient client, JsonRpc replacement,
                               CancellationTokenSource cancellation, string change)
    {
        if (change == "version") Set(client, "_sessionVersion", Version(client) + 1);
        if (change == "rpc") Set(client, "_rpc", replacement); // Identity is an independent gate.
        if (change == "stopping") Set(client, "_stopping", true);
        if (change == "disabled") ClangdSettings.Instance.EnableTypeHierarchy = false;
        if (change == "cancel") cancellation.Cancel();
    }

    private static async Task<bool> Retired(Task<JToken> pending, bool mustCancel, CancellationToken token)
    {
        try
        {
            await Within(pending);
            Assert(!mustCancel, "caller cancellation was swallowed");
            return await pending == null;
        }
        catch (OperationCanceledException ex)
        {
            if (mustCancel) Assert(ex.CancellationToken == token, "caller cancellation token was lost");
            return true;
        }
    }

    private static Task<JToken> Request(ClangdLanguageClient client, string method, CancellationToken token) =>
        (Task<JToken>)Method(method).Invoke(client, new object[] { "file:///C:/Work/a.cpp", 1, 2, token });
    private static MethodInfo Method(string name) => typeof(ClangdLanguageClient)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
    private static long Version(ClangdLanguageClient client) => (long)typeof(ClangdLanguageClient)
        .GetProperty("SessionVersion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
    private static T Field<T>(object target, string field) => (T)target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private static TaskCompletionSource<T> Signal<T>() =>
        new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(5000)) != task)
            throw new TimeoutException("focused client regression timed out");
        await task;
    }
    private static async Task Check(List<string> failures, string name, Func<Task> test)
    {
        try { await test(); Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { failures.Add("FAIL: " + name + ": " + ex.GetBaseException().Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed class HierarchyTarget
    {
        private readonly string _blocked;
        internal readonly List<string> Calls = new List<string>();
        internal readonly TaskCompletionSource<bool> Started = Signal<bool>();
        internal readonly TaskCompletionSource<bool> Release = Signal<bool>();
        internal HierarchyTarget(string blocked) { _blocked = blocked; }
        private async Task<JToken> Reply(string stage, JToken result)
        {
            Calls.Add(stage);
            if (_blocked == stage || (_blocked == "final-sub" && stage == "sub" && Calls.Count == 5))
            {
                Started.TrySetResult(true);
                await Release.Task;
            }
            return result;
        }
        [JsonRpcMethod("textDocument/prepareTypeHierarchy")]
        public Task<JToken> Prepare(JObject textDocument, JObject position) => Reply("prepare",
            new JArray(new JObject { ["name"] = "RootA", ["data"] = "old-a" },
                       new JObject { ["name"] = "RootB", ["data"] = "old-b" }));
        [JsonRpcMethod("typeHierarchy/supertypes")]
        public Task<JToken> Super(JObject item) => Reply("super", new JArray(new JObject { ["name"] = "Base" }));
        [JsonRpcMethod("typeHierarchy/subtypes")]
        public Task<JToken> Sub(JObject item) => Reply("sub", new JArray(new JObject { ["name"] = "Derived" }));
        [JsonRpcMethod("textDocument/ast")]
        public Task<JToken> Ast(JObject textDocument, JObject range) => Reply("extension", new JObject { ["kind"] = "Late" });
        [JsonRpcMethod("textDocument/symbolInfo")]
        public Task<JToken> Symbol(JObject textDocument, JObject position) => Reply("extension", new JObject { ["name"] = "Late" });
    }

    private sealed class RpcPair : IDisposable
    {
        private readonly NamedPipeServerStream _serverStream;
        private readonly NamedPipeClientStream _clientStream;
        private JsonRpc _server;
        internal JsonRpc Rpc;
        internal readonly HierarchyTarget Target;
        private RpcPair(HierarchyTarget target)
        {
            Target = target;
            var name = "InspectionClient-" + Guid.NewGuid().ToString("N");
            _serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
        internal static async Task<RpcPair> CreateAsync(HierarchyTarget target)
        {
            var pair = new RpcPair(target);
            var accept = pair._serverStream.WaitForConnectionAsync();
            await pair._clientStream.ConnectAsync(3000);
            await accept;
            pair._server = JsonRpc.Attach(pair._serverStream, target);
            pair.Rpc = JsonRpc.Attach(pair._clientStream);
            return pair;
        }
        public void Dispose()
        {
            Target.Release.TrySetResult(true);
            Rpc?.Dispose();
            _server?.Dispose();
            _clientStream.Dispose();
            _serverStream.Dispose();
        }
    }

    private sealed class ClientFixture : IDisposable
    {
        private readonly FieldInfo _settings = typeof(ClangdSettings)
            .GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly object _previousSettings;
        internal readonly ClangdLanguageClient Client = new ClangdLanguageClient();
        internal ClientFixture()
        {
            _previousSettings = _settings.GetValue(null);
            _settings.SetValue(null, new ClangdSettings {
                TakeOverFromNativeIntelliSense = true, EnableAstView = true,
                EnableSymbolInfo = true, EnableTypeHierarchy = true,
            });
        }
        public void Dispose() { _settings.SetValue(null, _previousSettings); }
    }
}
