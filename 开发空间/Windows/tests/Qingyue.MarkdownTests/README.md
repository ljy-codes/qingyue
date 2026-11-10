# Markdown 独立预览与着色回归

源码仅涉及 `ReadingRenderer.cs`、`MarkdownPreview.cs` 和 `MarkdownSyntax.cs`。测试项目直接链接这些源码，不依赖主窗口，不修改 UITests 或 Build2Checks。

## 接入 API

命名空间：`Qingyue.Windows`。在 WPF UI 线程使用：

```csharp
var preview = new MarkdownPreview(text, fontSize: 16, readingWidth: 680);
preview.Update(updatedText);

foreach (var token in MarkdownSyntax.Tokens(editorLine))
    formattedText.SetForegroundBrush(
        (Brush)TryFindResource(token.ResourceKey), token.Start, token.Length);
```

- `MarkdownPreview` 是 `public sealed RichTextBox`，只读、关闭 Undo、保留选择复制。构造和更新只创建控件/文档状态，无文件、偏好、会话、网络、定时器和全局事件副作用；100ms debounce 由主窗口负责。
- 预览跟随父级 DynamicResource，使用 `TextBrush`、`AccentTextBrush`、`SyntaxKeyBrush`、`SyntaxStringBrush`、`PanelBrush`、`EditorBrush`、`BorderBrush`、`MutedBrush`。
- `Update(string)` 同步替换独立文档，保留构造时指定的字号与阅读宽度。继承的 `FontSize` 修改后，下次 `Update` 会按新字号重建；阅读宽度在构造时确定。
- `MarkdownSyntax.Tokens(string line)` 返回 `IEnumerable<MarkdownColorToken>`；标记字段为 `int Start`、`int Length`、`string ResourceKey`。偏移对应原始行 UTF-16；区间排序且不重叠。只设置前景色，不改变字号、字重、字距或原始文本。
- 编辑标记资源：标题 `AccentTextBrush`，围栏/行内代码 `SyntaxStringBrush`，强调 `SyntaxValueBrush`，链接/列表/引用标记 `SyntaxKeyBrush`。

## 兼容边界

- `ReadingModel` 与 `VisibleText` 完全不改；原阅读渲染器继续每块一个 Run，`PointerAt/OffsetAt` 实现不改。
- 只有独立预览拆分行内 Run、使用 WPF List/Table；该预览不暴露源/显示映射，也不应作为编辑导航映射使用。
- 行内语法跟随既有轻量 ReadingModel，不承诺完整 CommonMark/GFM。应用行内格式前核对可见文字，遇到差异直接保留模型文字。超过模型 50 万正文 UTF-16 阈值时继续退回原文，内嵌图片数据单独计限。
- 单行 token API 无围栏上下文：可识别围栏行，内部代码行的跨行状态由调用方负责。Setext 标题依赖相邻行，不由单行 token API 推断。
- 图片只保留模型占位文字，链接是带样式的 Run，不创建可导航 Hyperlink，也不渲染 HTML。
- 0 起点有序列表使用原始文字标记，避免 WPF 的 StartIndex 正整数限制改变内容或抛异常。
- 有序列表数字使用 `int.TryParse`；无法表示为正整数的标记保持字面显示。当前 Core 与着色器只识别 1～9 位数字，10 位以上原本即按普通段落保留，不会截断数字。
- `Update` 对解析超时、参数/范围、格式、整数溢出、无效文档状态及索引异常回退为当前原文，下一次更新仍重试富文本。回退文档直接使用 Run，不再次调用解析器。
- 空参数、非 UI 线程调用仍按接口合同报错，OOM 等进程级故障不吞掉。公开 API 不变；内部文档构建委托仅用于无全局状态的异常注入回归。

## 验证命令

在 `开发空间/Windows` 目录：

```powershell
dotnet run --project tests/Qingyue.MarkdownTests/Qingyue.MarkdownTests.csproj
dotnet run --project tests/Qingyue.TextTests/Qingyue.TextTests.csproj
dotnet run --project tests/Qingyue.TextTests/Audit/WpfAudit.csproj
```

可选生成只包含样例控件的浅/深色截图：

```powershell
dotnet run --project tests/Qingyue.MarkdownTests/Qingyue.MarkdownTests.csproj -- --screenshots tests/Qingyue.MarkdownTests/artifacts
```

覆盖源/显示映射单调性与 Unicode 边界、原指针往返、标题层级、围栏原文、行内代码/强调、图片/HTML 惰性展示、列表/表格、更新和字号/宽度、动态资源切换、token 区间/优先级/转义、残缺输入、长行与大文件回退。CRLF 中间的指针与未装饰的 WPF 单 Run 基准比较，保留 WPF 既有规范化行为。

编辑容错扩展后共 12 组检查，包含 410 次插入/删除/随机更新、0/9 位/int 边界/5000 位数字、残缺标题/链接/任务/表格/围栏、中文/emoji/不完整代理项、六类可恢复异常注入及恢复正常预览；同时验证模拟 OOM 继续上抛且保留原文档。
