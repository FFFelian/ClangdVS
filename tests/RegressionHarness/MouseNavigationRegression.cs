using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClangdVS;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;
using Moq;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

internal static class MouseNavigationRegression
{
    internal static async Task RunAsync()
    {
        var completion = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
                                {
                                    try
                                    {
                                        Run();
                                        completion.SetResult(true);
                                    }
                                    catch (Exception ex)
                                    {
                                        completion.SetException(ex);
                                    }
                                });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
        await DefinitionRpcAsync();
    }

    private static async Task DefinitionRpcAsync()
    {
        var settings = ClangdSettings.Instance;
        var previousTakeover = settings.TakeOverFromNativeIntelliSense;
        settings.TakeOverFromNativeIntelliSense = true;
        try
        {
            await DefinitionRpcCoreAsync();
        }
        finally
        {
            settings.TakeOverFromNativeIntelliSense = previousTakeover;
        }
    }

    private static async Task DefinitionRpcCoreAsync()
    {
        var pipeName = "ClangdMouseRegression-" + Guid.NewGuid().ToString("N");
        using (var serverPipe = new NamedPipeServerStream(
                   pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                   PipeOptions.Asynchronous)) using (var clientPipe =
                                                         new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                                                                                   PipeOptions.Asynchronous))
        {
            var accept = serverPipe.WaitForConnectionAsync();
            await clientPipe.ConnectAsync(5000);
            await accept;
            var target = new DefinitionTarget();
            using (var server = JsonRpc.Attach(serverPipe, target)) using (var rpc = JsonRpc.Attach(clientPipe))
            {
                var client = new ClangdLanguageClient();
                typeof(ClangdLanguageClient)
                    .GetField("_rpc", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(client, rpc);
                var response = await client.GetDefinitionAsync("file:///D:/project/mouse.cpp", 1, 8);
                Assert((string)response[0]["uri"] == "file:///D:/definition.cpp" &&
                           target.Uri == "file:///D:/project/mouse.cpp" && target.Line == 1 && target.Character == 8,
                       "RPC definition changed source URI/UTF-16 position or response");
                target.Pending = true;
                using (var cancel = new CancellationTokenSource())
                {
                    var query = client.GetDefinitionAsync("file:///D:/project/mouse.cpp", 1, 8, cancel.Token);
                    Assert(await Task.WhenAny(target.Started.Task, Task.Delay(5000)) == target.Started.Task,
                           "RPC query was not dispatched");
                    cancel.Cancel();
                    Assert(await Task.WhenAny(query, Task.Delay(5000)) == query,
                           "RPC cancellation did not finish request");
                    try
                    {
                        await query;
                        throw new Exception("cancelled definition was swallowed as an empty result");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    Assert(await Task.WhenAny(target.Cancelled.Task, Task.Delay(5000)) == target.Cancelled.Task,
                           "server did not receive RPC cancellation");
                }
            }
        }
        Console.WriteLine("PASS: real JSON-RPC definition source position and server cancellation");
    }

    public sealed class DefinitionTarget
    {
        public string Uri;
        public int Line, Character;
        public bool Pending;
        public readonly TaskCompletionSource<bool> Started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Cancelled =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonRpcMethod("textDocument/definition")]
        public async Task<JToken> DefinitionAsync(JObject textDocument, JObject position,
                                                  CancellationToken cancellationToken)
        {
            Uri = (string)textDocument["uri"];
            Line = (int)position["line"];
            Character = (int)position["character"];
            if (Pending)
            {
                Started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult(true);
                    throw;
                }
            }
            return JToken.Parse("[{'uri':'file:///D:/definition.cpp','range':{'start':{'line':4,'character':7}}}]");
        }
    }

    private static void Run()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdMouseNavigation");
        Assert(type != null, "Ctrl+click mouse navigation handler is missing");

        SelectionAdornmentRegression(type);

        try
        {
            using (var f = new Fixture(type, failAttachDiagnostics: true))
            {
            }
            throw new Exception("constructor diagnostic failure was not exercised");
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is InvalidOperationException && ex.InnerException.Message == "options unavailable")
        {
        }
        Console.WriteLine("PASS: failed handler construction leaves no subscribed events for scan retry");

        using (var f = new Fixture(type))
        {
            f.Down();
            f.Options.Raise(o => o.OptionChanged += null,
                            new EditorOptionChangedEventArgs(DefaultWpfViewOptions.ZoomLevelName));
            f.Up();
            Assert(f.Requests == 1, "unrelated editor option change cancelled a Ctrl+click candidate");
        }
        Console.WriteLine("PASS: unrelated option change preserves gesture");
        using (var f = new Fixture(type))
        {
            f.Element.HandlePreviewUp = true;
            f.RoutedClick(f.Element);
            Assert(f.Requests == 0, "already handled mouse up must retain owner behavior");
            var diagnostic = System.IO.File.ReadAllText(
                System.IO.Path.Combine(Environment.GetEnvironmentVariable("CLANGDVS_LOG_DIRECTORY"), "clangd.log"));
            Assert(diagnostic.Contains("mouse gesture") && diagnostic.Contains("stage=up") &&
                       diagnostic.Contains("handled=True"),
                   "handled-events-too mouse up rejection diagnostic was not recorded");
        }
        Console.WriteLine("PASS: class-handled PreviewMouseUp observed diagnostically and passed through");
        using (var f = new Fixture(type))
        {
            f.Element.HandlePreviewDown = true;
            f.RoutedClick(f.Element);
            Assert(f.Requests == 0, "class-handled down must retain owner behavior");
        }
        using (var f = new Fixture(type))
        {
            var logPath =
                System.IO.Path.Combine(Environment.GetEnvironmentVariable("CLANGDVS_LOG_DIRECTORY"), "clangd.log");
            var before = System.IO.File.ReadAllText(logPath);
            f.Keys = ModifierKeys.None;
            f.RoutedClick(f.Element);
            Assert(System.IO.File.ReadAllText(logPath) == before, "ordinary click produced gesture diagnostic");
            f.Keys = ModifierKeys.Control;
            f.Classification = "comment";
            f.RoutedClick(f.Element);
            var first = System.IO.File.ReadAllText(logPath);
            Assert(first.Contains("rejected=excluded-classification") && first.Contains("classification=comment"),
                   "hit rejection details missing");
            f.RoutedClick(f.Element);
            Assert(System.IO.File.ReadAllText(logPath) == first, "rapid rejected Ctrl gestures were not rate limited");
        }
        foreach (var option in new[] { DefaultWpfViewOptions.ClickGoToDefEnabledName,
                                       DefaultWpfViewOptions.ClickGoToDefModifierKeyName,
                                       DefaultWpfViewOptions.ClickGoToDefOpensPeekName })
            using (var f = new Fixture(type))
            {
                f.Down();
                f.Options.Raise(o => o.OptionChanged += null, new EditorOptionChangedEventArgs(option));
                f.Up();
                Assert(f.Requests == 0, "navigation option change failed to cancel gesture");
            }
        Console.WriteLine(
            "PASS: handled down / Ctrl-only bounded rejection diagnostics / navigation option invalidation");
        NativeInputRegression(type);

        using (var f = new Fixture(type))
        {
            f.Down();
            Assert(!f.Handled, "down consumed editor selection/focus");
            f.Up();
            Assert(f.Requests == 1 && f.Handled && f.HandledBeforeQuery,
                   "up must be handled synchronously before query");
            Assert(f.Path == @"D:\project\mouse.cpp" && f.QueryLine == 1 && f.QueryCharacter == 8,
                   "caret A / hit B, tab, Chinese and emoji must use mapped source UTF-16 position");
            Assert(f.HitX == 130 && f.HitY == 240, "viewport offsets missing from hit test");
            Assert(f.Navigated == 1 && f.Validity(), "valid request did not navigate");
            f.View.VerifyGet(v => v.Caret, Times.Never);
            f.Graph.Verify(g => g.MapDownToBuffer(It.IsAny<SnapshotPoint>(), PointTrackingMode.Positive,
                                                  f.Buffer.Object, PositionAffinity.Successor),
                           Times.Exactly(2));
            Console.WriteLine("PASS: mouse hit B / UTF-16 / scroll / projection / handled ordering");
        }

        foreach (var invalid in new[] { "noctrl",      "shift",       "right",     "double",   "drag",
                                        "drag-return", "other-token", "edit",      "white",    "punctuation",
                                        "comment",     "string",      "adornment", "line-end", "nested",
                                        "off",         "peek",        "modifier",  "disabled", "noncpp",
                                        "no-document", "no-map" })
            using (var f = new Fixture(type))
            {
                switch (invalid)
                {
                case "noctrl":
                    f.Keys = ModifierKeys.None;
                    break;
                case "shift":
                    f.Keys = ModifierKeys.Control | ModifierKeys.Shift;
                    break;
                case "right":
                    f.Button = MouseButton.Right;
                    break;
                case "double":
                    f.Clicks = 2;
                    break;
                case "white":
                    f.Hit = 6;
                    break;
                case "punctuation":
                    f.Hit = 11;
                    break;
                case "comment":
                    f.Classification = "comment";
                    break;
                case "string":
                    f.Classification = "string";
                    break;
                case "adornment":
                case "line-end":
                    f.HasHit = false;
                    break;
                case "nested":
                    f.TextSurface = false;
                    break;
                case "off":
                    f.Takeover = false;
                    break;
                case "peek":
                    f.Peek = true;
                    break;
                case "modifier":
                    f.ConfiguredModifier = ModifierKeys.Alt;
                    break;
                case "disabled":
                    f.ClickEnabled = false;
                    break;
                case "noncpp":
                    f.Cpp = false;
                    break;
                case "no-document":
                    f.HasDocument = false;
                    break;
                case "no-map":
                    f.HasMapping = false;
                    break;
                }
                f.Down();
                if (invalid == "drag")
                    f.UpPoint = new Point(30 + SystemParameters.MinimumHorizontalDragDistance + 1, 40);
                if (invalid == "drag-return")
                    f.Move(new Point(30 + SystemParameters.MinimumHorizontalDragDistance + 1, 40));
                if (invalid == "other-token")
                    f.Hit = 14;
                if (invalid == "edit")
                    f.CurrentSnapshot = new Mock<ITextSnapshot>().Object;
                f.Up();
                Assert(f.Requests == 0 && !f.Handled, invalid + " must pass through without query");
            }
        Console.WriteLine("PASS: unrelated clicks / drag-return / token change / classifications / editor options");

        foreach (var reason in new[] { "ordinary-click", "new-ctrl", "closed", "lost-focus", "edit", "off", "options" })
            using (var f = new Fixture(type))
            {
                f.Pending = new TaskCompletionSource<bool>();
                f.Down();
                f.Up();
                Assert(f.Requests == 1 && f.Navigated == 0, "pending query setup failed");
                switch (reason)
                {
                case "ordinary-click":
                    f.Keys = ModifierKeys.None;
                    f.Down();
                    break;
                case "new-ctrl":
                    f.Down();
                    break;
                case "closed":
                    f.Close();
                    break;
                case "lost-focus":
                    f.View.Raise(v => v.LostAggregateFocus += null, EventArgs.Empty);
                    break;
                case "edit":
                    f.CurrentSnapshot = new Mock<ITextSnapshot>().Object;
                    f.Buffer.Raise(b => b.Changed += null, (TextContentChangedEventArgs)null);
                    break;
                case "off":
                    f.Takeover = false;
                    break;
                case "options":
                    f.Peek = true;
                    f.Options.Raise(o => o.OptionChanged += null,
                                    new EditorOptionChangedEventArgs(DefaultWpfViewOptions.ClickGoToDefOpensPeekName));
                    break;
                }
                Assert(!f.Validity(), reason + " left result valid");
                if (reason != "off")
                    Assert(f.QueryCancellation.IsCancellationRequested, reason + " did not cancel query");
                f.Pending.SetResult(true);
                f.Running.GetAwaiter().GetResult();
                Assert(f.Navigated == 0, reason + " allowed stale navigation");
            }
        Console.WriteLine("PASS: pending results invalidated by new gesture / close / edit / settings");

        using (var f = new Fixture(type))
        {
            // Real routed WPF events exercise event registration without relying
            // on the physical mouse position or keyboard state in this process.
            f.Keys = ModifierKeys.None;
            f.Element.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            f.Close();
            f.Buffer.VerifyRemove(b => b.Changed -= It.IsAny<EventHandler<TextContentChangedEventArgs>>(), Times.Once);
            f.Options.VerifyRemove(o => o.OptionChanged -= It.IsAny<EventHandler<EditorOptionChangedEventArgs>>(),
                                   Times.Once);
            f.View.VerifyRemove(v => v.Closed -= It.IsAny<EventHandler>(), Times.Once);
            f.View.VerifyRemove(v => v.LostAggregateFocus -= It.IsAny<EventHandler>(), Times.Once);
            f.Keys = ModifierKeys.Control;
            f.Element.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            f.Down();
            f.Up();
            Assert(f.Requests == 0, "closed view retained active event handler");
        }
        Console.WriteLine("PASS: routed events and close unsubscription");
        using (var f = new Fixture(type))
        {
            var host = new Canvas();
            var surface = new Canvas();
            var adornment = new Button();
            host.Children.Add(surface);
            surface.Children.Add(adornment);
            f.Element.Content = host;
            f.Element.Measure(new Size(500, 500));
            f.Element.Arrange(new Rect(0, 0, 500, 500));
            f.Element.UpdateLayout();
            f.RoutedClick(adornment);
            Assert(f.Requests == 0, "nested control click intercepted");
            f.RoutedClick(surface);
            Assert(f.Requests == 1 && f.HandledBeforeQuery, "VS internal canvas text surface click was ignored");
        }
        Console.WriteLine("PASS: VS canvas routed text click and nested control pass-through");
        using (var f = new Fixture(type))
        {
            f.Classification = "preprocessor keyword"; // Macro names are not excluded by directive classification.
            f.Down();
            f.Hit = 10;
            f.Up();
            Assert(f.Requests == 1, "same-token macro click rejected");
        }
        using (var f = new Fixture(type))
        {
            f.Pending = new TaskCompletionSource<bool>();
            f.Down();
            f.Up();
            f.Takeover = false;
            var frame = new DispatcherFrame();
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            watchdog.Tick += (_, __) =>
            {
                watchdog.Stop();
                frame.Continue = false;
            };
            watchdog.Start();
            Dispatcher.PushFrame(frame);
            Assert(f.QueryCancellation.IsCancellationRequested, "external takeover change did not cancel pending RPC");
            f.Takeover = true;
            Assert(!f.Validity(), "re-enabling takeover revived stale query");
            f.Pending.SetResult(true);
            f.Running.GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS: macro same-token gesture and pending takeover timer cancellation");
        var parse =
            typeof(ClangdVSPackage).GetMethod("TryGetDefinitionTarget", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(parse != null, "shared Location/LocationLink parser missing");
        foreach (var json in new[] {
                     "{'uri':'file:///D:/definition.cpp','range':{'start':{'line':4,'character':7}}}",
                     "[{'uri':'file:///D:/definition.cpp','range':{'start':{'line':4,'character':7}}},{'uri':'file://" +
                         "/D:/ignored.cpp'}]",
                     "[{'targetUri':'file:///D:/" +
                         "definition.cpp','targetSelectionRange':{'start':{'line':4,'character':7}},'targetRange':{'" +
                         "start':{'line':1,'character':0}}}]"
                 })
        {
            var args = new object[] { Newtonsoft.Json.Linq.JToken.Parse(json), null, 0, 0 };
            Assert((bool)parse.Invoke(null, args) && (string)args[1] == "file:///D:/definition.cpp" &&
                       (int)args[2] == 4 && (int)args[3] == 7,
                   "definition response shape/first selection target changed");
        }
        foreach (var json in new[] { "null", "[]", "{}",
                                     "{'uri':'file:///D:/x.cpp','range':{'start':{'line':-1,'character':0}}}" })
            Assert(!(bool)parse.Invoke(null, new object[] { Newtonsoft.Json.Linq.JToken.Parse(json), null, 0, 0 }),
                   "invalid definition accepted");
        Console.WriteLine("PASS: Location / Location[] / LocationLink selection range / empty responses");
    }

    private static void SelectionAdornmentRegression(Type type)
    {
        // Exercise the installed VS drawing-only selection element, not a name-only fake.
        var editorPath = Environment.GetEnvironmentVariable("CLANGDVS_EDITOR_DLL");
        if (string.IsNullOrEmpty(editorPath))
        {
            var roots = new List<string>();
            AddVisualStudioRoot(roots, Environment.GetEnvironmentVariable("VSINSTALLDIR"));
            AddVisualStudioRoot(roots, Environment.GetEnvironmentVariable("VSROOT"));
            var vswhere = Environment.GetEnvironmentVariable("VSWHERE");
            if (string.IsNullOrEmpty(vswhere))
            {
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                vswhere =
                    System.IO.Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            }
            if (System.IO.File.Exists(vswhere))
            {
                try
                {
                    using (var process = Process.Start(new ProcessStartInfo {
                        FileName = vswhere,
                        Arguments = "-latest -products * -property installationPath",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                    }))
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(5000);
                        foreach (var root in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            AddVisualStudioRoot(roots, root);
                    }
                }
                catch
                {
                }
            }
            foreach (var basePath in new[] {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                var instances = System.IO.Path.Combine(basePath, "Microsoft Visual Studio");
                try
                {
                    if (System.IO.Directory.Exists(instances))
                        foreach (var version in System.IO.Directory.GetDirectories(instances))
                            foreach (var edition in System.IO.Directory.GetDirectories(version))
                                AddVisualStudioRoot(roots, edition);
                }
                catch
                {
                }
            }
            editorPath =
                roots
                    .Select(root => System.IO.Path.Combine(root, "Common7", "IDE", "CommonExtensions", "Microsoft",
                                                           "Editor", "Microsoft.VisualStudio.Platform.VSEditor.dll"))
                    .FirstOrDefault(System.IO.File.Exists);
        }
        Assert(!string.IsNullOrEmpty(editorPath),
               "VS Editor SDK integration regression requires an installed Visual Studio instance; set " +
                   "CLANGDVS_EDITOR_DLL or VSINSTALLDIR to its root.");
        var editor = Assembly.LoadFrom(editorPath);
        var selectionType =
            editor.GetType("Microsoft.VisualStudio.Text.MultiSelection.Implementation.UI.SelectionAdornment", true);
        using (var f = new Fixture(type))
        {
            var host = new Canvas();
            var stackType = editor.GetType("Microsoft.VisualStudio.Text.Editor.Implementation.ViewStack", true);
            var stackConstructor = stackType.GetConstructors().Single(c => c.GetParameters().Length == 3);
            // Installed VS 18 may load a different SDK interface version than the
            // net472 harness; mock that constructor's exact view interface.
            var viewMockType = typeof(Mock<>).MakeGenericType(stackConstructor.GetParameters()[1].ParameterType);
            var viewMock = Activator.CreateInstance(viewMockType);
            var editorView =
                viewMockType
                    .GetProperty("Object", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .GetValue(viewMock);
            var stack =
                (Panel)stackConstructor.Invoke(new object[] { new Dictionary<string, int>(), editorView, false });
            var guarded = Activator.CreateInstance(
                editor.GetType("Microsoft.VisualStudio.Text.Utilities.GuardedOperations", true));
            var layer = (Panel)Activator.CreateInstance(
                editor.GetType("Microsoft.VisualStudio.Text.Editor.Implementation.AdornmentLayer", true), editorView,
                "Selection", guarded, false);
            var selection = (UIElement)Activator.CreateInstance(selectionType, null, Brushes.Blue,
                                                                new RectangleGeometry(new Rect(0, 0, 100, 30)));
            host.Children.Add(stack);
            stack.Children.Add(layer);
            layer.Children.Add(selection);
            f.Element.Content = host;
            f.Element.Measure(new Size(500, 500));
            f.Element.Arrange(new Rect(0, 0, 500, 500));
            f.Element.UpdateLayout();
            f.RoutedClick(host, selection);
            Assert(f.Requests == 1 && f.HandledBeforeQuery,
                   "production Canvas down / SelectionAdornment up was rejected before querying");
            var button = new Button();
            layer.Children.Add(button);
            f.RoutedClick(host, button);
            Assert(f.Requests == 1, "selection-layer nested Button was intercepted");
            f.Element.HandlePreviewUp = true;
            f.RoutedClick(host, selection);
            Assert(f.Requests == 1, "already handled selection up was intercepted");
            f.Element.HandlePreviewUp = false;
        }
        Console.WriteLine("PASS: installed VS SelectionAdornment mouse-up accepted; nested Button passed through");
    }

    private static void AddVisualStudioRoot(List<string> roots, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path) &&
            !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
            roots.Add(path);
    }

    private static void NativeInputRegression(Type type)
    {
        using (var f = new Fixture(type))
        {
            f.Element.Width = f.Element.Height = 500;
            var host = new Canvas { Background = Brushes.Transparent };
            var surface = new Canvas { Background = Brushes.Transparent, Width = 500, Height = 500 };
            host.Children.Add(surface);
            f.Element.Content = host;
            var parameters = new HwndSourceParameters("ClangdVS isolated WPF input regression") {
                Width = 500,
                Height = 500,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE
                ExtendedWindowStyle = 0x08000080          // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            };
            using (var hwnd = new HwndSource(parameters))
            {
                hwnd.RootVisual = f.Element;
                f.Element.Measure(new Size(500, 500));
                f.Element.Arrange(new Rect(0, 0, 500, 500));
                f.Element.UpdateLayout();
                int downCount = -1, upCount = -1;
                object downSource = null, upSource = null;
                f.Element.AddHandler(UIElement.PreviewMouseDownEvent,
                                     new MouseButtonEventHandler((_, e) =>
                                                                 {
                                                                     downCount = e.ClickCount;
                                                                     downSource = e.OriginalSource;
                                                                 }),
                                     true);
                f.Element.AddHandler(UIElement.PreviewMouseUpEvent,
                                     new MouseButtonEventHandler((_, e) =>
                                                                 {
                                                                     upCount = e.ClickCount;
                                                                     upSource = e.OriginalSource;
                                                                 }),
                                     true);
                // Drive WPF's real RawMouseInputReport -> MouseDevice -> InputManager
                // pipeline. No SendInput, desktop cursor movement or input to VS.
                RawMouse(hwnd, "Activate, AbsoluteMove", 10000);
                RawMouse(hwnd, "Button1Press", 10001);
                RawMouse(hwnd, "Button1Release", 10002);
                Assert(downCount == 1 && upCount == 1, "native WPF single down/up click count differs from expected");
                Assert(f.Requests == 1, "native WPF content/manipulation Canvas input did not navigate");
                Assert(downSource == surface && upSource == surface,
                       "native text canvas source scenario not exercised");
                RawMouse(hwnd, "Button1Press", 10003);
                RawMouse(hwnd, "Button1Release", 10004);
                Assert(downCount == 2 && upCount == 1 && f.Requests == 1,
                       "native double-down/up gesture was not passed through");
                RawMouse(hwnd, "Button1Press", 12000);
                Assert(Mouse.Capture(f.Element), "isolated WPF view capture failed");
                RawMouse(hwnd, "AbsoluteMove",
                         12001); // Capture synchronizes with the desktop; restore isolated report coordinates.
                RawMouse(hwnd, "Button1Release", 12001);
                Assert(upSource == f.Element && f.Requests == 1,
                       "capture synchronization movement must reject gesture as drag");
                Mouse.Capture(null);
                RawMouse(hwnd, "Deactivate", 12002);
                Console.WriteLine(
                    $"PASS: native WPF input pipeline single down/up=1/1 double=2/1 canvas/captured-view drag sources; requests={f.Requests}");
            }
        }
    }

    private static void RawMouse(HwndSource source, string action, int timestamp)
    {
        var assembly = typeof(Mouse).Assembly;
        var reportType = assembly.GetType("System.Windows.Input.RawMouseInputReport", true);
        var actionsType = assembly.GetType("System.Windows.Input.RawMouseActions", true);
        var modeType = assembly.GetType("System.Windows.Input.InputMode", true);
        var report = Activator.CreateInstance(
            reportType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
            new object[] { Enum.Parse(modeType, "Foreground"), timestamp, source, Enum.Parse(actionsType, action), 30,
                           40, 0, IntPtr.Zero },
            null);
        var argsType = assembly.GetType("System.Windows.Input.InputReportEventArgs", true);
        var args = (InputEventArgs)Activator.CreateInstance(
            argsType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
            new object[] { Mouse.PrimaryDevice, report }, null);
        args.RoutedEvent = (RoutedEvent) typeof(InputManager)
                               .GetField("PreviewInputReportEvent", BindingFlags.Static | BindingFlags.NonPublic)
                               .GetValue(null);
        InputManager.Current.ProcessInput(args);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Text = "caretA\n\t中文😀  name; OTHER";
        internal readonly Mock<IWpfTextView> View = new Mock<IWpfTextView>();
        internal readonly Mock<ITextBuffer> Buffer = new Mock<ITextBuffer>();
        internal readonly Mock<IBufferGraph> Graph = new Mock<IBufferGraph>();
        internal readonly Mock<IEditorOptions> Options = new Mock<IEditorOptions>();
        internal readonly InputTestView Element = new InputTestView();
        internal ModifierKeys Keys = ModifierKeys.Control, ConfiguredModifier = ModifierKeys.Control;
        internal MouseButton Button = MouseButton.Left;
        internal int Clicks = 1, Hit = 8, Requests, Navigated, QueryLine, QueryCharacter;
        internal bool Takeover = true, Peek, ClickEnabled = true, Cpp = true, HasHit = true, HasMapping = true,
                      HasDocument = true, TextSurface = true, Handled, HandledBeforeQuery;
        internal string Classification = "identifier", Path;
        internal double HitX, HitY;
        internal Point UpPoint = new Point(30, 40);
        internal ITextSnapshot CurrentSnapshot;
        internal Func<bool> Validity;
        internal CancellationToken QueryCancellation;
        internal TaskCompletionSource<bool> Pending;
        internal Task Running;
        private readonly object handler;
        private readonly Type type;
        private bool closed;
        private MouseButtonEventArgs routedUp;

        internal Fixture(Type type, bool failAttachDiagnostics = false)
        {
            this.type = type;
            var snapshot = Snapshot(Buffer.Object, Text);
            CurrentSnapshot = snapshot;
            Buffer.SetupGet(b => b.CurrentSnapshot).Returns(() => CurrentSnapshot);
            var content = new Mock<IContentType>();
            content.Setup(c => c.IsOfType(It.IsAny<string>())).Returns(() => Cpp);
            Buffer.SetupGet(b => b.ContentType).Returns(content.Object);
            var visualBuffer = new Mock<ITextBuffer>();
            var visualSnapshot = Snapshot(visualBuffer.Object, "projection");
            var formattedLine = new Mock<ITextViewLine>();
            formattedLine.Setup(l => l.GetBufferPositionFromXCoordinate(It.IsAny<double>(), true))
                .Callback<double, bool>((x, _) => HitX = x)
                .Returns(() => HasHit ? (SnapshotPoint?) new SnapshotPoint(visualSnapshot, 3) : null);
            var lines = new Mock<IWpfTextViewLineCollection>();
            lines.Setup(l => l.GetTextViewLineContainingYCoordinate(It.IsAny<double>()))
                .Callback<double>(y => HitY = y)
                .Returns(formattedLine.Object);
            View.SetupGet(v => v.TextViewLines).Returns(lines.Object);
            View.SetupGet(v => v.VisualElement).Returns(Element);
            View.SetupGet(v => v.ViewportLeft).Returns(100);
            View.SetupGet(v => v.ViewportTop).Returns(200);
            View.SetupGet(v => v.BufferGraph).Returns(Graph.Object);
            View.SetupGet(v => v.Options).Returns(Options.Object);
            View.SetupGet(v => v.IsClosed).Returns(() => closed);
            Graph
                .Setup(g => g.MapDownToBuffer(new SnapshotPoint(visualSnapshot, 3), PointTrackingMode.Positive,
                                              Buffer.Object, PositionAffinity.Successor))
                .Returns(() => HasMapping ? (SnapshotPoint?) new SnapshotPoint(snapshot, 7 + Hit) : null);
            var docs = new Mock<ITextDocumentFactoryService>();
            var document = new Mock<ITextDocument>();
            document.SetupGet(d => d.FilePath).Returns(@"D:\project\mouse.cpp");
            ITextDocument outDoc = document.Object;
            docs.Setup(d => d.TryGetTextDocument(Buffer.Object, out outDoc)).Returns(() => HasDocument);
            var navigator = new Mock<ITextStructureNavigator>();
            navigator.Setup(n => n.GetExtentOfWord(It.IsAny<SnapshotPoint>()))
                .Returns<SnapshotPoint>(p =>
                                        {
                                            var start = Hit >= 14 ? 20 : 14;
                                            var length = Hit >= 14 ? 5 : 4;
                                            return new TextExtent(new SnapshotSpan(snapshot, start, length), true);
                                        });
            var classifier = new Mock<IClassifier>();
            var classificationType = new Mock<IClassificationType>();
            classificationType.SetupGet(c => c.Classification).Returns(() => Classification);
            classificationType.Setup(c => c.IsOfType(It.IsAny<string>())).Returns<string>(c => c == Classification);
            classifier.Setup(c => c.GetClassificationSpans(It.IsAny<SnapshotSpan>()))
                .Returns<SnapshotSpan>(
                    s => new List<ClassificationSpan> { new ClassificationSpan(s, classificationType.Object) });
            Options.Setup(o => o.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefEnabledId))
                .Returns(() =>
                         {
                             if (failAttachDiagnostics)
                                 throw new InvalidOperationException("options unavailable");
                             return ClickEnabled;
                         });
            Options.Setup(o => o.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefModifierKeyId))
                .Returns(() => ConfiguredModifier);
            Options.Setup(o => o.GetOptionValue(DefaultWpfViewOptions.ClickGoToDefOpensPeekId)).Returns(() => Peek);
            Func<string, int, int, CancellationToken, Func<bool>, Task<bool>> query =
                async (path, line, character, token, valid) =>
            {
                Requests++;
                Path = path;
                QueryLine = line;
                QueryCharacter = character;
                QueryCancellation = token;
                Validity = valid;
                HandledBeforeQuery = routedUp?.Handled ?? Handled;
                if (Pending != null)
                    await Pending.Task;
                if (valid() && !token.IsCancellationRequested)
                {
                    Navigated++;
                    return true;
                }
                return false;
            };
            try
            {
                handler = Activator.CreateInstance(
                    type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new object[] { View.Object, Buffer.Object, docs.Object, navigator.Object, classifier.Object,
                                   (Func<bool>)(() => Takeover), (Action<Func<Task>>)(action => Running = action()),
                                   query, (Func<ModifierKeys>)(() => Keys) },
                    null);
            }
            catch when (failAttachDiagnostics)
            {
                Buffer.VerifyAdd(b => b.Changed += It.IsAny<EventHandler<TextContentChangedEventArgs>>(), Times.Never);
                View.VerifyAdd(v => v.Closed += It.IsAny<EventHandler>(), Times.Never);
                throw;
            }
        }

        internal void Down()
        {
            Handled = false;
            Invoke("ProcessMouseDown", new Point(30, 40), Button, Keys, Clicks, TextSurface);
        }
        internal void Up()
        {
            Invoke("ProcessMouseUp", UpPoint, Button, Keys, Clicks, TextSurface, (Action)(() => Handled = true));
        }
        internal void Move(Point point)
        {
            Invoke("ProcessMouseMove", point);
        }
        internal void RoutedClick(UIElement source, UIElement upSource = null)
        {
            Handled = false;
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0,
                                                MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
            source.RaiseEvent(down);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0,
                                              MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent };
            routedUp = up;
            (upSource ?? source).RaiseEvent(up);
            Handled = up.Handled;
            if (Requests > 0)
            {
                var nativeNavigations = 0;
                MouseButtonEventHandler native = (_, e) =>
                {
                    if (!e.Handled)
                        nativeNavigations++;
                };
                Element.AddHandler(UIElement.MouseUpEvent, native, true);
                up.RoutedEvent = UIElement.MouseUpEvent;
                (upSource ?? source).RaiseEvent(up);
                Element.RemoveHandler(UIElement.MouseUpEvent, native);
                Assert(nativeNavigations == 0, "Preview handled state failed to suppress native bubbling navigation");
            }
        }
        internal void Close()
        {
            closed = true;
            View.Raise(v => v.Closed += null, EventArgs.Empty);
        }
        private void Invoke(string name, params object[] args)
        {
            try
            {
                type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(handler, args);
            }
            catch (TargetInvocationException ex)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
        public void Dispose()
        {
            if (!closed)
                Close();
        }

        private static ITextSnapshot Snapshot(ITextBuffer buffer, string text)
        {
            var snapshot = new Mock<ITextSnapshot>();
            snapshot.SetupGet(s => s.TextBuffer).Returns(buffer);
            snapshot.SetupGet(s => s.Length).Returns(text.Length);
            snapshot.Setup(s => s[It.IsAny<int>()]).Returns<int>(p => text[p]);
            snapshot.Setup(s => s.GetText(It.IsAny<int>(), It.IsAny<int>()))
                .Returns<int, int>((p, n) => text.Substring(p, n));
            snapshot.Setup(s => s.GetText(It.IsAny<Span>())).Returns<Span>(s => text.Substring(s.Start, s.Length));
            snapshot.Setup(s => s.GetLineFromPosition(It.IsAny<int>()))
                .Returns<int>(p =>
                              {
                                  int start = p >= 7 ? 7 : 0;
                                  var line = new Mock<ITextSnapshotLine>();
                                  line.SetupGet(l => l.Start).Returns(new SnapshotPoint(snapshot.Object, start));
                                  line.SetupGet(l => l.LineNumber).Returns(start == 0 ? 0 : 1);
                                  return line.Object;
                              });
            return snapshot.Object;
        }
    }

    private sealed class InputTestView : ContentControl
    {
        internal bool HandlePreviewDown, HandlePreviewUp;
        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            if (HandlePreviewDown)
                e.Handled = true;
        }
        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);
            if (HandlePreviewUp)
                e.Handled = true;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
