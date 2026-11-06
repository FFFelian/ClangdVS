using System;
using System.Threading.Tasks;
using ClangdVS;
using Newtonsoft.Json.Linq;

internal static class ClangdFeatureMiddleLayerRegression
{
    internal static async Task RunAsync()
    {
        var settings = new ClangdSettings();
        var layerType = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdFeatureMiddleLayer");
        Assert(layerType != null, "clangd feature middle layer is missing");
        var layer = Activator.CreateInstance(layerType,
                                            System.Reflection.BindingFlags.Instance |
                                                System.Reflection.BindingFlags.NonPublic,
                                            null, new object[] { new Func<ClangdSettings>(() => settings) }, null);
        var canHandle = layerType.GetMethod("CanHandle");
        var handleRequest = layerType.GetMethod("HandleRequestAsync");
        var handleNotification = layerType.GetMethod("HandleNotificationAsync");
        Assert(canHandle != null && handleRequest != null && handleNotification != null,
               "middle layer does not implement the VS interception contract");

        Func<string, bool> method = name => (bool)canHandle.Invoke(layer, new object[] { name });
        Assert(method("textDocument/completion") && method("textDocument/hover"),
               "completion and hover requests are not intercepted");
        Assert(!method("textDocument/publishDiagnostics") && !method("workspace/semanticTokens/refresh"),
               "server-to-client notifications were incorrectly treated as outgoing client requests");
        Assert(method("textDocument/formatting") && method("textDocument/rangeFormatting"),
               "formatting requests are not intercepted");
        Assert(method("textDocument/codeAction") && method("codeAction/resolve") && method("workspace/executeCommand"),
               "code action resolve and command execution must share the feature gate");
        Assert(method("textDocument/definition") && method("textDocument/references") &&
                   method("textDocument/documentSymbol") && method("workspace/symbol"),
               "navigation requests are not intercepted");
        Assert(method("textDocument/rename"), "rename is not included in the navigation gate");
        Assert(!method("workspace/didChangeConfiguration"), "unrelated requests are intercepted");

        settings.EnableCompletionAndHover = false;
        var sent = false;
        Func<JToken, Task<JToken>> sendRequest = _ =>
        {
            sent = true;
            return Task.FromResult<JToken>(new JObject { ["ok"] = true });
        };
        var empty = await (Task<JToken>)handleRequest.Invoke(
            layer, new object[] { "textDocument/completion", new JObject(), sendRequest });
        Assert(!sent && empty is JArray emptyItems && emptyItems.Count == 0,
               "disabled completion was sent or returned an invalid nonempty result");

        settings.EnableCompletionAndHover = true;
        var forwarded = await (Task<JToken>)handleRequest.Invoke(
            layer, new object[] { "textDocument/hover", new JObject(), sendRequest });
        Assert(sent && (bool)forwarded["ok"], "enabled hover request was not forwarded");

        settings.EnableCodeActions = false;
        foreach (var name in new[] { "codeAction/resolve", "workspace/executeCommand" })
        {
            sent = false;
            var blocked = await (Task<JToken>)handleRequest.Invoke(layer,
                new object[] { name, new JObject { ["edit"] = new JObject(), ["command"] = new JObject() }, sendRequest });
            Assert(!sent && (name != "codeAction/resolve" || blocked?["edit"] == null && blocked?["command"] == null),
                   "disabled code actions still resolve edits or execute commands");
        }

        Assert(!method("initialize"), "initialize must be handled on the wire, not the bypassed middleware");
        settings.EnableSemanticHighlighting = false;
        var tokens = await (Task<JToken>)handleRequest.Invoke(
            layer, new object[] { "textDocument/semanticTokens/full/delta", new JObject(), sendRequest });
        Assert(tokens["data"] is JArray data && data.Count == 0 && tokens["edits"] == null,
               "disabled semantic delta must replace cached tokens rather than leave them unchanged");

        var notificationSent = false;
        Func<JToken, Task> sendNotification = _ =>
        {
            notificationSent = true;
            return Task.CompletedTask;
        };
        await (Task)handleNotification.Invoke(layer,
                                             new object[] { "textDocument/didChange", new JObject(),
                                                            sendNotification });
        Assert(notificationSent, "unrelated outgoing document notification was swallowed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
