using System;
using System.IO;
using System.Linq;
using System.Text;
using ClangdVS;
using Newtonsoft.Json.Linq;

internal static class ClangdLspMessageFilterRegression
{
    internal static void Run()
    {
        var streamType = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.LspMessageFilterStream");
        Assert(streamType != null, "LSP framing filter stream is missing");
        var input = Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":{}}") +
                    Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/inactiveRegions\",\"params\":{}}") +
                    Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/clangd.fileStatus\",\"params\":{}}") +
                    Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
        var settings = new ClangdSettings {
            EnableDiagnostics = false,
            EnableInactiveRegions = false,
        };
        var stream = (Stream)Activator.CreateInstance(
            streamType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            new object[] {
                new MemoryStream(Encoding.UTF8.GetBytes(input)),
                new Func<JObject, bool>(message =>
                {
                    var method = (string)message["method"];
                    if (method == "textDocument/publishDiagnostics")
                        return settings.EnableDiagnostics;
                    if (method == "textDocument/inactiveRegions")
                        return settings.EnableInactiveRegions;
                    return true;
                }),
            },
            null);

        using (stream)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[7];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, count);
            var messages = ReadFrames(Encoding.UTF8.GetString(output.ToArray())).ToArray();
            Assert(messages.Length == 2, "filter did not remove exactly the disabled diagnostic and inactive-region messages");
            Assert(messages[0].Contains("textDocument/clangd.fileStatus") && messages[1].Contains("result"),
                   "filter reordered or corrupted retained frames");
        }
    }

    private static string Frame(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return "Content-Length: " + bytes.Length + "\r\n\r\n" + json;
    }

    private static System.Collections.Generic.IEnumerable<string> ReadFrames(string data)
    {
        var remaining = data;
        while (remaining.Length > 0)
        {
            var separator = remaining.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert(separator >= 0, "retained LSP frame has no header terminator");
            var header = remaining.Substring(0, separator);
            var lengthText = header.Substring(header.IndexOf(':') + 1).Trim();
            var length = int.Parse(lengthText);
            var bodyStart = separator + 4;
            yield return remaining.Substring(bodyStart, length);
            remaining = remaining.Substring(bodyStart + length);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
