using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClangdVS;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

internal static class InspectionWindowRegression
{
    internal static async Task RunAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var failures = new List<string>();
                    Check(failures, "Symbol Info selects properties", SymbolSelectsProperties);
                    Check(failures, "hierarchy unwraps item and selects tree", HierarchyRoot);
                    Check(failures, "empty results are readable", EmptyResults);
                    Check(failures, "AST children are lazy", LazyAst);
                    Check(failures, "AST selection updates properties without stale rows", AstProperties);
                    Check(failures, "array AST properties stay shallow", ArrayProperties);
                    Check(failures, "tab visual text uses paired dynamic theme tokens", InspectionThemeRegression.Tabs);
                    Check(failures, "tree visual selection uses paired dynamic theme tokens", InspectionThemeRegression.Tree);
                    Check(failures, "theme resources update existing controls", DynamicTheme);
                    Check(failures, "hierarchy guards and cycles", HierarchyGuards);
                    Check(failures, "prepared hierarchy respects takeover gate", PreparedHierarchyGate);
                    Check(failures, "disposed window ignores late presentation", DisposedPresentation);
                    if (failures.Count != 0)
                        throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
                    await InspectionLifetimeRegression.RunAsync();
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (await Task.WhenAny(completion.Task, Task.Delay(15000)).ConfigureAwait(false) != completion.Task)
            throw new TimeoutException("Inspection window STA regression did not finish.");
        await completion.Task.ConfigureAwait(false);
    }

    private static void SymbolSelectsProperties()
    {
        using (var window = new ClangdInspectionToolWindow())
        {
            Show(window, "Symbol Info", JToken.Parse("[{\"name\":\"Foo\",\"id\":\"42\"}]"));
            Assert(Field<TabControl>(window, "_tabs").SelectedIndex == 1, "Symbol Info did not select properties");
            Assert(Field<DataGrid>(window, "_properties").Items.Count == 2, "Symbol properties missing");
            Show(window, "AST", JToken.Parse("{\"kind\":\"Decl\",\"name\":\"Foo\"}"));
            Assert(Field<TabControl>(window, "_tabs").SelectedIndex == 0, "AST did not select tree");
            Show(window, "Symbol Info", JToken.Parse("{\"name\":\"Bar\"}"));
            Assert(Field<TabControl>(window, "_tabs").SelectedIndex == 1, "Single symbol did not select properties");
        }
    }

    private static void HierarchyRoot()
    {
        using (var session = new InspectionSession())
        using (var window = new ClangdInspectionToolWindow())
        {
            Field<TabControl>(window, "_tabs").SelectedIndex = 1;
            Show(window, "Type Hierarchy", JToken.Parse(
                "[{\"item\":{\"kind\":5,\"name\":\"Derived\",\"uri\":\"file:///a.cpp\"}," +
                "\"supertypes\":[{\"name\":\"Base\"}],\"subtypes\":[]}]"));
            var root = (TreeViewItem)Field<TreeView>(window, "_tree").Items[0];
            Assert((string)root.Header == "Derived", "Hierarchy root is not item.name: " + root.Header);
            Assert(root.IsExpanded && root.IsSelected, "Hierarchy root is not initially expanded/selected");
            Assert(Property(window, "name") == "Derived", "Hierarchy root properties missing");
            Assert(Field<TabControl>(window, "_tabs").SelectedIndex == 0, "Hierarchy did not select tree");
            Expand(root);
            var supers = root.Items.OfType<TreeViewItem>().Single(n => ((string)n.Header).Contains("基类型"));
            supers.IsSelected = true;
            Assert(Property(window, "name") == null && Property(window, "提示") != null,
                   "Group retained previous node properties");
            Expand(supers);
            Assert((string)((TreeViewItem)supers.Items[0]).Header == "Base", "Prepared supertypes lost");
            Assert(((TreeViewItem)supers.Items[0]).Items.Count != 0, "Hierarchy child cannot expand recursively");
            ((TreeViewItem)supers.Items[0]).IsSelected = true;
            Assert(Property(window, "name") == "Base", "Hierarchy child properties missing");
            var subs = (TreeViewItem)root.Items[1];
            Expand(subs);
            var empty = (TreeViewItem)subs.Items[0];
            Assert(((string)empty.Header).Contains("没有结果"), "Empty hierarchy is not readable");
            empty.IsSelected = true;
            Assert(Property(window, "name") == null && Property(window, "提示").Contains("没有结果"),
                   "Empty hierarchy retained previous properties");
        }
    }

    private static void EmptyResults()
    {
        using (var window = new ClangdInspectionToolWindow())
        {
            foreach (var title in new[] { "Symbol Info", "AST", "Type Hierarchy" })
            foreach (var value in new JToken[] { null, JValue.CreateNull(), new JArray(), new JObject() })
            {
                Show(window, title, value);
                Assert(Field<TextBlock>(window, "_status").Text.Contains("没有结果"), title + " has no empty-result status");
                Assert(Field<TreeView>(window, "_tree").Items.Count == 0, "Empty result retained tree nodes");
                Assert(Property(window, "提示")?.Contains("没有结果") == true, "Empty properties lack a message");
                Assert(Field<TabControl>(window, "_tabs").SelectedIndex == (title == "Symbol Info" ? 1 : 0),
                       "Empty result selected wrong tab");
            }
        }
    }

    private static void LazyAst()
    {
        using (var window = new ClangdInspectionToolWindow())
        {
            var root = new JObject { ["kind"] = "Root" };
            var current = root;
            for (var i = 0; i < 2048; i++)
            {
                var child = new JObject { ["kind"] = "Child" };
                current["children"] = new JArray(child);
                current = child;
            }
            Show(window, "AST", root);
            var node = (TreeViewItem)Field<TreeView>(window, "_tree").Items[0];
            Assert(node.IsExpanded, "AST root is not initially expanded");
            Assert(CountNodes(node) <= 3, "AST eagerly created descendants");
            var childNode = (TreeViewItem)node.Items[0];
            Assert((string)childNode.Header == "Child", "AST children have an intermediate group");
            Assert(!childNode.IsExpanded, "AST expanded deeper than one level");
            Expand(childNode);
            Assert((string)((TreeViewItem)childNode.Items[0]).Header == "Child", "AST expansion lost grandchild");
            Assert(CountNodes(node) <= 4, "AST expansion eagerly created grandchildren");
            var wide = new JObject { ["kind"] = "Wide", ["children"] = new JArray(
                Enumerable.Range(0, 2000).Select(i => new JObject { ["kind"] = "Leaf", ["name"] = i.ToString() })) };
            Show(window, "AST", wide);
            Assert(CountNodes((TreeViewItem)Field<TreeView>(window, "_tree").Items[0]) == 2001,
                   "One-level expansion lost wide AST nodes");
        }
    }

    private static void AstProperties()
    {
        using (var window = new ClangdInspectionToolWindow())
        {
            Show(window, "AST", JToken.Parse("{\"kind\":\"Decl\",\"name\":\"Root\",\"range\":{\"start\":{\"line\":2}}," +
                "\"children\":[{\"kind\":\"Expr\",\"name\":\"Child\",\"detail\":\"int\"}]}"));
            var tree = Field<TreeView>(window, "_tree");
            var root = (TreeViewItem)tree.Items[0];
            Assert(root.IsSelected && Property(window, "name") == "Root", "Initial AST root properties missing");
            Assert(Property(window, "range")?.Contains("line") == true, "Structured metadata lost");
            Assert(Property(window, "children")?.Contains("Child") != true, "Properties serialize AST descendants");
            var child = (TreeViewItem)root.Items[0];
            child.IsSelected = true;
            Assert(Property(window, "name") == "Child" && Property(window, "detail") == "int",
                   "Selection did not update properties");
            Assert(Field<TabControl>(window, "_tabs").SelectedIndex == 0, "Selection unexpectedly changed tab");
            window.InvalidateResult();
            Assert(Property(window, "name") == null && Property(window, "提示")?.Contains("失效") == true,
                   "Invalidation retained old properties");
            child.IsSelected = false;
            child.IsSelected = true;
            Assert(Property(window, "name") == null, "Detached node restored stale properties");
            window.ShowFailure("AST", new InvalidOperationException("test-error"));
            Assert(Property(window, "提示")?.Contains("test-error") == true, "Failure leaves blank properties");
            Show(window, "AST", new JArray(new JObject { ["kind"] = "First" }, new JObject { ["kind"] = "Second" }));
            ((TreeViewItem)tree.Items[1]).IsSelected = true;
            Assert(Property(window, "kind") == "Second", "Multiple-root selection missing");
        }
    }

    private static string Property(ClangdInspectionToolWindow window, string name)
    {
        foreach (var row in Field<DataGrid>(window, "_properties").Items)
            if ((string)row.GetType().GetProperty("Name").GetValue(row) == name)
                return (string)row.GetType().GetProperty("Value").GetValue(row);
        return null;
    }

    private static void ArrayProperties()
    {
        using (var window = new ClangdInspectionToolWindow())
        {
            var nested = new JArray(new JObject { ["kind"] = "HiddenDescendant" });
            Show(window, "AST", new JArray { nested });
            Assert(Property(window, "数量") == "1" && Property(window, "值") == null,
                   "Array properties serialized descendants instead of showing a shallow count");
        }
    }

    private static void DynamicTheme()
    {
        using (var window = new ClangdInspectionToolWindow())
        {
            var panel = (FrameworkElement)window.Content;
            var resources = panel.Resources;
            // No VS shell is running: supply actual SDK keys, then replace their values at runtime.
            var keys = new object[] {
                EnvironmentColors.ToolWindowBackgroundBrushKey, EnvironmentColors.ToolWindowTextBrushKey,
                EnvironmentColors.ToolWindowBorderBrushKey, EnvironmentColors.ToolWindowContentGridBrushKey,
                EnvironmentColors.ToolTipBrushKey, EnvironmentColors.ToolTipTextBrushKey, EnvironmentColors.ToolTipBorderBrushKey,
                EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey, EnvironmentColors.CommandBarTextHoverBrushKey,
                TreeViewColors.BackgroundBrushKey, TreeViewColors.BackgroundTextBrushKey,
                TreeViewColors.SelectedItemActiveBrushKey, TreeViewColors.SelectedItemActiveTextBrushKey,
                TreeViewColors.SelectedItemInactiveBrushKey, TreeViewColors.SelectedItemInactiveTextBrushKey,
            };
            foreach (var key in keys) resources[key] = Brushes.DarkSlateBlue;
            Show(window, "AST", new JObject { ["kind"] = "Decl", ["name"] = "Themed" });
            var tree = Field<TreeView>(window, "_tree");
            var node = (TreeViewItem)tree.Items[0];
            var tabs = Field<TabControl>(window, "_tabs");
            var tab = (TabItem)tabs.Items[1];
            var grid = Field<DataGrid>(window, "_properties");
            var host = new Window { Content = panel, Width = 600, Height = 400, ShowInTaskbar = false };
            try
            {
                host.Show();
                tabs.SelectedIndex = 1;
                host.UpdateLayout();
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                Assert(row != null, "Property grid did not realize rows");
                var cell = Descendants<DataGridCell>(row).First();
                var header = Descendants<DataGridColumnHeader>(grid).First(h => h.Column != null);
                Assert(header.Template.FindName("PART_RightHeaderGripper", header) is Thumb,
                       "Themed column header lost built-in column resizing");
                var rightGrip = (Thumb)header.Template.FindName("PART_RightHeaderGripper", header);
                Assert(rightGrip.Background != null && rightGrip.IsHitTestVisible,
                       "Themed column resize grip is not hit-testable");
                var tooltip = node.ToolTip as ToolTip;
                Assert(tooltip != null, "AST tooltip is not explicitly themed");
                tooltip.PlacementTarget = node;
                tooltip.IsOpen = true;
                host.UpdateLayout();
                for (var pass = 0; pass < 2; pass++)
                {
                    var brush = pass == 0 ? Brushes.DarkSlateBlue : Brushes.LightGoldenrodYellow;
                    foreach (var key in keys) resources[key] = brush;
                    host.UpdateLayout();
                    Assert(ReferenceEquals(panel.GetValue(Panel.BackgroundProperty), brush), "Root background is not dynamic");
                    foreach (var control in new Control[] { grid, row, cell, header, tooltip })
                    {
                        Assert(ReferenceEquals(control.Background, brush), control.GetType().Name + " background is not dynamic");
                        Assert(ReferenceEquals(control.Foreground, brush), control.GetType().Name + " foreground is not dynamic");
                    }
                    Assert(Descendants<TextBlock>(cell).Any(text => ReferenceEquals(text.Foreground, brush)),
                           "Generated property text is not dynamic");
                    Assert(Descendants<TextBlock>(header).Any(text => ReferenceEquals(text.Foreground, brush)),
                           "Generated column header text is not dynamic");
                }
                // A selected value can be copied with the built-in DataGrid command.
                Assert(grid.IsReadOnly && grid.SelectionUnit == DataGridSelectionUnit.CellOrRowHeader &&
                       grid.ClipboardCopyMode == DataGridClipboardCopyMode.ExcludeHeader,
                       "Property values cannot use built-in cell selection/copy");
                grid.SelectedCells.Clear();
                grid.SelectedCells.Add(new DataGridCellInfo(row.Item, grid.Columns[1]));
                Assert(ApplicationCommands.Copy.CanExecute(null, grid) &&
                       (string)grid.Columns[1].OnCopyingCellClipboardContent(row.Item) == "Decl",
                       "Built-in Copy cannot obtain the selected property value");
                resources[TreeViewColors.SelectedItemActiveBrushKey] = Brushes.Crimson;
                resources[TreeViewColors.SelectedItemActiveTextBrushKey] = Brushes.White;
                cell.IsSelected = true;
                cell.Focus();
                host.UpdateLayout();
                Assert(ReferenceEquals(cell.Background, Brushes.Crimson) && ReferenceEquals(cell.Foreground, Brushes.White),
                       "Selected grid cell does not use paired VS selection colors");
                Assert(Descendants<TextBlock>(cell).Any(text => ReferenceEquals(text.Foreground, Brushes.White)),
                       "Selected cell's generated text lost its foreground");
                resources[TreeViewColors.SelectedItemInactiveBrushKey] = Brushes.Navy;
                resources[TreeViewColors.SelectedItemInactiveTextBrushKey] = Brushes.Ivory;
                tab.Focus();
                host.UpdateLayout();
                Assert(ReferenceEquals(cell.Background, Brushes.Navy) && ReferenceEquals(cell.Foreground, Brushes.Ivory),
                       "Unfocused selection does not use paired VS colors");
                Assert(Descendants<TextBlock>(cell).Any(text => ReferenceEquals(text.Foreground, Brushes.Ivory)),
                       "Unfocused cell's generated text lost its foreground");
                cell.IsSelected = false;
                resources[EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey] = Brushes.DarkCyan;
                resources[EnvironmentColors.CommandBarTextHoverBrushKey] = Brushes.Beige;
                foreach (var control in new Control[] { row, cell, header })
                {
                    SetMouseOver(control, true);
                    Assert(ReferenceEquals(control.Background, Brushes.DarkCyan) && ReferenceEquals(control.Foreground, Brushes.Beige),
                           control.GetType().Name + " hover does not use paired VS colors");
                    resources[EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey] = Brushes.DarkOliveGreen;
                    Assert(ReferenceEquals(control.Background, Brushes.DarkOliveGreen), "Hover token update was ignored");
                    SetMouseOver(control, false);
                    resources[EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey] = Brushes.DarkCyan;
                }
                resources[EnvironmentColors.ToolTipBrushKey] = Brushes.DarkMagenta;
                resources[EnvironmentColors.ToolTipTextBrushKey] = Brushes.Linen;
                Assert(ReferenceEquals(tooltip.Background, Brushes.DarkMagenta) && ReferenceEquals(tooltip.Foreground, Brushes.Linen),
                       "Tooltip does not use paired VS tooltip tokens");
                tooltip.IsOpen = false;
            }
            finally { host.Content = null; host.Close(); }
        }
    }

    private static void SetMouseOver(Control control, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
            BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        control.SetValue(key, value);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void HierarchyGuards()
    {
        using (var session = new InspectionSession())
        using (var window = new ClangdInspectionToolWindow())
        {
            Show(window, "Type Hierarchy", JToken.Parse(
                "{\"item\":{\"name\":\"A\",\"uri\":\"file:///a.cpp\"}," +
                "\"supertypes\":[{\"name\":\"A\",\"uri\":\"file:///a.cpp\"}]}"));
            var root = (TreeViewItem)Field<TreeView>(window, "_tree").Items[0];
            Expand(root);
            var supers = (TreeViewItem)root.Items[0];
            Expand(supers);
            var cycle = (TreeViewItem)supers.Items[0];
            Assert(cycle.Items.Count == 0 && ((string)cycle.Header).Contains("循环"), "Cycle remains expandable");
            var subs = (TreeViewItem)root.Items[1];
            session.Settings.EnableTypeHierarchy = false;
            Expand(subs);
            Assert(subs.Items.OfType<TreeViewItem>().Any(n => ((string)n.Header).Contains("未启用") ||
                                                            ((string)n.Header).Contains("未运行")),
                   "Unavailable client/feature has no readable status");
            Show(window, "AST", new JObject { ["kind"] = "New" });
            Expand(root);
            Assert((string)((TreeViewItem)Field<TreeView>(window, "_tree").Items[0]).Header == "New",
                   "Stale hierarchy changed current result");
        }
    }

    private static void PreparedHierarchyGate()
    {
        using (var session = new InspectionSession())
        using (var window = new ClangdInspectionToolWindow())
        {
            Show(window, "Type Hierarchy", JToken.Parse(
                "{\"item\":{\"name\":\"Old\"},\"supertypes\":[{\"name\":\"Stale\"}]}"));
            var root = (TreeViewItem)Field<TreeView>(window, "_tree").Items[0];
            var supers = (TreeViewItem)root.Items[0];
            session.Settings.TakeOverFromNativeIntelliSense = false;
            Expand(supers);
            Assert(!supers.Items.OfType<TreeViewItem>().Any(n => (string)n.Header == "Stale"),
                   "prepared children bypassed takeover gate");
        }
    }

    private static void DisposedPresentation()
    {
        var window = new ClangdInspectionToolWindow();
        Show(window, "AST", new JObject { ["kind"] = "Old" });
        window.Dispose();
        Show(window, "AST", new JObject { ["kind"] = "Late" });
        typeof(ClangdInspectionToolWindow).GetMethod("ShowFailure", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, new object[] { "AST", new InvalidOperationException("late-disposed-error") });
        Assert(!Field<TreeView>(window, "_tree").Items.OfType<TreeViewItem>().Any(n => (string)n.Header == "Late"),
               "late presentation resurrected disposed window");
        Assert(!Field<TextBlock>(window, "_status").Text.Contains("late-disposed-error"),
               "late failure changed disposed window");
    }

    private static int CountNodes(TreeViewItem node) => 1 + node.Items.OfType<TreeViewItem>().Sum(CountNodes);
    private static void Expand(TreeViewItem node) => node.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent, node));
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void Show(ClangdInspectionToolWindow window, string title, JToken result) =>
        typeof(ClangdInspectionToolWindow).GetMethod("ShowResult", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(window, new object[] { title, result, null });
    private static void Check(List<string> failures, string name, Action test)
    {
        try { test(); Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { failures.Add("FAIL: " + name + ": " + ex.GetBaseException().Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

// A live process and inert RPC are sufficient for lifecycle gates; no server is launched.
internal sealed class InspectionSession : IDisposable
{
    private readonly ClangdLanguageClient _previousClient;
    private readonly ClangdSettings _previousSettings;
    private readonly JsonRpc _rpc = new JsonRpc(new MemoryStream(), new MemoryStream());
    internal readonly ClangdLanguageClient Client = new ClangdLanguageClient();
    internal readonly ClangdSettings Settings = new ClangdSettings {
        TakeOverFromNativeIntelliSense = true, EnableAstView = true,
        EnableSymbolInfo = true, EnableTypeHierarchy = true,
    };

    internal InspectionSession()
    {
        _previousClient = (ClangdLanguageClient)typeof(ClangdLanguageClient)
            .GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var settings = typeof(ClangdSettings).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        _previousSettings = (ClangdSettings)settings.GetValue(null);
        settings.SetValue(null, Settings);
        Set(Client, "_rpc", _rpc);
        Set(Client, "_serverProcess", Process.GetCurrentProcess());
        Current(Client);
    }

    internal static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    internal static void Current(ClangdLanguageClient value) => typeof(ClangdLanguageClient)
        .GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);
    public void Dispose()
    {
        Current(_previousClient);
        typeof(ClangdSettings).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, _previousSettings);
        ((Process)typeof(ClangdLanguageClient).GetField("_serverProcess", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(Client))?.Dispose();
        _rpc.Dispose();
    }
}
