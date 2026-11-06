using System;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClangdVS;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

internal static class InspectionLifetimeRegression
{
    internal static async Task RunAsync()
    {
        foreach (var reason in new[] { "disabled", "takeover", "closed", "reopened", "replaced", "stopping", "stopped", "restart", "current", "cancelled" })
        foreach (var command in new[] { 0x1104, 0x1105, 0x1106 })
        foreach (var error in new[] { false, true })
        {
            using (var session = new InspectionSession())
            using (var window = new ClangdInspectionToolWindow())
            using (var cancellation = new CancellationTokenSource())
            {
                var document = new object();
                object activeDocument = document;
                var version = Version(session.Client);
                var source = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                var current = true;
                Func<bool> gate = () => current && ContextCurrent(command, document, activeDocument, session.Client, version);
                var pending = Complete(source.Task, cancellation.Token, gate, window);
                if (reason == "disabled")
                {
                    if (command == 0x1104) session.Settings.EnableAstView = false;
                    if (command == 0x1105) session.Settings.EnableSymbolInfo = false;
                    if (command == 0x1106) session.Settings.EnableTypeHierarchy = false;
                }
                if (reason == "takeover") session.Settings.TakeOverFromNativeIntelliSense = false;
                if (reason == "closed") activeDocument = null;
                if (reason == "reopened") activeDocument = new object(); // Same path is deliberately irrelevant.
                if (reason == "replaced") InspectionSession.Current(new ClangdLanguageClient());
                if (reason == "stopping") InspectionSession.Set(session.Client, "_stopping", true);
                if (reason == "stopped") InspectionSession.Set(session.Client, "_rpc", null);
                if (reason == "restart") InspectionSession.Set(session.Client, "_sessionVersion", version + 1);
                if (reason == "current") current = false;
                if (reason == "cancelled") { cancellation.Cancel(); cancellation.Dispose(); }
                if (error) source.SetException(new InvalidOperationException("late-error"));
                else source.SetResult(new JObject { ["kind"] = "Late" });
                await pending;
                Assert(Tree(window).Items.Count == 0 && !Status(window).Text.Contains("late-error"),
                       reason + " allowed late " + (error ? "failure" : "success"));
            }
        }
        using (var session = new InspectionSession())
        using (var window = new ClangdInspectionToolWindow())
        {
            await Complete(Task.FromResult<JToken>(new JObject { ["kind"] = "Fresh" }),
                           CancellationToken.None, () => true, window);
            Assert((string)((TreeViewItem)Tree(window).Items[0]).Header == "Fresh", "fresh result lost");
            var source = new TaskCompletionSource<JToken>();
            source.SetException(new InvalidOperationException("fresh-error"));
            await Complete(source.Task, CancellationToken.None, () => true, window);
            Assert(Status(window).Text.Contains("fresh-error"), "fresh failure lost");
        }
        await HierarchySessionAsync();
        await HierarchyLateRepliesAsync();
        await UiPublicationGateAsync();
        SettingsInvalidate();
        await InspectionClientSessionRegression.RunAsync();
        Console.WriteLine("PASS: inspection success/failure gates, document identity and same-client session retirement");
    }

