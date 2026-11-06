using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

// Run sequentially: temporarily substitutes the in-memory settings singleton, never calls Save.
// Program hook: await Check("real clangd protocol", ClangdRealProtocolRegression.RunAsync);
internal static class ClangdRealProtocolRegression
{
    private const string TempParent = @"C:\Users\admin\AppData\Local\Temp\opencode";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Source =
        "namespace probe {\n" +
        "struct Base { virtual ~Base() = default; };\n" +
        "struct Middle : Base {};\n" +
        "struct Leaf : Middle {};\n" +
        "int add(int left, int right) { return left + right; }\n" +
        "int run() { auto answer = add(4, 9); return answer; }\n" +
        "#if 0\n" +
        "int inactive_value = 1;\n" +
        "#endif\n" +
        "}\n";

    internal static async Task RunAsync()
    {
        var executable = FindClangd();
        if (executable == null)
        {
            Console.WriteLine("SKIP: real clangd protocol: clangd not installed in PATH or VS LLVM directories.");
            return;
        }
        Assert(Directory.Exists(TempParent), "approved temp parent is missing: " + TempParent);
        var directory = Path.Combine(TempParent, "clangd-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Console.WriteLine("PROBE: clangd=" + executable + "; fixture=" + directory);
            await PrintVersionAsync(executable, directory);
            // No includes, no SDK/compiler discovery, no user/project .clangd configuration.
            File.WriteAllText(Path.Combine(directory, "compile_flags.txt"), "-xc++\n-std=c++17\n", new UTF8Encoding(false));
            var failures = new List<string>();
            using (var settings = new SettingsScope())
            {
                await SessionAsync(executable, directory, settings.Value, true, failures);
                await SessionAsync(executable, directory, settings.Value, false, failures);
            }
            Assert(failures.Count == 0, string.Join(Environment.NewLine, failures));
            Console.WriteLine("PASS: real clangd protocol core; see individual PASS/UNSUPPORTED lines for extensions.");
        }
        finally
        {
            // This GUID directory belongs exclusively to this invocation. Never delete the parent.
            Directory.Delete(directory, true);
        }
    }

