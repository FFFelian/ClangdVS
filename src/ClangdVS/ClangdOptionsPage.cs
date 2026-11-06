using System;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace ClangdVS
{
    /// <summary>
    /// Tools > Options > clangd page. Property text is localized to Chinese (key technical terms kept in English),
    /// mirroring the clangd.* configuration section of vscode-clangd.
    /// </summary>
    public class ClangdOptionsPage : DialogPage
    {
        [Category("服务器")]
        [DisplayName("clangd 路径")]
        [Description("clangd 可执行文件的完整路径。保留默认值 clangd 时，将依次从 PATH 环境变量、Visual Studio " +
                     "附带的 LLVM 组件以及默认安装位置中查找。")]
        public string ClangdPath
        {
            get => ClangdSettings.Instance.ClangdPath;
            set => ClangdSettings.Instance.ClangdPath = value;
        }

        [Category("服务器")]
        [DisplayName("启动参数")]
        [Description("传递给 clangd 的附加命令行参数，多个参数之间用空格分隔，例如 --background-index。")]
        public string Arguments
        {
            get => string.Join(" ", ClangdSettings.Instance.Arguments ?? Array.Empty<string>());
            set => ClangdSettings.Instance.Arguments = ParseArguments(value);
        }

        [Category("服务器")]
        [DisplayName("后备编译标志")]
        [Description(
            "在未找到编译数据库 (compile_commands.json) 时，用于解析文件的附加 clang 标志，多个标志之间用空格分隔。")]
        public string FallbackFlags
        {
            get => string.Join(" ", ClangdSettings.Instance.FallbackFlags ?? Array.Empty<string>());
            set => ClangdSettings.Instance.FallbackFlags = ParseArguments(value);
        }

        [Category("服务器")]
        [DisplayName("性能跟踪文件")]
        [Description("可选。clangd 输出性能跟踪日志的目标文件，采用 Chrome trace-viewer JSON 格式。")]
        public string TraceFile
        {
            get => ClangdSettings.Instance.TraceFile;
            set => ClangdSettings.Instance.TraceFile = value;
        }

        [Category("服务器")]
        [DisplayName("崩溃后自动重启")]
        [Description("clangd 意外退出后自动重新启动。")]
        public bool RestartAfterCrash
        {
            get => ClangdSettings.Instance.RestartAfterCrash;
            set => ClangdSettings.Instance.RestartAfterCrash = value;
        }

        [Category("IntelliSense 接管")]
        [DisplayName("接管原生 IntelliSense")]
        [Description("关闭 Visual Studio 内置的 C/C++ IntelliSense（包括 " +
                     "QuickInfo、成员列表、波浪线和错误列表），改由 clangd 提供这些功能。关闭后停止 clangd " +
                     "并恢复原生 IntelliSense。")]
        public bool TakeOverFromNativeIntelliSense
        {
            get => ClangdSettings.Instance.TakeOverFromNativeIntelliSense;
            set => ClangdSettings.Instance.TakeOverFromNativeIntelliSense = value;
        }

        [Category("编辑体验")]
        [DisplayName("补全和悬停")]
        [Description("启用 clangd 代码补全和 Quick Info 悬停信息。")]
        public bool EnableCompletionAndHover
        {
            get => ClangdSettings.Instance.EnableCompletionAndHover;
            set => ClangdSettings.Instance.EnableCompletionAndHover = value;
        }

        [Category("编辑体验")]
        [DisplayName("诊断")]
        [Description("启用 clangd 诊断、波浪线和错误列表条目。")]
        public bool EnableDiagnostics
        {
            get => ClangdSettings.Instance.EnableDiagnostics;
            set => ClangdSettings.Instance.EnableDiagnostics = value;
        }

        [Category("编辑体验")]
        [DisplayName("语义着色")]
        [Description("启用 clangd 提供的语义分类和语义高亮。")]
        public bool EnableSemanticHighlighting
        {
            get => ClangdSettings.Instance.EnableSemanticHighlighting;
            set => ClangdSettings.Instance.EnableSemanticHighlighting = value;
        }

        [Category("编辑体验")]
        [DisplayName("格式化")]
        [Description("启用 clangd 文档和选区格式化。")]
        public bool EnableFormatting
        {
            get => ClangdSettings.Instance.EnableFormatting;
            set => ClangdSettings.Instance.EnableFormatting = value;
        }

        [Category("编辑体验")]
        [DisplayName("代码操作")]
        [Description("启用 clangd 快速修复、clang-tidy 修复和重构操作。")]
        public bool EnableCodeActions
        {
            get => ClangdSettings.Instance.EnableCodeActions;
            set => ClangdSettings.Instance.EnableCodeActions = value;
        }

        [Category("编辑体验")]
        [DisplayName("导航")]
        [Description("启用 clangd 定义、引用、符号导航和头/源切换。")]
        public bool EnableNavigation
        {
            get => ClangdSettings.Instance.EnableNavigation;
            set => ClangdSettings.Instance.EnableNavigation = value;
        }

        [Category("clangd 扩展能力")]
        [DisplayName("非活动预处理器区域淡化")]
        [Description("淡化当前编译配置中未启用的 #if/#else 代码区域。")]
        public bool EnableInactiveRegions
        {
            get => ClangdSettings.Instance.EnableInactiveRegions;
            set => ClangdSettings.Instance.EnableInactiveRegions = value;
        }

        [Category("clangd 扩展能力")]
        [DisplayName("Inlay Hints")]
        [Description("在调用参数和推导类型旁显示 clangd 行内提示。")]
        public bool EnableInlayHints
        {
            get => ClangdSettings.Instance.EnableInlayHints;
            set => ClangdSettings.Instance.EnableInlayHints = value;
        }

        [Category("clangd 扩展能力")]
        [DisplayName("AST 查看")]
        [Description("启用命令和工具窗口查看光标处的 clang AST。")]
        public bool EnableAstView
        {
            get => ClangdSettings.Instance.EnableAstView;
            set => ClangdSettings.Instance.EnableAstView = value;
        }

        [Category("clangd 扩展能力")]
        [DisplayName("Symbol Info")]
        [Description("启用命令查看光标处符号的 clangd 标识信息。")]
        public bool EnableSymbolInfo
        {
            get => ClangdSettings.Instance.EnableSymbolInfo;
            set => ClangdSettings.Instance.EnableSymbolInfo = value;
        }

        [Category("clangd 扩展能力")]
        [DisplayName("Type Hierarchy")]
        [Description("启用命令查看类型的基类和派生类关系。")]
        public bool EnableTypeHierarchy
        {
            get => ClangdSettings.Instance.EnableTypeHierarchy;
            set => ClangdSettings.Instance.EnableTypeHierarchy = value;
        }

        [Category("诊断")]
        [DisplayName("写入日志文件")]
        [Description("将 clangd 输出与扩展诊断信息记录到 %LOCALAPPDATA%\\ClangdVS\\clangd.log。")]
        public bool LogToOutputWindow
        {
            get => ClangdSettings.Instance.LogToOutputWindow;
            set => ClangdSettings.Instance.LogToOutputWindow = value;
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            ClangdSettings.Instance.SaveAndNotify();
            base.OnApply(e);
        }

        private static string[] ParseArguments(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            // Simple quote-aware splitting - sufficient for flags/arguments.
            var result = new System.Collections.Generic.List<string>();
            var inQuote = false;
            var current = new System.Text.StringBuilder();
            foreach (var ch in value.Trim())
            {
                if (ch == '"')
                {
                    inQuote = !inQuote;
                }
                else if (char.IsWhiteSpace(ch) && !inQuote)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }

            return result.ToArray();
        }
    }
}
