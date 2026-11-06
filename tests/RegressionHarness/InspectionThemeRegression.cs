using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClangdVS;
using Microsoft.VisualStudio.PlatformUI;
using Newtonsoft.Json.Linq;

// Exercise production templates, never replacement styles. Optionally merge the installed
// VS dictionary in this independent STA process to reproduce the host's ambient styles.
internal static class InspectionThemeRegression
{
    internal static void Tabs()
    {
        WithWindow((window, host, resources) =>
        {
            var tabs = Field<TabControl>(window, "_tabs");
            var structure = (TabItem)tabs.Items[0];
            var properties = (TabItem)tabs.Items[1];
            resources[EnvironmentColors.ToolWindowTabGradientBrushKey] = Brushes.MidnightBlue;
            resources[EnvironmentColors.ToolWindowTabTextBrushKey] = Brushes.Linen;
            resources[EnvironmentColors.ToolWindowTabBorderBrushKey] = Brushes.SteelBlue;
            resources[EnvironmentColors.ToolWindowTabSelectedTabBrushKey] = Brushes.Navy;
            resources[EnvironmentColors.ToolWindowTabSelectedTextBrushKey] = Brushes.Ivory;
            resources[EnvironmentColors.ToolWindowTabSelectedBorderBrushKey] = Brushes.Coral;
            resources[EnvironmentColors.ToolWindowTabMouseOverBackgroundGradientBrushKey] = Brushes.DarkGreen;
            resources[EnvironmentColors.ToolWindowTabMouseOverTextBrushKey] = Brushes.Beige;
            host.UpdateLayout();
            AssertText(properties, "属性", Brushes.Linen, "Unselected tab header");
            AssertPaint(properties, "属性", Brushes.MidnightBlue, Brushes.Linen, "Normal tab header");
            AssertPaint(structure, "结构", Brushes.Navy, Brushes.Ivory, "Selected tab header");
            var selectedBorder = Visuals<Border>(structure).First();
            Assert(ReferenceEquals(selectedBorder.BorderBrush, Brushes.Coral) &&
                   selectedBorder.Padding == structure.Padding && selectedBorder.BorderThickness == structure.BorderThickness,
                   "Tab template lost its themed border or padding");
            Assert(tabs.TabStripPlacement == Dock.Top && properties.ActualWidth < 200,
                   "Structure/properties still use the 200px left-navigation layout");
            Assert(Visuals<TreeView>(tabs).Any(), "Selected structure content was not presented");
            SetMouseOver(properties, true);
            AssertPaint(properties, "属性", Brushes.DarkGreen, Brushes.Beige, "Hovered tab header");
            resources[EnvironmentColors.ToolWindowTabMouseOverBackgroundGradientBrushKey] = Brushes.DarkCyan;
            resources[EnvironmentColors.ToolWindowTabMouseOverTextBrushKey] = Brushes.Yellow;
            AssertPaint(properties, "属性", Brushes.DarkCyan, Brushes.Yellow, "Updated hover header");
            SetMouseOver(properties, false);
            resources[EnvironmentColors.ToolWindowTabTextBrushKey] = Brushes.LightPink;
            resources[EnvironmentColors.ToolWindowTabSelectedTabBrushKey] = Brushes.DarkRed;
            resources[EnvironmentColors.ToolWindowTabSelectedTextBrushKey] = Brushes.White;
            AssertText(properties, "属性", Brushes.LightPink, "Updated normal header");
            AssertPaint(structure, "结构", Brushes.DarkRed, Brushes.White, "Updated selected header");

            structure.Focus();
            structure.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(host), 0, Key.End) { RoutedEvent = Keyboard.KeyDownEvent });
            host.UpdateLayout();
            Assert(properties.IsSelected && Visuals<DataGrid>(tabs).Any(),
                   "Tab keyboard navigation did not switch the selected content");
            AssertPaint(properties, "属性", Brushes.DarkRed, Brushes.White, "New selected header");
            AssertText(structure, "结构", Brushes.LightPink, "Previously selected header");
            Assert(Visuals<ContentPresenter>(properties).Any(p => p.Content?.ToString() == "属性"),
                   "Tab header does not preserve ContentPresenter support");
            resources[EnvironmentColors.SystemGrayTextBrushKey] = Brushes.Gray;
            structure.IsEnabled = false;
            AssertText(structure, "结构", Brushes.Gray, "Disabled tab header");
        });
    }

    internal static void Tree()
    {
        WithWindow((window, host, resources) =>
        {
            var tree = Field<TreeView>(window, "_tree");
            var node = (TreeViewItem)tree.Items[0];
            var child = (TreeViewItem)node.Items[0];
            resources[TreeViewColors.BackgroundBrushKey] = Brushes.DarkSlateGray;
            resources[TreeViewColors.BackgroundTextBrushKey] = Brushes.Wheat;
            resources[TreeViewColors.SelectedItemInactiveBrushKey] = Brushes.Navy;
            resources[TreeViewColors.SelectedItemInactiveTextBrushKey] = Brushes.Ivory;
            resources[TreeViewColors.SelectedItemActiveBrushKey] = Brushes.DarkRed;
            resources[TreeViewColors.SelectedItemActiveTextBrushKey] = Brushes.Yellow;
            resources[EnvironmentColors.SystemGrayTextBrushKey] = Brushes.Gray;
            resources[TreeViewColors.GlyphBrushKey] = Brushes.Bisque;
            resources[TreeViewColors.SelectedItemInactiveGlyphBrushKey] = Brushes.Coral;
            resources[TreeViewColors.SelectedItemActiveGlyphBrushKey] = Brushes.Lime;
            ((TabItem)Field<TabControl>(window, "_tabs").Items[0]).Focus();
            host.UpdateLayout();
            AssertPaint(node, "Decl Themed", Brushes.Navy, Brushes.Ivory, "Inactive tree selection");
            AssertText(child, "Child", Brushes.Wheat, "Normal child header");
            Assert(ReferenceEquals(tree.Background, Brushes.DarkSlateGray), "Tree background is not themed");
            AssertGlyph(node, Brushes.Coral, "Inactive expander");
            Assert(node.Focus(), "Tree node could not receive keyboard focus");
            host.UpdateLayout();
            AssertPaint(node, "Decl Themed", Brushes.DarkRed, Brushes.Yellow, "Active tree selection");
            AssertGlyph(node, Brushes.Lime, "Active expander");
            resources[TreeViewColors.SelectedItemActiveBrushKey] = Brushes.DarkGreen;
            resources[TreeViewColors.SelectedItemActiveTextBrushKey] = Brushes.White;
            resources[TreeViewColors.SelectedItemActiveGlyphBrushKey] = Brushes.Aqua;
            resources[TreeViewColors.BackgroundTextBrushKey] = Brushes.LightPink;
            AssertPaint(node, "Decl Themed", Brushes.DarkGreen, Brushes.White, "Updated active selection");
            AssertGlyph(node, Brushes.Aqua, "Updated active expander");
            AssertText(child, "Child", Brushes.LightPink, "Updated normal child header");
            var expander = Visuals<ToggleButton>(node).First();
            Assert(expander.Visibility == Visibility.Visible && expander.IsChecked == true,
                   "Expanded node has no working expander");
            Assert(Visuals<System.Windows.Shapes.Path>(expander).Single().RenderTransform is RotateTransform rotation &&
                   rotation.Angle == 90, "Expanded glyph did not rotate");
            expander.IsChecked = false;
            host.UpdateLayout();
            Assert(!node.IsExpanded && !child.IsVisible, "Expander collapse did not hide descendants");
            node.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(host), 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            host.UpdateLayout();
            Assert(node.IsExpanded && child.IsVisible, "Keyboard expansion lost children");
            Assert(child.Items.Count == 1 && ((TreeViewItem)child.Items[0]).Header.ToString() == "展开以加载…",
                   "Template eagerly loaded lazy descendants");
            child.IsExpanded = true;
            host.UpdateLayout();
            Assert(((TreeViewItem)child.Items[0]).Header.ToString() == "Grandchild",
                   "Expanding a rendered child did not load its descendants");
            var leaf = (TreeViewItem)child.Items[0];
            Assert(Visuals<ToggleButton>(leaf).Single().Visibility == Visibility.Hidden,
                   "Leaf node still displays an expander");
            node.IsEnabled = false;
            host.UpdateLayout();
            AssertText(node, "Decl Themed", Brushes.Gray, "Disabled node header");
            AssertGlyph(node, Brushes.Gray, "Disabled expander");
            resources[EnvironmentColors.SystemGrayTextBrushKey] = Brushes.Silver;
            AssertText(node, "Decl Themed", Brushes.Silver, "Updated disabled header");
            AssertGlyph(node, Brushes.Silver, "Updated disabled expander");
            node.IsEnabled = true;
            ((TabItem)Field<TabControl>(window, "_tabs").Items[0]).Focus();
            resources[TreeViewColors.SelectedItemInactiveBrushKey] = Brushes.DarkMagenta;
            resources[TreeViewColors.SelectedItemInactiveTextBrushKey] = Brushes.Linen;
            host.UpdateLayout();
            AssertPaint(node, "Decl Themed", Brushes.DarkMagenta, Brushes.Linen, "Updated inactive selection");
        });
    }

    private static void WithWindow(Action<ClangdInspectionToolWindow, Window, ResourceDictionary> test)
    {
        using (var window = new ClangdInspectionToolWindow())
        {
            var panel = (FrameworkElement)window.Content;
            var ide = Environment.GetEnvironmentVariable("CLANGDVS_TEST_VS_IDE");
            ResolveEventHandler resolver = (sender, args) =>
            {
                foreach (var dir in new[] { ide, Path.Combine(ide, "PublicAssemblies"), Path.Combine(ide, "PrivateAssemblies") })
                {
                    var path = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            var host = new Window { Content = panel, Width = 600, Height = 400,
                Left = -20000, Top = -20000, ShowInTaskbar = false };
            try
            {
                if (!string.IsNullOrEmpty(ide))
                {
                    AppDomain.CurrentDomain.AssemblyResolve += resolver;
                    Assembly.Load("Microsoft.VisualStudio.Platform.WindowManagement");
                    panel.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(
                        "/Microsoft.VisualStudio.Platform.WindowManagement;component/Themes/Resources.xaml", UriKind.Relative) });
                    Console.WriteLine("Theme regression using installed VS Resources.xaml: " + ide);
                }
                panel.Resources[EnvironmentColors.ToolWindowBackgroundBrushKey] = Brushes.MidnightBlue;
                panel.Resources[EnvironmentColors.ToolWindowTextBrushKey] = Brushes.Linen;
                window.ShowResult("AST", JObject.Parse("{\"kind\":\"Decl\",\"name\":\"Themed\",\"children\":[" +
                    "{\"kind\":\"Child\",\"children\":[{\"kind\":\"Grandchild\"}]}]}"));
                host.Show();
                host.UpdateLayout();
                test(window, host, panel.Resources);
            }
            finally
            {
                host.Content = null;
                host.Close();
                if (!string.IsNullOrEmpty(ide)) AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
        }
    }

    private static void AssertPaint(DependencyObject owner, string text, Brush background, Brush foreground, string message)
    {
        var visualText = Visuals<TextBlock>(owner).Single(t => t.Text == text);
        Assert(ReferenceEquals(visualText.Foreground, foreground), message + " text: " + visualText.Foreground);
        var parent = VisualTreeHelper.GetParent(visualText);
        while (parent != null && parent != owner)
        {
            if (parent is Border border && ReferenceEquals(border.Background, background)) return;
            parent = VisualTreeHelper.GetParent(parent);
        }
        throw new InvalidOperationException(message + " has no painted border using " + background);
    }

    private static void AssertText(DependencyObject owner, string text, Brush brush, string message) =>
        Assert(ReferenceEquals(Visuals<TextBlock>(owner).Single(t => t.Text == text).Foreground, brush),
               message + " does not draw text with " + brush);

    private static void AssertGlyph(DependencyObject node, Brush brush, string message) =>
        Assert(ReferenceEquals(Visuals<System.Windows.Shapes.Path>(Visuals<ToggleButton>(node).First()).Single().Fill, brush),
               message + " does not draw its glyph with " + brush);

    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }

    private static void SetMouseOver(Control control, bool value) => control.SetValue(
        (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
            .GetValue(null), value);
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
