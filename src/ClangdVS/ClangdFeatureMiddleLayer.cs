using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;

namespace ClangdVS
{
    internal sealed class ClangdFeatureMiddleLayer : ILanguageClientMiddleLayer
    {
        private static readonly HashSet<string> NavigationMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "textDocument/declaration",
            "textDocument/definition",
            "textDocument/implementation",
            "textDocument/typeDefinition",
            "textDocument/references",
            "textDocument/rename",
            "textDocument/documentSymbol",
            "workspace/symbol",
        };

        private readonly Func<ClangdSettings> _getSettings;

        internal ClangdFeatureMiddleLayer(Func<ClangdSettings> getSettings)
        {
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        }

        public bool CanHandle(string methodName) =>
            methodName == "textDocument/completion" ||
            methodName == "textDocument/hover" ||
            methodName == "textDocument/didClose" ||
            methodName == "textDocument/formatting" ||
            methodName == "textDocument/rangeFormatting" || methodName == "textDocument/codeAction" ||
            methodName == "codeAction/resolve" || methodName == "workspace/executeCommand" ||
            methodName == "textDocument/semanticTokens/full" ||
            methodName == "textDocument/semanticTokens/full/delta" ||
            methodName == "textDocument/semanticTokens/range" ||
            NavigationMethods.Contains(methodName);

        public async Task<JToken> HandleRequestAsync(string methodName, JToken methodParam,
                                               Func<JToken, Task<JToken>> sendRequest)
        {
            if (sendRequest == null)
                throw new ArgumentNullException(nameof(sendRequest));

            if (IsDisabled(methodName))
                return EmptyResult(methodName);

            var result = await sendRequest(methodParam).ConfigureAwait(false);
            return IsDisabled(methodName) ? EmptyResult(methodName) : result;
        }

        public Task HandleNotificationAsync(string methodName, JToken methodParam,
                                            Func<JToken, Task> sendNotification)
        {
            if (sendNotification == null)
                throw new ArgumentNullException(nameof(sendNotification));
            if (methodName == "textDocument/didClose")
            {
                if (Uri.TryCreate((string)methodParam?["textDocument"]?["uri"], UriKind.Absolute, out var uri))
                    InactiveRegionServices.Store.Remove(uri);
                return sendNotification(methodParam);
            }
            return sendNotification(methodParam);
        }

        private bool IsDisabled(string methodName)
        {
            var settings = _getSettings();
            if (methodName == "textDocument/completion" || methodName == "textDocument/hover")
                return !settings.EnableCompletionAndHover;
            if (methodName == "textDocument/formatting" || methodName == "textDocument/rangeFormatting")
                return !settings.EnableFormatting;
            if (methodName == "textDocument/codeAction" || methodName == "codeAction/resolve" ||
                methodName == "workspace/executeCommand")
                return !settings.EnableCodeActions;
            if (methodName.StartsWith("textDocument/semanticTokens/", StringComparison.Ordinal))
                return !settings.EnableSemanticHighlighting;
            return NavigationMethods.Contains(methodName) && !settings.EnableNavigation;
        }

        private static JToken EmptyResult(string methodName)
        {
            switch (methodName)
            {
                case "textDocument/completion":
                case "textDocument/codeAction":
                case "textDocument/formatting":
                case "textDocument/rangeFormatting":
                case "textDocument/references":
                case "textDocument/documentSymbol":
                case "workspace/symbol":
                    return new JArray();
                case "textDocument/semanticTokens/full":
                case "textDocument/semanticTokens/range":
                    return new JObject { ["data"] = new JArray() };
                case "textDocument/semanticTokens/full/delta":
                    return new JObject { ["data"] = new JArray() };
                case "codeAction/resolve":
                    return new JObject { ["title"] = "clangd 代码操作已关闭",
                        ["disabled"] = new JObject { ["reason"] = "clangd 代码操作已关闭" } };
                default:
                    return JValue.CreateNull();
            }
        }
    }
}
