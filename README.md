# ClangdVS

> **声明**：本项目由生成式 AI 生成，可能存在未经人工审查的部分。使用前请自行审查代码。

在 Visual Studio 中使用 [clangd](https://clangd.llvm.org) 语言服务器编辑 C/C++ 代码。

ClangdVS 让 Visual Studio 2022/2026 通过 clangd 获得代码补全、悬停提示、诊断、语义着色、导航、格式化和代码操作，并支持一键接管内置的 C/C++ IntelliSense 引擎。功能设计参照 [vscode-clangd](https://github.com/clangd/vscode-clangd)。

## 功能

| 功能 | 说明 |
|---|---|
| 补全与悬停 | 代码补全、函数签名与 Quick Info |
| 诊断 | 波浪线与错误列表条目，由 clangd 提供 |
| 语义着色 | 基于 clangd 语义分类的高亮 |
| 导航 | 跳转定义、查找引用、符号导航 |
| 格式化 | 文档与选区格式化 |
| 代码操作 | 快速修复、clang-tidy 修复与重构 |
| **Alt+O 头/源切换** | 在头文件与源文件之间快速切换（同 vscode-clangd 快捷键） |
| **接管原生 IntelliSense** | 一键禁用或恢复 Visual Studio 内置 C/C++ 引擎，随时可逆 |
| 非活动区域淡化 | 淡化当前编译配置未启用的 `#if` / `#else` 代码块 |
| Inlay Hints | 在调用参数与推导类型旁显示行内提示 |
| AST / Symbol Info / Type Hierarchy | 在内置工具窗口中查看光标处的语法结构，默认关闭 |

## 环境要求

- **Visual Studio 2022 或 2026**（含 Community / Professional / Enterprise，仅 64 位）
- 已安装“Visual Studio 核心编辑器”组件
- **clangd**，以下任一方式提供即可（自动检测）：
  1. VS Installer 中的 **“适用于 Windows 的 C++ Clang 工具”** 组件
  2. [LLVM 官方发行版](https://releases.llvm.org)（需在 PATH 中）
  3. 在选项页中手动指定 `clangd.exe` 路径

## 安装

1. 双击 `ClangdVS.vsix` 安装
2. 重启 Visual Studio
3. 打开任意 `.cpp` / `.h` 文件

## 快速开始

clangd 依据**编译数据库**理解你的代码。为了让补全与跳转准确，请为项目生成 `compile_commands.json`：

| 构建系统 | 做法 |
|---|---|
| CMake | 配置时开启 `CMAKE_EXPORT_COMPILE_COMMANDS` |
| MSBuild / Visual Studio | 使用 [msbuild-compdb](https://github.com/max-wolf-cpp/msbuild-compdb) 等工具生成 |
| 其他 | 用 `.clangd` 文件中的 `CompileFlags.Add` 提供兜底编译标志 |

clangd 会自动向上级目录查找 `compile_commands.json`，因此把它放在项目根目录即可。

## 使用

### 接管原生 IntelliSense

内置的 C/C++ IntelliSense 与 clangd 会同时工作、互相干扰。开启接管后，原生引擎的补全、QuickInfo、成员列表、波浪线、错误列表条目与 VCR linter 提示会立即停用，完全交由 clangd 接管：

> 菜单 **工具 → clangd 语言服务 → 接管编辑器（禁用原生 IntelliSense）**

再次点击同一菜单项即可**完全还原**原生行为（原有设置会被备份）。

### 菜单命令

菜单 **工具 → clangd 语言服务** 提供：

| 命令 | 快捷键 | 说明 |
|---|---|---|
| 切换头文件/源文件 | `Alt+O` | 在对应的头文件与源文件之间切换（编辑器右键菜单中同样可用） |
| 重启语言服务器 | | 重新启动 clangd 进程 |
| 接管编辑器（禁用原生 IntelliSense） | | 切换接管状态 |
| 查看 clangd AST | | 查看光标处的 clang AST |
| 查看 Symbol Info | | 查看光标处符号的标识信息 |
| 查看 Type Hierarchy | | 查看类型的基类与派生类关系 |

后三项需要先在选项中启用，详见下节。

## 设置

> **工具 → 选项 → clangd**

设置保存在 `%LOCALAPPDATA%\ClangdVS\settings.json`。该文件被外部修改时会自动重新载入。

### 服务器

| 设置 | 默认值 | 说明 |
|---|---|---|
| clangd 路径 | `clangd` | `clangd.exe` 的完整路径。保留默认值时，依次从 PATH、Visual Studio 附带的 LLVM 组件及标准安装位置查找 |
| 启动参数 | 空 | 传给 clangd 的附加命令行参数，以空格分隔，例如 `--background-index` |
| 后备编译标志 | 空 | 未找到编译数据库时用于解析文件的附加 clang 标志 |
| 性能跟踪文件 | 空 | 可选。clangd 性能跟踪日志的输出文件（Chrome trace-viewer JSON 格式） |
| 崩溃后自动重启 | 开 | clangd 意外退出后自动重新启动 |

以上路径类设置支持变量展开：`${userHome}`、`${programFiles}`、`${localAppData}`、`${env:VCINSTALLDIR}`、`${env:VSINSTALLDIR}`。

### IntelliSense 接管

| 设置 | 默认值 | 说明 |
|---|---|---|
| 接管原生 IntelliSense | 关 | 关闭内置 C/C++ IntelliSense 并改由 clangd 提供；关闭该选项会停止 clangd 并恢复原生引擎 |

### 编辑体验

| 设置 | 默认值 | 说明 |
|---|---|---|
| 补全和悬停 | 开 | clangd 代码补全与 Quick Info 悬停信息 |
| 诊断 | 开 | clangd 诊断、波浪线与错误列表条目 |
| 语义着色 | 开 | clangd 语义分类与语义高亮 |
| 格式化 | 开 | 文档与选区格式化 |
| 代码操作 | 开 | 快速修复、clang-tidy 修复与重构操作 |
| 导航 | 开 | 定义、引用、符号导航与头/源切换 |

### clangd 扩展能力

| 设置 | 默认值 | 说明 |
|---|---|---|
| 非活动预处理器区域淡化 | 开 | 淡化当前编译配置中未启用的 `#if` / `#else` 区域 |
| Inlay Hints | 开 | 在调用参数与推导类型旁显示行内提示 |
| AST 查看 | 关 | 启用“查看 clangd AST”命令与工具窗口 |
| Symbol Info | 关 | 启用“查看 Symbol Info”命令 |
| Type Hierarchy | 关 | 启用“查看 Type Hierarchy”命令 |

### 诊断

| 设置 | 默认值 | 说明 |
|---|---|---|
| 写入日志文件 | 开 | 将 clangd 输出与扩展诊断信息写入日志文件 |

## 日志与排障

日志文件：`%LOCALAPPDATA%\ClangdVS\clangd.log`（达到 5 MB 后自动轮转）

clangd 无法启动时，请依次确认：

1. 菜单能否正常打开（若不能，扩展可能未正确加载）
2. 选项中设置的 `clangd.exe` 路径是否正确
3. 若留空自动检测，确认 clangd 已在 PATH 中，或已安装 VS 的“C++ Clang 工具”组件
4. 查看日志文件中的错误信息

## 从源码构建

需要 .NET SDK 与 Visual Studio（提供 VSCT、CreatePkgDef 等 SDK 工具）。

```powershell
./build.ps1                      # Debug
./build.ps1 -Configuration Release
```

产物输出到 `src/ClangdVS/bin/<Debug|Release>/net472/ClangdVS.vsix`。

也可以直接调用 MSBuild，或用 Visual Studio 打开 `ClangdVS.sln` 构建：

```powershell
dotnet build src/ClangdVS/ClangdVS.csproj -c Release
```

## 运行测试

回归测试覆盖协议解析、设置应用、接管生命周期与编辑器/工具窗口行为，其中部分用例需要真实 clangd：

```powershell
pwsh -File tests/RegressionHarness/run.ps1
```

默认会先构建 Release 版扩展；若已构建，可加 `-SkipExtensionBuild` 跳过。

## 已知限制

- **VS 2022（17.x）兼容性尚未实机验证**。扩展清单允许安装到 VS 2022，但目前的验证均在 VS 2026 上完成，因此暂不声明对 VS 2022 的兼容性。
- clangd 与内置 C/C++ IntelliSense **可能同时工作并互相干扰**，建议开启接管以获得一致体验。
- 关闭“代码操作”会拦截后续的代码操作请求，但**已交给 Visual Studio、由宿主本地执行的编辑无法撤销**，因此无法保证关闭后所有既有操作立即失效。
- 官方文档明确 LSP 客户端“并非为替换现有语言服务设计”；本项目通过接管开关实际达成了替换，但这一路线依赖 Visual Studio 2026 的内部行为，未来版本可能失效。
- 接管功能会修改 Visual Studio 的 C/C++ IntelliSense 设置（修改前自动备份，可完整还原）。

## 许可

MIT，详见 [`LICENSE.txt`](LICENSE.txt)。移植自 [vscode-clangd](https://github.com/clangd/vscode-clangd)，Copyright (c) 2019 The LLVM Developers（MIT）。