    private static async Task SessionAsync(string executable, string directory, ClangdSettings settings, bool enabled,
        List<string> failures)
    {
        settings.EnableInactiveRegions = enabled;
        var path = Path.Combine(directory, enabled ? "enabled.cpp" : "disabled.cpp");
        File.WriteAllText(path, Source, new UTF8Encoding(false));
        var uri = new Uri(path);
        var diagnostics = new DiagnosticsTarget(uri.AbsoluteUri);
        var bindingTrace = new BindingTrace();
        var process = new Process { StartInfo = StartInfo(executable, directory,
            "--enable-config=false --background-index=false --clang-tidy=false --pch-storage=memory --log=error --compile-commands-dir=\"" + directory + "\"") };
        JsonRpc rpc = null;
        Stream initializeStream = null;
        Task<string> stderr = null;
        PipeTextReader stderrReader = null;
        WireStream sent = null;
        WireStream received = null;
        var initializeFinished = new ManualResetEvent(false);
        Thread watchdog = null;
        var client = new ClangdLanguageClient();
        var rpcField = typeof(ClangdLanguageClient).GetField("_rpc", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(rpcField != null, "client _rpc field missing");
        var previousRpc = rpcField.GetValue(client);
        try
        {
            StartProcess(process);
            stderrReader = new PipeTextReader(process.StandardError);
            stderr = stderrReader.Completion;
            sent = new WireStream(process.StandardInput.BaseStream);
            received = new WireStream(process.StandardOutput.BaseStream);
            initializeStream = new LspInitializeStream(sent, () => ClangdSettings.Instance.EnableInactiveRegions);
            // Explicit Newtonsoft formatter, matching the old-middleware production contract.
            rpc = new JsonRpc(new HeaderDelimitedMessageHandler(initializeStream, received, new JsonMessageFormatter()));
            rpc.TraceSource.Switch.Level = SourceLevels.Information;
            rpc.TraceSource.Listeners.Add(bindingTrace);
            rpc.AddLocalRpcTarget(new ClangdLanguageClient.ClangdCustomTarget());
            rpc.AddLocalRpcTarget(diagnostics);
            rpc.StartListening();
            rpcField.SetValue(client, rpc);
            // A pool-backed timer cannot report starvation while all workers are in pipe reads.
            watchdog = new Thread(() => {
                if (!initializeFinished.WaitOne(Deadline))
                    DumpSession(process, rpc, sent, received, stderrReader, bindingTrace);
            }) { IsBackground = true, Name = "clangd probe watchdog" };
            watchdog.Start();

            var result = await Timed("initialize", token => rpc.InvokeWithParameterObjectAsync<JToken>("initialize", new {
                processId = Process.GetCurrentProcess().Id,
                rootUri = new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri,
                capabilities = new { general = new { positionEncodings = new[] { "utf-16" } },
                    textDocument = new { publishDiagnostics = new { versionSupport = true } } },
                initializationOptions = client.InitializationOptions,
            }, token));
            initializeFinished.Set();
            Assert(result?["capabilities"] is JObject, "initialize did not return capabilities");
            var wireInitialize = sent.Messages().Single(message => (string)message["method"] == "initialize");
            Assert((bool?)wireInitialize.SelectToken("params.capabilities.textDocument.inactiveRegionsCapabilities.inactiveRegions") == enabled,
                "production initialize stream failed to inject the actual capability");
            Assert((string)wireInitialize.SelectToken("params.capabilities.general.positionEncodings[0]") == "utf-16",
                "initialize injection lost existing capabilities");
            Console.WriteLine("PASS: real initialize capability injection inactiveRegions=" + enabled +
                "; server=" + result["serverInfo"]?.ToString(Newtonsoft.Json.Formatting.None));
            await NotifyAsync(rpc, "initialized", new { });
            await NotifyAsync(rpc, "textDocument/didOpen", new {
                textDocument = new { uri = uri.AbsoluteUri, languageId = "cpp", version = 1, text = Source },
            });
            await Timed("didOpen diagnostics", token => diagnostics.First.Task);
            AssertNoErrors(diagnostics.First.Task.Result);

            if (enabled)
            {
                await WaitUntilAsync("inactiveRegions wire notification", () => received.Messages().Any(message =>
                    (string)message["method"] == "textDocument/inactiveRegions"));
                var notification = received.Messages().Last(message => (string)message["method"] == "textDocument/inactiveRegions");
                Console.WriteLine("  inactiveRegions wire=" + notification.ToString(Newtonsoft.Json.Formatting.None));
                await CheckAsync("inactiveRegions actual production single-object RPC dispatch", async () => {
                    await WaitUntilAsync("inactiveRegions RPC target dispatch", () => bindingTrace.Snapshot().Contains(
                        "Invoking ClangdVS.ClangdLanguageClient+ClangdCustomTarget.textDocument/inactiveRegions(JObject)"));
                    Console.WriteLine("PASS: binder dispatch: ClangdCustomTarget.textDocument/inactiveRegions(JObject)");
                }, failures);
                var populated = false;
                await CheckAsync("inactiveRegions production single-object binder -> Store", async () => {
                    var payload = notification["params"];
                    Assert(((string)payload?["uri"] ?? (string)payload?["textDocument"]?["uri"]) == uri.AbsoluteUri,
                        "inactiveRegions wire URI mismatch");
                    await WaitUntilAsync("inactiveRegions production single-object binder", () =>
                        InactiveRegionServices.Store.Get(uri).Count > 0);
                    AssertRegionsMatch(notification["params"]?["regions"], InactiveRegionServices.Store.Get(uri));
                    Assert(InactiveRegionServices.Store.Get(uri).Any(range => range.Item1 <= 7 && range.Item3 >= 7),
                        "inactive-region range does not cover the disabled fixture line");
                    populated = true;
                    Console.WriteLine("PASS: inactiveRegions #if 0 range coordinates in production Store");
                }, failures, "wire URI fields: " + notification["params"].ToString(Newtonsoft.Json.Formatting.None));

                var gaps = new List<string>();
                var position = Position("struct Middle", "struct ".Length);
                await ExtensionAsync("AST via RequestAstAsync", async token => {
                    var ast = await client.RequestAstAsync(uri.AbsoluteUri, position.Item1, position.Item2, token);
                    Assert(ast is JObject && !string.IsNullOrEmpty((string)ast["kind"]) &&
                        !string.IsNullOrEmpty((string)ast["role"]) && ast["range"] is JObject,
                        "AST response lacks kind/role/range");
                    Assert(ast.ToString().Contains("Middle"), "AST does not describe fixture Middle");
                    Console.WriteLine("  AST kind=" + ast["kind"] + "; role=" + ast["role"]);
                }, gaps, failures);
                await ExtensionAsync("symbolInfo via RequestSymbolInfoAsync", async token => {
                    var symbol = Position("auto answer = add", "auto answer = ".Length);
                    var info = await client.RequestSymbolInfoAsync(uri.AbsoluteUri, symbol.Item1, symbol.Item2, token);
                    var details = info is JArray array ? array.OfType<JObject>().ToArray() : new[] { info as JObject };
                    Assert(details.Any(item => (string)item?["name"] == "add" && !string.IsNullOrEmpty((string)item["usr"])),
                        "symbolInfo did not resolve fixture add with a USR");
                    Console.WriteLine("  symbolInfo=" + info.ToString(Newtonsoft.Json.Formatting.None));
                }, gaps, failures);
                await ExtensionAsync("inlayHints via RequestInlayHintsAsync (clangd/inlayHints)", async token => {
                    var hints = await client.RequestInlayHintsAsync(uri.AbsoluteUri, 0, Source.Split('\n').Length - 1, token);
                    Assert(hints is JArray values && values.Count > 0, "fixture produced no inlay hints");
                    Assert(hints.Children<JObject>().Any(hint => hint["position"] is JObject &&
                        ((string)hint["label"] ?? "").Contains("left")), "parameter hint left missing");
                    Console.WriteLine("  inlayHints=" + hints.ToString(Newtonsoft.Json.Formatting.None));
                }, gaps, failures);
                await ExtensionAsync("type hierarchy via RequestTypeHierarchyAsync", async token => {
                    var hierarchy = await client.RequestTypeHierarchyAsync(uri.AbsoluteUri, position.Item1, position.Item2, token);
                    Assert(hierarchy is JArray roots && roots.Count == 1, "expected one hierarchy root");
                    var root = hierarchy[0];
                    Assert((string)root["item"]?["name"] == "Middle", "hierarchy root is not Middle");
                    Assert(root["supertypes"].Any(item => (string)item["name"] == "Base"), "Middle superclass Base missing");
                    Assert(root["subtypes"].Any(item => (string)item["name"] == "Leaf"), "Middle subclass Leaf missing");
                    Assert((string)root["item"]?["uri"] == uri.AbsoluteUri, "hierarchy root URI mismatch");
                    Console.WriteLine("  hierarchy=Base <- Middle <- Leaf");
                }, gaps, failures);

                // Real replacement notification must retire the previous nonempty store entry.
                await NotifyAsync(rpc, "textDocument/didChange", new {
                    textDocument = new { uri = uri.AbsoluteUri, version = 2 }, wantDiagnostics = true,
                    contentChanges = new[] { new { text = Source.Replace("#if 0", "#if 1") } },
                });
                await Timed("didChange diagnostics", token => diagnostics.Second.Task);
                AssertNoErrors(diagnostics.Second.Task.Result);
                await WaitUntilAsync("inactiveRegions empty replacement", () =>
                    received.Messages().Any(message => (string)message["method"] == "textDocument/inactiveRegions" &&
                        message["params"]?["regions"] is JArray regions && regions.Count == 0) &&
                    InactiveRegionServices.Store.Get(uri).Count == 0);
                Console.WriteLine(populated
                    ? "PASS: didChange #if 1 clears production inactive-region Store"
                    : "GAP: didChange produced empty inactiveRegions; Store replacement unverified because initial population failed");
                if (gaps.Count != 0)
                    Console.WriteLine("GAP: this clangd does not support client RPCs: " + string.Join(", ", gaps));
            }

            await NotifyAsync(rpc, "textDocument/didClose", new { textDocument = new { uri = uri.AbsoluteUri } });
            await Timed("shutdown", token => rpc.InvokeWithParameterObjectAsync<JToken>("shutdown", null, token));
            await NotifyAsync(rpc, "exit", null);
            await WaitUntilAsync("clangd process exit", () => process.HasExited);
            Assert(process.ExitCode == 0, "clangd exit code=" + process.ExitCode);
            if (!enabled)
            {
                Assert(!received.Messages().Any(message => (string)message["method"] == "textDocument/inactiveRegions"),
                    "disabled initialize capability still produced inactiveRegions");
                Assert(InactiveRegionServices.Store.Get(uri).Count == 0, "disabled fixture populated the Store");
                Console.WriteLine("PASS: disabled capability: didOpen parsed successfully, no inactiveRegions through shutdown");
            }
        }
        catch (Exception error)
        {
            Console.WriteLine("SESSION FAILURE: inactiveRegions=" + enabled + "; " + error);
            if (stderrReader != null)
                DumpSession(process, rpc, sent, received, stderrReader, bindingTrace);
            throw;
        }
        finally
        {
            initializeFinished.Set();
            watchdog?.Join();
            initializeFinished.Dispose();
            rpcField.SetValue(client, previousRpc);
            // Kill before disposing pipe wrappers: a stuck write must not hold their dispose gate.
            try
            {
                if (stderr != null && !process.HasExited)
                {
                    process.Kill();
                    Assert(process.WaitForExit(5000), "clangd did not exit after Kill");
                }
                if (stderr != null)
                {
                    var text = await Timed("stderr drain", token => stderr);
                    if (!string.IsNullOrWhiteSpace(text)) Console.WriteLine("clangd stderr: " + text.Trim());
                }
            }
            finally
            {
                try { rpc?.Dispose(); }
                finally
                {
                    try { initializeStream?.Dispose(); }
                    finally
                    {
                        try { received?.Dispose(); }
                        finally
                        {
                            process.Dispose();
                            bindingTrace.Dispose();
                            InactiveRegionServices.Store.Remove(uri);
                        }
                    }
                }
            }
        }
    }

    private static void DumpSession(Process process, JsonRpc rpc, WireStream sent, WireStream received,
        PipeTextReader stderr, BindingTrace trace)
    {
        ThreadPool.GetAvailableThreads(out var workers, out var io);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        Console.WriteLine("SESSION EVIDENCE: pid=" + process.Id + "; exited=" + process.HasExited +
            "; rpc.Completion=" + rpc?.Completion.Status + "; rpc failure=" + rpc?.Completion.Exception +
            "; stderr=" + stderr.Completion.Status +
            "; pool available/max workers=" + workers + "/" + maxWorkers + "; IO=" + io + "/" + maxIo);
        Console.WriteLine("SENT: " + sent?.Snapshot());
        Console.WriteLine("RECEIVED: " + received?.Snapshot());
        Console.WriteLine("STDERR: " + stderr.Snapshot());
        Console.WriteLine("RPC TRACE: " + trace.Snapshot());
        Console.WriteLine("ENCODING: console stdin=" + Console.InputEncoding.WebName + "; preamble=" +
            BitConverter.ToString(Console.InputEncoding.GetPreamble()) + "; process stdin=" +
            process.StandardInput.Encoding.WebName + "; preamble=" + BitConverter.ToString(process.StandardInput.Encoding.GetPreamble()));
    }

    private static void AssertRegionsMatch(JToken regions, IReadOnlyList<Tuple<int, int, int, int>> stored)
    {
        Assert(regions is JArray && regions.Count() == stored.Count, "binder lost inactive-region ranges");
        for (var i = 0; i < stored.Count; i++)
        {
            var range = regions[i];
            Assert(Tuple.Create((int)range["start"]["line"], (int)range["start"]["character"],
                (int)range["end"]["line"], (int)range["end"]["character"]).Equals(stored[i]),
                "production binder altered range coordinates");
        }
    }

    private static Tuple<int, int> Position(string marker, int offset)
    {
        var lines = Source.Split('\n');
        var line = Array.FindIndex(lines, value => value.Contains(marker));
        Assert(line >= 0, "missing fixture marker " + marker);
        return Tuple.Create(line, lines[line].IndexOf(marker, StringComparison.Ordinal) + offset);
    }

    private static Task ExtensionAsync(string name, Func<CancellationToken, Task> check, List<string> gaps,
        List<string> failures) => CheckAsync(name, async () =>
    {
        try
        {
            await Timed(name, async token => { await check(token); return true; });
        }
        catch (RemoteInvocationException error) when (error.ErrorCode == -32601)
        {
            gaps.Add(name);
            Console.WriteLine("UNSUPPORTED: " + name + ": -32601 " + error.Message);
            return;
        }
        Console.WriteLine("PASS: " + name);
    }, failures);

    private static async Task CheckAsync(string name, Func<Task> check, List<string> failures, string evidence = null)
    {
        try { await check(); }
        catch (Exception error)
        {
            var failure = name + ": " + error.GetType().Name + ": " + error.Message +
                (evidence == null ? "" : "; " + evidence);
            failures.Add(failure);
            Console.WriteLine("FAIL: " + failure);
        }
    }

    private static async Task<T> Timed<T>(string name, Func<CancellationToken, Task<T>> operation)
    {
        using (var cancellation = new CancellationTokenSource(Deadline))
        {
            var task = operation(cancellation.Token);
            var expired = Task.Delay(Deadline);
            if (await Task.WhenAny(task, expired).ConfigureAwait(false) != task)
            {
                cancellation.Cancel();
                // Observe a late failure even if an underlying API ignores cancellation.
                _ = task.ContinueWith(faulted => { var ignored = faulted.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw new TimeoutException(name + " exceeded " + Deadline.TotalSeconds + "s");
            }
            try { return await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { throw new TimeoutException(name + " exceeded " + Deadline.TotalSeconds + "s"); }
        }
    }

    private static Task NotifyAsync(JsonRpc rpc, string method, object parameters) =>
        Timed(method, async token => { await rpc.NotifyWithParameterObjectAsync(method, parameters); return true; });

    private static Task WaitUntilAsync(string name, Func<bool> predicate) => Timed(name, async token => {
        while (!predicate()) await Task.Delay(25, token).ConfigureAwait(false);
        return true;
    });

    private static void AssertNoErrors(JObject notification)
    {
        Assert(notification["diagnostics"] is JArray, "missing diagnostics array");
        Assert(!notification["diagnostics"].Any(item => (int?)item["severity"] == 1),
            "fixture C++ parse errors: " + notification.ToString(Newtonsoft.Json.Formatting.None));
    }

    private sealed class DiagnosticsTarget
    {
        private readonly string _uri;
        internal readonly TaskCompletionSource<JObject> First = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<JObject> Second = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DiagnosticsTarget(string uri) { _uri = uri; }

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void OnDiagnostics(JObject payload)
        {
            if ((string)payload?["uri"] != _uri) return;
            if ((int?)payload["version"] == 1) First.TrySetResult(payload);
            if ((int?)payload["version"] == 2) Second.TrySetResult(payload);
        }
    }

    private sealed class BindingTrace : TraceListener
    {
        private readonly StringBuilder _text = new StringBuilder();
        public override void Write(string message) { }
        public override void WriteLine(string message) { }
        public override void TraceEvent(TraceEventCache cache, string source, TraceEventType eventType,
            int id, string format, params object[] args)
        {
            var text = args == null ? format : string.Format(CultureInfo.InvariantCulture, format, args);
            if (text == null) return;
            lock (_text) { if (_text.Length < 64000) _text.Append(id).Append(": ").Append(text).Append(" | "); }
        }
        public override void TraceEvent(TraceEventCache cache, string source, TraceEventType eventType,
            int id, string message) => TraceEvent(cache, source, eventType, id, message, null);
        public override void TraceData(TraceEventCache cache, string source, TraceEventType eventType,
            int id, params object[] data) => TraceEvent(cache, source, eventType, id,
                string.Join("; ", data.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))), null);
        public override void TraceData(TraceEventCache cache, string source, TraceEventType eventType,
            int id, object data) => TraceData(cache, source, eventType, id, new[] { data });
        internal string Snapshot() { lock (_text) return _text.ToString(); }
    }

    private sealed class SettingsScope : IDisposable
    {
        private static readonly Type Type = typeof(ClangdSettings);
        private readonly FieldInfo _instance = Type.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly FieldInfo _watcher = Type.GetField("_watcher", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly object _gate = Type.GetField("WatcherGate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        private readonly object _previousInstance;
        private readonly object _previousWatcher;
        private readonly bool _previousWatcherEnabled;
        // An unstarted watcher prevents Instance from creating a directory/watcher in user settings.
        private readonly FileSystemWatcher _sentinel = new FileSystemWatcher();
        internal readonly ClangdSettings Value = new ClangdSettings {
            EnableInlayHints = true, EnableAstView = true, EnableSymbolInfo = true, EnableTypeHierarchy = true,
            FallbackFlags = new[] { "-xc++", "-std=c++17" },
        };

        internal SettingsScope()
        {
            lock (_gate)
            {
                _previousInstance = _instance.GetValue(null);
                _previousWatcher = _watcher.GetValue(null);
                if (_previousWatcher is FileSystemWatcher watcher)
                {
                    _previousWatcherEnabled = watcher.EnableRaisingEvents;
                    watcher.EnableRaisingEvents = false;
                }
                _watcher.SetValue(null, _sentinel);
                _instance.SetValue(null, Value);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _instance.SetValue(null, _previousInstance);
                _watcher.SetValue(null, _previousWatcher);
                if (_previousWatcher is FileSystemWatcher watcher)
                    watcher.EnableRaisingEvents = _previousWatcherEnabled;
                _sentinel.Dispose();
            }
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string directory, string arguments) => new ProcessStartInfo {
        FileName = executable, Arguments = arguments, WorkingDirectory = directory,
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };

    private static void StartProcess(Process process)
    {
        // net472 Process.Start constructs StandardInput with Console.InputEncoding and
        // sets AutoFlush=true. That flush can emit a BOM BEFORE we wrap BaseStream:
        // pwsh's UTF-8 console then turns "Content-Length" into "\uFEFFContent-Length",
        // which clangd discards. Our later wire capture cannot see this hidden prefix.
        // net472 has no ProcessStartInfo.StandardInputEncoding. This sequential probe
        // scopes the console change to Start only (no await) and restores even on failure.
        var previous = Console.InputEncoding;
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Assert(process.Start(), "clangd process did not start");
        }
        finally { Console.InputEncoding = previous; }
        Assert(process.StandardInput.Encoding.GetPreamble().Length == 0,
            "Process.Start emitted an encoding preamble before the LSP wire capture");
    }

    private static async Task PrintVersionAsync(string executable, string directory)
    {
        using (var process = new Process { StartInfo = StartInfo(executable, directory, "--version") })
        {
            Task<string> output = null;
            Task<string> error = null;
            try
            {
                StartProcess(process);
                output = new PipeTextReader(process.StandardOutput).Completion;
                error = new PipeTextReader(process.StandardError).Completion;
                Console.WriteLine((await Timed("clangd --version stdout", token => output)).Trim());
                var text = await Timed("clangd --version stderr", token => error);
                if (!string.IsNullOrWhiteSpace(text)) Console.WriteLine(text.Trim());
                await WaitUntilAsync("clangd --version exit", () => process.HasExited);
                Assert(process.ExitCode == 0, "clangd --version failed");
            }
            finally
            {
                if (output != null && !process.HasExited)
                {
                    process.Kill();
                    Assert(process.WaitForExit(5000), "clangd --version did not exit after Kill");
                }
            }
        }
    }

    // Process creates synchronous anonymous pipes on net472. ReadToEndAsync on these
    // pipes blocks a pool worker until exit, just like FileStream.ReadAsync below.
    private sealed class PipeTextReader
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly TaskCompletionSource<string> _completion =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<string> Completion => _completion.Task;
        internal string Snapshot() { lock (_text) return _text.ToString(); }

        internal PipeTextReader(TextReader reader)
        {
            new Thread(() => {
                try
                {
                    var buffer = new char[4096];
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
                        lock (_text)
                        {
                            // Continue draining even when the diagnostic capture is full.
                            _text.Append(buffer, 0, Math.Min(count, 64000 - _text.Length));
                        }
                    _completion.TrySetResult(Snapshot());
                }
                catch (Exception error) { _completion.TrySetException(error); }
            }) { IsBackground = true, Name = "clangd probe text drain" }.Start();
        }
    }

    private static string FindClangd()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')), "clangd.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        var installations = new List<string>();
        var vs = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrWhiteSpace(vs)) installations.Add(vs);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
        {
            var parent = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(parent)) continue;
            foreach (var year in Directory.GetDirectories(parent))
                installations.AddRange(Directory.GetDirectories(year));
        }
        foreach (var installation in installations.Distinct())
            foreach (var relative in new[] { @"VC\Tools\Llvm\x64\bin\clangd.exe", @"VC\Tools\Llvm\bin\clangd.exe" })
            {
                var candidate = Path.Combine(installation, relative);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    // Captures actual framed bytes on either side of the production initialize stream.
    // Bounded, synchronized snapshots also distinguish an absent notification from a binder failure.
    private sealed class WireStream : Stream
    {
        private readonly Stream _inner;
        private readonly MemoryStream _wire = new MemoryStream();
        private readonly BlockingCollection<Action> _operations = new BlockingCollection<Action>();
        private readonly Thread _worker;
        private volatile string _operation = "idle";
        private int _disposed;
        internal WireStream(Stream inner)
        {
            _inner = inner;
            // One owned thread per pipe, not Task.Run/FileStream's sync-over-async
            // worker pool. Idle stdout must not prevent the initialize write from running.
            _worker = new Thread(() => {
                foreach (var operation in _operations.GetConsumingEnumerable()) operation();
            }) { IsBackground = true, Name = "clangd probe wire I/O" };
            _worker.Start();
        }
        private Task<T> Queue<T>(Func<T> operation, CancellationToken token)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _operations.Add(() => {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        completion.TrySetResult(operation());
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { completion.TrySetCanceled(); }
                    catch (Exception error) { completion.TrySetException(error); }
                });
            }
            catch (InvalidOperationException) { completion.TrySetException(new ObjectDisposedException(nameof(WireStream))); }
            return completion.Task;
        }
        internal string Snapshot()
        {
            lock (_wire) return "stream=" + _inner.GetType().FullName + "; async=" +
                (_inner is FileStream file ? file.IsAsync.ToString() : "unknown") +
                "; operation=" + _operation + "; bytes=" + _wire.Length + "\n" + Encoding.UTF8.GetString(_wire.ToArray());
        }
        private void Record(byte[] buffer, int offset, int count)
        {
            lock (_wire)
            {
                if (_wire.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("probe wire capture exceeded 4 MiB");
                _wire.Write(buffer, offset, count);
            }
        }
        internal IReadOnlyList<JObject> Messages()
        {
            byte[] bytes;
            lock (_wire) bytes = _wire.ToArray();
            var messages = new List<JObject>();
            var offset = 0;
            while (offset < bytes.Length)
            {
                var end = offset;
                while (end + 3 < bytes.Length && !(bytes[end] == 13 && bytes[end + 1] == 10 && bytes[end + 2] == 13 && bytes[end + 3] == 10)) end++;
                if (end + 3 >= bytes.Length) break; // concurrent read may have only a partial frame
                var header = Encoding.ASCII.GetString(bytes, offset, end - offset);
                var length = int.Parse(header.Split(new[] { "\r\n" }, StringSplitOptions.None)
                    .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Substring("Content-Length:".Length).Trim(), CultureInfo.InvariantCulture);
                offset = end + 4;
                Assert(length >= 0, "negative captured frame length");
                if (length > bytes.Length - offset) break;
                messages.Add(JObject.Parse(Encoding.UTF8.GetString(bytes, offset, length)));
                offset += length;
            }
            return messages;
        }
        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = _inner.Read(buffer, offset, count); Record(buffer, offset, read); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => Queue(() =>
        {
            _operation = "Read pending count=" + count;
            var read = Read(buffer, offset, count);
            _operation = "Read completed count=" + read; return read;
        }, token);
        public override void Write(byte[] buffer, int offset, int count)
        { _inner.Write(buffer, offset, count); Record(buffer, offset, count); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => Queue(() =>
        {
            _operation = "Write pending count=" + count;
            Write(buffer, offset, count);
            _operation = "Write completed count=" + count; return true;
        }, token);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken token) => Queue(() => {
            _operation = "Flush pending";
            Flush(); _operation = "Flush completed"; return true;
        }, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Session teardown exits/kills clangd first, releasing blocked reads/writes.
                _operations.CompleteAdding();
                Assert(_worker.Join(5000), "wire I/O thread did not stop after clangd exit");
                _inner.Dispose();
                _operations.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("real clangd protocol: " + message);
    }
}
