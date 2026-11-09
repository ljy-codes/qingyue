# 文本核心行为测试

2026-11-09。分支 `feat/windows-client`，直接在共享工作区完成，未创建线程、worktree 或 commit，未修改 Swift、共享契约或 UI。

## 运行

在仓库根目录运行：

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'
dotnet run --project '开发空间/Windows/tests/Qingyue.TextTests/Qingyue.TextTests.csproj' -c Release
```

无需第三方包，目标 .NET 10。每组独立执行，失败打印原因并返回 1。
初始缺失实现时，使用同一套真实行为测试观察到 `0/17 passed; 17 failed`，退出 1。
追加无路径 Markdown、Mac 格式探测和捕获组别名回归时，也分别观察到失败后再修复。
最终正常项目引用验证：`23/23 passed; 0 failed`，退出 0。

Core 尚未提供文件类型或并行修改导致 Core 无法编译时，可隔离运行：

```powershell
dotnet run --project '开发空间/Windows/tests/Qingyue.TextTests/Qingyue.TextTests.csproj' -c Release -p:TextOnlyBootstrap=true
```

该模式直接链接实际文本源码和实际 `DocumentTypes.cs`；仅用测试占位类型满足契约中的 `DocumentFile` 类型引用，不模拟文件行为。最终验收使用正式 Core 项目引用。

## 实际接口与约定

命名空间 `Qingyue.Core`。`DocumentTypes.cs` 无需变更。

```csharp
TextKind TextServices.DetectKind(string? path, string text);
string? TextServices.Diagnostic(string text, TextKind kind);
string TextServices.FormatJson(string text);

const int SearchEngine.MaximumTextLength = 5_000_000;
IReadOnlyList<SearchMatch> SearchEngine.Find(
    string text, string query, string replacement, SearchOptions options);
string SearchEngine.ReplaceAll(string text, IReadOnlyList<SearchMatch> matches);

ReadingModel ReadingModel.Build(string text, TextKind kind);
string ReadingModel.VisibleText { get; }
IReadOnlyList<ReadingBlock> ReadingModel.Blocks { get; }
IReadOnlyList<ReadingHeading> ReadingModel.Headings { get; }
int ReadingModel.SourceToDisplay(int offset);
int ReadingModel.DisplayToSource(int offset);
```

- 搜索、块和目录均使用 UTF-16 偏移；不返回切断有效 surrogate pair 的搜索范围。非法替换范围会抛中文 `ArgumentException`，先校验所有范围再构造结果。
- 搜索查询最多 4096、替换模板及单项展开最多 100 万、匹配最多 10 万、最终结果最多 1000 万 UTF-16 单元。正则使用 .NET 350 ms 匹配超时，超时转换为中文 `InvalidOperationException`。
- 字面替换不解释 `$` 或反斜杠；正则支持数字、命名捕获及 .NET 特殊替换 token，包括数字别名、前导零和稀疏组号，并在展开前限制分配。
- 所有块用一个 `\n` 连接；每块 `DisplayStart` 精确对应 `VisibleText`。Plain/JSON/JSONC/YAML/INI/XML 显示原文并保持 identity 映射；Markdown 是明确的块级近似映射，不能据此对任意字符做反向精确编辑。
- 无路径/未知扩展名支持内容探测，包含 Markdown 标题；显式 `.txt/.log/.csv/.tsv` 保持 Plain。
- JSON 格式化不经浮点转换，保留数值原词法与重复键；字符串转义可能由 `Utf8JsonWriter` 规范化。JSONC 仅检查，不格式化。JSON 深度上限 256。
- YAML 仅检查缩进 Tab，返回 null 不表示完整 YAML 语法正确。XML 禁止 DTD/外部实体。
- Markdown 是轻量解析器，不是完整 CommonMark/GFM；支持基础标题、段落、列表及任务、引用、围栏、分隔线、表格与常见行内语法。HTML 作为文字，图片只输出占位文字，不加载网络。
- 富文本超过 200,000 UTF-16 单元原文降级且不提取目录；目录最多 500 项；表格最多 32 列、1024 格，超限退回文字。映射空显示文本时偏移 0 优先对应源起点。

## WPF 只读运行审查

```powershell
dotnet run --project '开发空间/Windows/tests/Qingyue.TextTests/Audit/WpfAudit.csproj' -c Release
```

直接链接真实 UI `ReadingRenderer.cs`，只创建文档对象，不显示窗口、写偏好或操作会话。
Plain 中文/emoji/多行、Markdown 跨块与内部换行、表格测试结果：`WPF renderer mapping audit: 0 failures`，退出 0。
WPF 块间文本导出使用 CRLF；渲染器的块内 Run 指针映射在这些样本中与模型一致。
审查发现与建议见 `WPF-review.md`。此检查不覆盖实际键盘、IME、窗口布局或长文件交互延迟。

## 修改范围与状态

生产文件仅 `TextServices.cs`、`SearchEngine.cs`、`ReadingModel.cs`。
测试及辅助文件仅本目录。Core 编译时曾被另一 worker 的 `DocumentFile` 临时缺失/错误阻塞，随后正式验证已通过。
兼容性：公开契约保持不变；所有保护性失败均不产生部分替换输出。
最终集成状态：UI 审查列出的五项问题均已修正；阅读锚点、目录更新、仅查找与替换隔离已纳入真实 WPF 流程回归，
文本位置索引另有 5 组检查。格式检查界面明确区分 YAML 有限检查与未提供完整校验的类型。
审查历史及处理结果见 `WPF-review.md`，完整构建验收见仓库 `docs/test.md`。本文本模块无待实现的约定接口。
