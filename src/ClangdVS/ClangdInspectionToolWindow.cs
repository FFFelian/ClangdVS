using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClangdVS
{
    [Guid("6B2AE944-310C-4D76-893E-593615E9E9D1")]
    public sealed class ClangdInspectionToolWindow : ToolWindowPane
    {
        private readonly TextBlock _status;
        private readonly TreeView _tree;
        private readonly DataGrid _properties;
        private readonly TabControl _tabs;
        private CancellationTokenSource _requests = new CancellationTokenSource();
        private long _generation;
        private bool _disposed;
        private Func<bool> _resultCurrent;

        public ClangdInspectionToolWindow() : base(null)
        {
            Caption = "clangd 检查结果";
            _status = new TextBlock { Margin = new Thickness(8), Text = "选择 AST、Symbol Info 或 Type Hierarchy 命令。" };
            _tree = new TreeView();
            _properties = new DataGrid {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
            };
            _properties.Columns.Add(new DataGridTextColumn { Header = "属性", Binding = new Binding("Name") });
            _properties.Columns.Add(new DataGridTextColumn { Header = "值", Binding = new Binding("Value"),
                                                           Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _tabs = new TabControl();
            _tabs.Items.Add(new TabItem { Header = "结构", Content = _tree });
            _tabs.Items.Add(new TabItem { Header = "属性", Content = _properties });
            Content = new DockPanel {
                Children = { _status, _tabs },
            };
            DockPanel.SetDock(_status, Dock.Top);
            ApplyTheme((DockPanel)Content);
            _tree.SelectedItemChanged += (sender, args) => ShowNodeProperties(args.NewValue as TreeViewItem);
            ShowPropertyMessage("选择 AST、Symbol Info 或 Type Hierarchy 命令。");
        }

        private void ApplyTheme(DockPanel root)
        {
            // DynamicResource keeps VS themes, high contrast and user token overrides live.
            // VsResourceKeys has no DataGrid style: style its generated containers explicitly.
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _tree.SetResourceReference(Control.BackgroundProperty, TreeViewColors.BackgroundBrushKey);
            _tree.SetResourceReference(Control.ForegroundProperty, TreeViewColors.BackgroundTextBrushKey);
            _tree.Resources[typeof(TreeViewItem)] = CreateTreeItemStyle();
            // Navigation styles contain a fixed-width Label with its own default foreground.
            // Use the stock TabControl layout/keyboard handling and local tool-window headers.
            _tabs.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _tabs.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            _tabs.Padding = new Thickness(2);
            var tabStyle = CreateTabItemStyle();
            foreach (TabItem tab in _tabs.Items)
                tab.Style = tabStyle;
            _properties.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _properties.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _properties.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            foreach (var type in new[] { typeof(DataGridRow), typeof(DataGridCell), typeof(DataGridColumnHeader) })
            {
                var style = new Style(type);
                style.Setters.Add(ResourceSetter(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey));
                style.Setters.Add(ResourceSetter(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey));
                style.Setters.Add(ResourceSetter(Control.BorderBrushProperty, EnvironmentColors.ToolWindowContentGridBrushKey));
                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(ResourceSetter(Control.BackgroundProperty, EnvironmentColors.CommandBarMouseOverBackgroundGradientBrushKey));
                hover.Setters.Add(ResourceSetter(Control.ForegroundProperty, EnvironmentColors.CommandBarTextHoverBrushKey));
                style.Triggers.Add(hover);
                if (type != typeof(DataGridColumnHeader))
                {
                    var selectedProperty = type == typeof(DataGridCell) ? DataGridCell.IsSelectedProperty : DataGridRow.IsSelectedProperty;
                    var selected = new Trigger { Property = selectedProperty, Value = true };
                    selected.Setters.Add(ResourceSetter(Control.BackgroundProperty, TreeViewColors.SelectedItemInactiveBrushKey));
                    selected.Setters.Add(ResourceSetter(Control.ForegroundProperty, TreeViewColors.SelectedItemInactiveTextBrushKey));
                    style.Triggers.Add(selected);
                    var active = new MultiDataTrigger();
                    active.Conditions.Add(new Condition(new Binding("IsSelected") { RelativeSource = RelativeSource.Self }, true));
                    active.Conditions.Add(new Condition(new Binding("IsKeyboardFocusWithin") {
                        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1) }, true));
                    active.Setters.Add(ResourceSetter(Control.BackgroundProperty, TreeViewColors.SelectedItemActiveBrushKey));
                    active.Setters.Add(ResourceSetter(Control.ForegroundProperty, TreeViewColors.SelectedItemActiveTextBrushKey));
                    style.Triggers.Add(active);
                }
                else
                {
                    // The stock header template paints OS hover colors over Background.
                    var border = new FrameworkElementFactory(typeof(Border));
                    border.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
                    border.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
                    border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
                    border.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
                    var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                    presenter.SetBinding(ContentPresenter.ContentProperty, TemplateBinding("Content"));
                    border.AppendChild(presenter);
                    var layout = new FrameworkElementFactory(typeof(Grid));
                    layout.AppendChild(border);
                    foreach (var side in new[] { "Left", "Right" })
                    {
                        var grip = new FrameworkElementFactory(typeof(Thumb), "PART_" + side + "HeaderGripper");
                        grip.SetValue(FrameworkElement.WidthProperty, 5.0);
                        grip.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                            side == "Left" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
                        grip.SetValue(UIElement.OpacityProperty, 0.0);
                        grip.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.SizeWE);
                        layout.AppendChild(grip);
                    }
                    style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(type) { VisualTree = layout }));
                }
                _properties.Resources[type] = style;
            }
        }

        private static Binding TemplateBinding(string property) => new Binding(property) {
            RelativeSource = RelativeSource.TemplatedParent,
        };

        private static Setter ResourceSetter(DependencyProperty property, object key) =>
            new Setter(property, new DynamicResourceExtension(key));

        private static Style CreateTabItemStyle()
        {
            var style = new Style(typeof(TabItem));
            style.Setters.Add(ResourceSetter(Control.BackgroundProperty, EnvironmentColors.ToolWindowTabGradientBrushKey));
            style.Setters.Add(ResourceSetter(Control.ForegroundProperty, EnvironmentColors.ToolWindowTabTextBrushKey));
            style.Setters.Add(ResourceSetter(Control.BorderBrushProperty, EnvironmentColors.ToolWindowTabBorderBrushKey));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
            border.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
            border.SetBinding(Border.BorderThicknessProperty, TemplateBinding("BorderThickness"));
            border.SetBinding(Border.PaddingProperty, TemplateBinding("Padding"));
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            header.SetBinding(FrameworkElement.HorizontalAlignmentProperty, TemplateBinding("HorizontalContentAlignment"));
            header.SetBinding(FrameworkElement.VerticalAlignmentProperty, TemplateBinding("VerticalContentAlignment"));
            border.AppendChild(header);
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(TabItem)) { VisualTree = border }));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(ResourceSetter(Control.BackgroundProperty, EnvironmentColors.ToolWindowTabMouseOverBackgroundGradientBrushKey));
            hover.Setters.Add(ResourceSetter(Control.ForegroundProperty, EnvironmentColors.ToolWindowTabMouseOverTextBrushKey));
            hover.Setters.Add(ResourceSetter(Control.BorderBrushProperty, EnvironmentColors.ToolWindowTabMouseOverBorderBrushKey));
            style.Triggers.Add(hover);
            var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(ResourceSetter(Control.BackgroundProperty, EnvironmentColors.ToolWindowTabSelectedTabBrushKey));
            selected.Setters.Add(ResourceSetter(Control.ForegroundProperty, EnvironmentColors.ToolWindowTabSelectedTextBrushKey));
            selected.Setters.Add(ResourceSetter(Control.BorderBrushProperty, EnvironmentColors.ToolWindowTabSelectedBorderBrushKey));
            style.Triggers.Add(selected);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(ResourceSetter(Control.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey));
            style.Triggers.Add(disabled);
            return style;
        }

        private static Style CreateTreeItemStyle()
        {
            // The default WPF item template uses OS Highlight/Control colors for selection.
            // Keep TreeViewItem's behavior, but paint the header with paired VS tokens.
            var style = new Style(typeof(TreeViewItem));
            style.Setters.Add(ResourceSetter(Control.ForegroundProperty, TreeViewColors.BackgroundTextBrushKey));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            var layout = new FrameworkElementFactory(typeof(StackPanel));
            var row = new FrameworkElementFactory(typeof(DockPanel));
            var expander = new FrameworkElementFactory(typeof(ToggleButton), "Expander");
            expander.SetValue(DockPanel.DockProperty, Dock.Left);
            expander.SetValue(FrameworkElement.WidthProperty, 18.0);
            expander.SetValue(UIElement.FocusableProperty, false);
            expander.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            expander.SetResourceReference(Control.ForegroundProperty, TreeViewColors.GlyphBrushKey);
            expander.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsExpanded") {
                RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay,
            });
            var arrowBorder = new FrameworkElementFactory(typeof(Border));
            arrowBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "Arrow");
            arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0 0 L 4 4 L 0 8 Z"));
            arrow.SetBinding(System.Windows.Shapes.Shape.FillProperty, TemplateBinding("Foreground"));
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
            arrowBorder.AppendChild(arrow);
            var toggleTemplate = new ControlTemplate(typeof(ToggleButton)) { VisualTree = arrowBorder };
            var expandedArrow = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            expandedArrow.Setters.Add(new Setter(UIElement.RenderTransformProperty, new RotateTransform(90), "Arrow"));
            toggleTemplate.Triggers.Add(expandedArrow);
            expander.SetValue(Control.TemplateProperty, toggleTemplate);
            row.AppendChild(expander);
            var border = new FrameworkElementFactory(typeof(Border), "PART_Header");
            border.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
            border.SetBinding(Border.PaddingProperty, TemplateBinding("Padding"));
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetBinding(FrameworkElement.HorizontalAlignmentProperty, TemplateBinding("HorizontalContentAlignment"));
            border.AppendChild(header);
            row.AppendChild(border);
            layout.AppendChild(row);
            var items = new FrameworkElementFactory(typeof(ItemsPresenter), "ItemsHost");
            items.SetValue(FrameworkElement.MarginProperty, new Thickness(18, 0, 0, 0));
            layout.AppendChild(items);
            var template = new ControlTemplate(typeof(TreeViewItem)) { VisualTree = layout };
            var collapsed = new Trigger { Property = TreeViewItem.IsExpandedProperty, Value = false };
            collapsed.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "ItemsHost"));
            template.Triggers.Add(collapsed);
            var leaf = new Trigger { Property = ItemsControl.HasItemsProperty, Value = false };
            leaf.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Hidden, "Expander"));
            template.Triggers.Add(leaf);
            var selected = new Trigger { Property = TreeViewItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(ResourceSetter(Control.BackgroundProperty, TreeViewColors.SelectedItemInactiveBrushKey));
            selected.Setters.Add(ResourceSetter(Control.ForegroundProperty, TreeViewColors.SelectedItemInactiveTextBrushKey));
            selected.Setters.Add(new Setter(Control.ForegroundProperty,
                new DynamicResourceExtension(TreeViewColors.SelectedItemInactiveGlyphBrushKey), "Expander"));
            template.Triggers.Add(selected);
            var active = new MultiTrigger();
            active.Conditions.Add(new Condition(TreeViewItem.IsSelectedProperty, true));
            active.Conditions.Add(new Condition(TreeViewItem.IsSelectionActiveProperty, true));
            active.Setters.Add(ResourceSetter(Control.BackgroundProperty, TreeViewColors.SelectedItemActiveBrushKey));
            active.Setters.Add(ResourceSetter(Control.ForegroundProperty, TreeViewColors.SelectedItemActiveTextBrushKey));
            active.Setters.Add(new Setter(Control.ForegroundProperty,
                new DynamicResourceExtension(TreeViewColors.SelectedItemActiveGlyphBrushKey), "Expander"));
            template.Triggers.Add(active);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(ResourceSetter(Control.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey));
            disabled.Setters.Add(new Setter(Control.ForegroundProperty,
                new DynamicResourceExtension(EnvironmentColors.SystemGrayTextBrushKey), "Expander"));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private TreeViewItem CreateNode(string title, JToken value = null)
        {
            var node = new TreeViewItem { Header = title, Tag = value };
            return node;
        }

        private void ShowNodeProperties(TreeViewItem node)
        {
            if (node?.Tag is JToken token)
            {
                var properties = token is JObject obj
                    ? obj.Properties().Where(property => !IsChildProperty(property.Name)).Select(property => new {
                        Name = property.Name, Value = PropertyValue(property.Value),
                    }).ToArray()
                    : token is JArray array ? new[] { new { Name = "数量", Value = array.Count.ToString() } }
                    : new[] { new { Name = "值", Value = PropertyValue(token) } };
                if (properties.Length != 0)
                {
                    _properties.ItemsSource = properties;
                    return;
                }
            }
            ShowPropertyMessage(node == null ? "请选择结构节点查看属性。" : node.Header + "：请选择类型或 AST 节点查看属性。");
        }

        private static bool IsChildProperty(string name) => name == "children" || name == "supertypes" || name == "subtypes";
        private static string PropertyValue(JToken value) => value is JValue scalar && scalar.Type == JTokenType.String
            ? scalar.ToString() : value.ToString(Newtonsoft.Json.Formatting.None);

        private void ShowPropertyMessage(string message) =>
            _properties.ItemsSource = new[] { new { Name = "提示", Value = message } };

        internal void ShowResult(string title, JToken result, Func<bool> isCurrent = null)
        {
            if (_disposed)
                return;
            ResetResult();
            _resultCurrent = isCurrent;
            _status.Text = title;
            _tabs.SelectedIndex = title == "Symbol Info" ? 1 : 0;
            if (IsEmpty(result))
            {
                _status.Text += "：没有结果。";
                ShowPropertyMessage("没有结果。");
                return;
            }

            if (title == "Symbol Info")
            {
                var symbols = result is JArray values ? values.OfType<JObject>() : new[] { result }.OfType<JObject>();
                var properties = symbols.SelectMany(symbol => symbol.Properties()).Select(property => new {
                    Name = property.Name,
                    Value = property.Value.ToString(Newtonsoft.Json.Formatting.None),
                }).ToArray();
                _properties.ItemsSource = properties;
                if (properties.Length == 0)
                {
                    _status.Text += "：没有结果。";
                    ShowPropertyMessage("没有结果。");
                }
                return;
            }

            var generation = _generation;
            var client = ClangdLanguageClient.Current;
            var sessionVersion = client?.SessionVersion ?? 0;
            foreach (var item in result is JArray array ? array.AsEnumerable() : new[] { result })
            {
                if (IsEmpty(item))
                    continue;
                _tree.Items.Add(title == "Type Hierarchy"
                                    ? BuildHierarchyRoot(item, generation, client, sessionVersion)
                                    : BuildNode(item, generation));
            }
            if (_tree.Items.Count == 0)
            {
                _status.Text += "：没有结果。";
                ShowPropertyMessage("没有结果。");
            }
            else
            {
                foreach (TreeViewItem root in _tree.Items)
                    root.IsExpanded = true;
                var first = (TreeViewItem)_tree.Items[0];
                first.IsSelected = true;
                ShowNodeProperties(first);
            }
        }

        internal void ShowFailure(string title, Exception exception)
        {
            if (_disposed)
                return;
            ResetResult();
            _tabs.SelectedIndex = title == "Symbol Info" ? 1 : 0;
            _status.Text = title + "：" + exception.Message;
            ShowPropertyMessage(_status.Text);
        }

        private void ResetResult()
        {
            _resultCurrent = null;
            ++_generation;
            var previous = _requests;
            _requests = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            _tree.Items.Clear();
            _properties.ItemsSource = null;
        }

        internal void InvalidateResult()
        {
            if (_disposed)
                return;
            ResetResult();
            _status.Text = "查询已失效，请重新查询。";
            ShowPropertyMessage(_status.Text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                ++_generation;
                _requests.Cancel();
                _requests.Dispose();
                _resultCurrent = null;
                _tree.Items.Clear();
                _properties.ItemsSource = null;
            }
            base.Dispose(disposing);
        }

        private bool IsCurrent(long generation) => !_disposed && generation == _generation &&
            (_resultCurrent == null || _resultCurrent());

        private static bool IsEmpty(JToken token) => token == null || token.Type == JTokenType.Null ||
            token.Type == JTokenType.Undefined || token is JContainer container && !container.HasValues;

        private static string Text(JToken value) => value is JValue ? value.ToString() : null;

        private static string NodeTitle(JToken token, bool hierarchy = false)
        {
            if (token is JObject obj)
            {
                var name = Text(obj["name"]);
                if (hierarchy)
                    return string.IsNullOrWhiteSpace(name) ? "（未命名类型）" : name;
                var title = string.Join(" ", new[] { Text(obj["kind"]), name, Text(obj["detail"]) }
                                                   .Where(value => !string.IsNullOrWhiteSpace(value)));
                return string.IsNullOrWhiteSpace(title) ? "（未命名节点）" : title;
            }
            return token is JValue ? token.ToString() : "数组";
        }

        private TreeViewItem BuildNode(JToken token, long generation)
        {
            var node = CreateNode(NodeTitle(token), token);
            if (token is JObject objectToken)
            {
                // Do not serialize children into a tooltip: ASTs can be very large/deep.
                var tooltip = new ToolTip { Resources = ((FrameworkElement)Content).Resources,
                    Content = string.Join(Environment.NewLine, objectToken.Properties()
                    .Where(property => property.Value is JValue && property.Name != "detail")
                    .Select(property => property.Name + ": " + Text(property.Value))) };
                tooltip.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.LargeToolTipStyleKey);
                tooltip.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolTipBrushKey);
                tooltip.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolTipTextBrushKey);
                tooltip.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolTipBorderBrushKey);
                node.ToolTip = tooltip;
                var children = objectToken.Properties().Where(property =>
                    (property.Name == "children" || property.Name == "supertypes" || property.Name == "subtypes") &&
                    !IsEmpty(property.Value)).ToArray();
                if (children.Length != 0)
                {
                    AddLazyChildren(node, generation, () =>
                    {
                        foreach (var property in children)
                        {
                            if (property.Name == "children")
                            {
                                AddMemoryChildren(node, property.Value, generation);
                                continue;
                            }
                            var group = CreateNode(property.Name == "supertypes" ? "基类型（supertypes）" : "派生类型（subtypes）");
                            AddLazyChildren(group, generation, () => AddMemoryChildren(group, property.Value, generation));
                            node.Items.Add(group);
                        }
                    });
                }
            }
            else if (token is JArray values && values.Count != 0)
            {
                AddLazyChildren(node, generation, () => AddMemoryChildren(node, values, generation));
            }
            return node;
        }

        private void AddMemoryChildren(TreeViewItem node, JToken values, long generation)
        {
            foreach (var child in values is JArray array ? array.AsEnumerable() : new[] { values })
                if (!IsEmpty(child))
                    node.Items.Add(BuildNode(child, generation));
        }

        private void AddLazyChildren(TreeViewItem node, long generation, Action populate)
        {
            node.Items.Add(CreateNode("展开以加载…"));
            RoutedEventHandler expanded = null;
            expanded = (sender, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, node) || !IsCurrent(generation))
                    return;
                node.Expanded -= expanded;
                node.Items.Clear();
                populate();
            };
            node.Expanded += expanded;
        }

        private TreeViewItem BuildHierarchyRoot(JToken value, long generation, ClangdLanguageClient client,
                                                long sessionVersion)
        {
            var item = (value as JObject)?["item"] ?? value;
            var node = CreateNode(NodeTitle(item, true), item);
            var path = new HashSet<string>(StringComparer.Ordinal) { HierarchyIdentity(item) };
            foreach (var supertypes in new[] { true, false })
            {
                var direction = supertypes ? "supertypes" : "subtypes";
                var group = CreateNode(supertypes ? "基类型（supertypes）" : "派生类型（subtypes）");
                AddHierarchyLoader(group, item, supertypes, (value as JObject)?[direction], path, generation, client,
                                   sessionVersion);
                node.Items.Add(group);
            }
            return node;
        }

        private TreeViewItem BuildHierarchyChild(JToken item, bool supertypes, HashSet<string> ancestors,
                                                long generation, ClangdLanguageClient client, long sessionVersion)
        {
            var node = CreateNode(NodeTitle(item, true), item);
            var identity = HierarchyIdentity(item);
            if (ancestors.Contains(identity))
            {
                node.Header += "（循环引用）";
                return node;
            }
            var path = new HashSet<string>(ancestors, StringComparer.Ordinal) { identity };
            AddHierarchyLoader(node, item, supertypes, null, path, generation, client, sessionVersion);
            return node;
        }

        private static string HierarchyIdentity(JToken item)
        {
            var obj = item as JObject;
            var start = (obj?["selectionRange"] ?? obj?["range"])?["start"];
            // A path-local identity allows diamonds while stopping recursion back into an ancestor.
            return string.Join("\n", Text(obj?["uri"]), Text(obj?["name"]),
                               Text(start?["line"]), Text(start?["character"]));
        }

        private bool CanRequest(long generation, ClangdLanguageClient client, long sessionVersion) => IsCurrent(generation) &&
            ClangdSettings.Instance.TakeOverFromNativeIntelliSense && ClangdSettings.Instance.EnableTypeHierarchy &&
            client != null && client.IsRunning && client.SessionVersion == sessionVersion &&
            ReferenceEquals(client, ClangdLanguageClient.Current);

        private void AddHierarchyLoader(TreeViewItem node, JToken item, bool supertypes, JToken prepared,
                                        HashSet<string> path, long generation, ClangdLanguageClient client,
                                        long sessionVersion)
        {
            node.Items.Add(CreateNode("展开以加载…"));
            var loading = false;
            var loaded = false;
            node.Expanded += async (sender, args) =>
            {
                // Expanded is routed: an ancestor must not reload when a descendant expands.
                if (!ReferenceEquals(args.OriginalSource, node) || loading || loaded || !IsCurrent(generation))
                    return;
                if (!CanRequest(generation, client, sessionVersion))
                {
                    SetMessage(!ClangdSettings.Instance.EnableTypeHierarchy ? "Type Hierarchy 未启用。" :
                               "clangd 客户端未运行或已更换，请重新查询。");
                    return;
                }
                if (prepared != null && prepared.Type != JTokenType.Null)
                {
                    Populate(prepared is JArray array ? array.AsEnumerable() : new[] { prepared });
                    loaded = true;
                    return;
                }
                loading = true;
                SetMessage("正在加载…");
                var token = _requests.Token;
                try
                {
                    var children = await client.RequestHierarchyChildrenAsync(item, supertypes, token)
                                               .ConfigureAwait(false);
                    await node.Dispatcher.InvokeAsync(() =>
                    {
                        if (!CanRequest(generation, client, sessionVersion) || token.IsCancellationRequested)
                            return;
                        Populate(children);
                        loaded = true;
                    });
                }
                catch (OperationCanceledException)
                {
                    await node.Dispatcher.InvokeAsync(() =>
                    {
                        if (CanRequest(generation, client, sessionVersion) && !token.IsCancellationRequested)
                            SetMessage("加载已取消，折叠后展开可重试。");
                    });
                }
                catch (Exception exception)
                {
                    await node.Dispatcher.InvokeAsync(() =>
                    {
                        if (CanRequest(generation, client, sessionVersion) && !token.IsCancellationRequested)
                            SetMessage("加载失败：" + exception.Message + "（折叠后展开可重试）");
                    });
                }
                finally
                {
                    // This flag is only read on the dispatcher owning the node.
                    await node.Dispatcher.InvokeAsync(() => loading = false);
                }
            };

            void SetMessage(string message)
            {
                node.Items.Clear();
                node.Items.Add(CreateNode(message));
            }

            void Populate(IEnumerable<JToken> children)
            {
                node.Items.Clear();
                foreach (var child in children ?? Enumerable.Empty<JToken>())
                    if (!IsEmpty(child))
                        node.Items.Add(BuildHierarchyChild(child, supertypes, path, generation, client, sessionVersion));
                if (node.Items.Count == 0)
                    SetMessage("没有结果。");
            }
        }
    }
}