    private static void SettingsInvalidate()
    {
        foreach (var takeover in new[] { false, true })
        using (var session = new InspectionSession())
        using (var window = new ClangdInspectionToolWindow())
        {
            // No VS service container is needed: inspection invalidation precedes broker work.
            var package = (ClangdVSPackage)FormatterServices.GetUninitializedObject(typeof(ClangdVSPackage));
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            InspectionSession.Set(package, "_inspectionCancellation", cancellation);
            InspectionSession.Set(package, "_inspectionWindow", window);
            InspectionSession.Set(package, "_inspectionCommandId", 0x1106);
            Show(window, JToken.Parse("{\"item\":{\"name\":\"A\"},\"supertypes\":[{\"name\":\"Old\"}]}"));
            var oldGroup = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]).Items[0];
            if (takeover)
            {
                session.Settings.TakeOverFromNativeIntelliSense = false;
                typeof(ClangdVSPackage).GetMethod("OnTakeoverSettingChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(package, new object[] { false });
            }
            else
            {
                session.Settings.EnableTypeHierarchy = false;
                typeof(ClangdVSPackage).GetMethod("OnFeatureSettingsChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(package, new object[] { true, false, false, false, false });
            }
            Assert(token.IsCancellationRequested && Tree(window).Items.Count == 0,
                   "settings off did not cancel the query and clear its tree");
            session.Settings.EnableTypeHierarchy = true;
            session.Settings.TakeOverFromNativeIntelliSense = true;
            oldGroup.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent, oldGroup));
            Assert(!oldGroup.Items.OfType<TreeViewItem>().Any(n => (string)n.Header == "Old"),
                   "re-enabling settings resurrected an invalidated delegate");
        }
    }

    private static async Task HierarchyLateRepliesAsync()
    {
        foreach (var reason in new[] { "restart", "disabled", "takeover", "document", "reset", "fresh" })
        foreach (var error in new[] { false, true })
        using (var session = new InspectionSession())
        using (var window = new ClangdInspectionToolWindow())
        {
            // The server deliberately ignores cancellation until the test releases it.
            var name = "InspectionRpc-" + Guid.NewGuid().ToString("N");
            using (var serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                       PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                var accept = serverStream.WaitForConnectionAsync();
                await clientStream.ConnectAsync(3000);
                await accept;
                var target = new HierarchyTarget();
                using (var server = JsonRpc.Attach(serverStream, target))
                using (var rpc = JsonRpc.Attach(clientStream))
                {
                    InspectionSession.Set(session.Client, "_rpc", rpc);
                    var documentCurrent = true;
                    typeof(ClangdInspectionToolWindow).GetMethod("ShowResult", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(window, new object[] { "Type Hierarchy", new JObject { ["name"] = "A" },
                                                     (Func<bool>)(() => documentCurrent) });
                    var supers = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]).Items[0];
                    var loader = HierarchyLoader(supers);
                    supers.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent, supers));
                    Assert(await Task.WhenAny(target.Started.Task, Task.Delay(3000)) == target.Started.Task,
                           "hierarchy request never reached RPC");
                    if (reason == "restart") InspectionSession.Set(session.Client, "_sessionVersion", Version(session.Client) + 1);
                    if (reason == "disabled") session.Settings.EnableTypeHierarchy = false;
                    if (reason == "takeover") session.Settings.TakeOverFromNativeIntelliSense = false;
                    if (reason == "document") documentCurrent = false;
                    if (reason == "reset") Show(window, new JObject { ["name"] = "New" });
                    if (error) target.Result.SetException(new InvalidOperationException("late-hierarchy-error"));
                    else target.Result.SetResult(new JArray(new JObject { ["name"] = "LateChild" }));
                    // Await the actual async-void loader's finally block, not an arbitrary sleep.
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while ((bool)loader.Item2.GetValue(loader.Item1) && DateTime.UtcNow < deadline)
                        await Task.Delay(5);
                    Assert(!(bool)loader.Item2.GetValue(loader.Item1), "hierarchy loader did not finish");
                    var headers = string.Join("\n", supers.Items.OfType<TreeViewItem>().Select(n => n.Header));
                    if (reason == "fresh")
                        Assert(headers.Contains(error ? "late-hierarchy-error" : "LateChild"),
                               "current hierarchy " + (error ? "error" : "result") + " was lost");
                    else
                        Assert(!headers.Contains("LateChild") && !headers.Contains("late-hierarchy-error"),
                               reason + " accepted late hierarchy " + (error ? "error" : "result"));
                }
            }
        }
        Console.WriteLine("PASS: real hierarchy RPC late successes/errors are retired across session/settings/document/reset");
    }

    private static async Task UiPublicationGateAsync()
    {
        foreach (var error in new[] { false, true })
        using (var window = new ClangdInspectionToolWindow())
        {
            var current = true;
            var source = new TaskCompletionSource<JToken>();
            if (error) source.SetException(new InvalidOperationException("late-ui-error"));
            else source.SetResult(new JObject { ["kind"] = "LateUi" });
            Func<Action, Task> onMainThread = publish =>
            {
                current = false; // Invalidation occurs while the UI continuation is queued.
                publish();
                return Task.CompletedTask;
            };
            await (Task)Method("CompleteInspectionAsync").Invoke(null, new object[] {
                source.Task, CancellationToken.None, onMainThread, (Func<bool>)(() => current), window, "AST" });
            Assert(Tree(window).Items.Count == 0 && !Status(window).Text.Contains("late-ui-error"),
                   "publication gate ran before returning to the UI thread");
        }
    }

    private static Tuple<object, FieldInfo> HierarchyLoader(TreeViewItem node)
    {
        var store = typeof(UIElement).GetProperty("EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(node);
        var handlers = (Array)store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Invoke(store, new object[] { TreeViewItem.ExpandedEvent });
        foreach (var info in handlers)
        {
            var target = ((Delegate)info.GetType().GetProperty("Handler").GetValue(info)).Target;
            var loading = target?.GetType().GetField("loading", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (loading != null) return Tuple.Create(target, loading);
        }
        throw new InvalidOperationException("hierarchy loader handler missing");
    }

    public sealed class HierarchyTarget
    {
        internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
        internal readonly TaskCompletionSource<JToken> Result = new TaskCompletionSource<JToken>();
        [JsonRpcMethod("typeHierarchy/supertypes")]
        public Task<JToken> SupertypesAsync(JObject item)
        {
            Started.TrySetResult(true);
            return Result.Task;
        }
    }

    private static async Task HierarchySessionAsync()
    {
        using (var session = new InspectionSession())
        using (var window = new ClangdInspectionToolWindow())
        {
            Show(window, JToken.Parse("{\"item\":{\"name\":\"A\"},\"supertypes\":[{\"name\":\"Old\"}]}"));
            var supers = (TreeViewItem)((TreeViewItem)Tree(window).Items[0]).Items[0];
            InspectionSession.Set(session.Client, "_sessionVersion", Version(session.Client) + 1);
            supers.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent, supers));
            Assert(!supers.Items.OfType<TreeViewItem>().Any(n => (string)n.Header == "Old"),
                   "same-client restart accepted prepared hierarchy children");

            Show(window, new JObject { ["name"] = "B" });
            var root = (TreeViewItem)Tree(window).Items[0];
            supers = (TreeViewItem)root.Items[0];
            var version = Version(session.Client);
            InspectionSession.Set(session.Client, "_sessionVersion", version + 1);
            // An inert RPC would throw if the obsolete item were dispatched at all.
            supers.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent, supers));
            await Task.Yield();
            Assert(supers.Items.OfType<TreeViewItem>().Any(n => ((string)n.Header).Contains("失效") ||
                                                               ((string)n.Header).Contains("更换")),
                   "old item was sent to restarted RPC instead of being gated before dispatch");
        }
    }

    private static bool ContextCurrent(int command, object document, object activeDocument, ClangdLanguageClient client, long version) =>
        (bool)Method("IsInspectionContextCurrent").Invoke(null, new object[] { command, document, activeDocument, client, version });
    private static Task Complete(Task<JToken> result, CancellationToken token, Func<bool> current,
                                 ClangdInspectionToolWindow window) =>
        (Task)Method("CompleteInspectionAsync").Invoke(null, new object[] {
            result, token, (Func<Action, Task>)(publish => { publish(); return Task.CompletedTask; }),
            current, window, "AST" });
    private static MethodInfo Method(string name)
    {
        var method = typeof(ClangdVSPackage).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method != null, "missing inspection lifecycle helper " + name);
        return method;
    }
    private static long Version(ClangdLanguageClient client) => (long)typeof(ClangdLanguageClient)
        .GetProperty("SessionVersion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
    private static T Field<T>(object value, string name) => (T)value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
    private static TreeView Tree(ClangdInspectionToolWindow window) => Field<TreeView>(window, "_tree");
    private static TextBlock Status(ClangdInspectionToolWindow window) => Field<TextBlock>(window, "_status");
    private static void Show(ClangdInspectionToolWindow window, JToken result) => typeof(ClangdInspectionToolWindow)
        .GetMethod("ShowResult", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(window, new object[] { "Type Hierarchy", result, null });
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
