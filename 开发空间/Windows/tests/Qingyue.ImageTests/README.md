# Markdown 图片独立验收

覆盖 `MarkdownPreview.cs`、`ReadingRenderer.cs` 和 `RichDocumentMap.cs` 的实际图片、富文本与导航行为；完整检查已接入 `scripts/test.ps1`。

实施顺序：

1. 先运行既有 Markdown、文本、Mermaid、WPF 映射测试。
2. 用真实 PNG 编写失败用例，确认旧预览没有创建 WPF Image。
3. 在图片辅助类型中扫描并提取图片引用，用短引用构建富文本，长 data URI 不进入 Core 的正文长度判断；代码、转义、Mermaid 保持原语义。
4. 两套渲染器共用本地图片控件，后台读取/解码、Freeze，限制单图字节/像素和解码并发，拒绝网络来源。
5. 对中文、空格、URL 编码、缺失/损坏、长内嵌图、尺寸自适应、文档更新及映射进行验收，再复跑既有测试和主程序构建。

运行（在 `开发空间/Windows` 下）：

```powershell
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj -- --samples 'D:\source-code\服务使用指南\流程中台'
```

接口：

```csharp
new MarkdownPreview(text, fontSize, readingWidth,
    sourcePath: markdownFilePath, includeDiagramSource: false);

new RenderedReading(model, renderMermaid: true,
    sourcePath: markdownFilePath, sourceText: originalMarkdown);
```

所有新增参数均可选，原调用保持兼容。`sourcePath` 为 Markdown 文件路径，用于解析相对图片；绝对路径和 data URI 不依赖它。旧映射渲染器需要额外传 `sourceText` 才能取得 Core 显示模型已经隐藏的图片目标。完整 Markdown 阅读由主控 factory 使用 `MarkdownPreview.Document`，并传 `includeDiagramSource: true`，保留可选择的 Mermaid 源段落。

支持本地相对/绝对路径、中文空格、URL 百分号编码、括号路径、`file:` URI 和 `data:image/png;base64,`。网络协议、UNC、编码后的远程地址和网络驱动器不会读取。代码围栏、行内代码、转义语法保持原文。图片 UI 后保留小字号可选图注；段落、表格、Mermaid 的 Tag 使用原文模型块，投影 token 不进入查找索引。`PointerAt/OffsetAt` 原单 Run 算法仅增加主控要求的 richMap 分支。

读取与解码在后台进行，位图使用 OnLoad 并 Freeze；同时最多解码两张。单图限制为 16 MiB、3200 万原始像素、单边 32768，解码最长边最多 4096。单篇最多 32 个图片控件、64 MiB 编码数据、4800 万解码像素。图片等比适应可用宽度，缺失、损坏、超限时显示提示，卸载会取消未完成任务，未被 UI 接收的位图释放预算。

2026-11-10 图片验收：26/26 通过。19 组通用检查包含实际冻结位图、两渲染器路径、60 万字符内嵌数据、富表格/列表/Mermaid、块 Tag、图前图后及图注选择、Mermaid 源消息选择、异常回退、无网络请求、资源限制、宽度缩放和取消恢复。列表导航覆盖表格后的列表、多个列表项、有序列表、任务列表、格式化起点及文档交给 RichTextBox 后的精确选择。WPF 列表起点增加无文字内联边界，避免选择带出项目符号；Run 内反向映射使用指针偏移，避免 TextRange 的列表序列化影响计数。

畸形图片识别使用独立子进程和 3 秒硬上限，覆盖 100,000 个重复 `![` 加 500,001 个正文字符后的三种未闭合/无效 alt 和 target。修复前超时失败，修复后三组 70 万字符以上输入扫描合计 11 ms，源码保持逐字一致。另验证 01/03/04 原始样本、逐字节复制到无 `images` 子目录的三个副本，以及 02 无图文档打开。

真实样本只在运行时读取，业务正文不写入测试源码。副本及生成图片保存在 `bin/Debug/net10.0-windows/artifacts/`，已被仓库忽略。所有测试均不修改原始样本。本项目不使用第三方 NuGet、浏览器、剪贴板或用户偏好；本地 TCP 监听器仅用于断言图片组件没有发起网络请求。

2026-11-10 resize 回归：

```powershell
dotnet build tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj --no-restore -p:BuildProjectReferences=false -m:1 -nr:false
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj --no-build --no-restore -- --resize-only
```

全部使用离屏 `Measure/Arrange`，不创建或操作 GUI 窗口，不读取真实资料。修复前按主窗口原逻辑同时设置控件 `Padding` 和文档 `PagePadding`：宽 1000 时单侧预期 148，实际文字起点 296，视口只剩 704；失败回归已实际运行。修复后只用文档留白，1000 → 1600 → 420 → 1000 的文字起点分别为 160、460、24、160。测试还覆盖分屏宽窄恢复、图片比例与位图复用、表格/列表/Mermaid 源文/图注查找，以及 499,998 字符正文的文档和指针复用。

全宽主窗口接线由主控负责：将 `UpdateReadingWidth` 中直接修改文档字号/行高、计算 `DocumentHost` 宽度并设置两处 Padding 的代码替换为：

```csharp
tab.Rendered.UpdatePresentation(tab.Preview, settings.FontSize + 1, settings.ReadingWidth);
```

须先将 `tab.Rendered.Document` 赋给 `tab.Preview.Document`。每个预览控件只注册一次 `SizeChanged` 和 `Loaded`，事件中调用当前 tab 的 `UpdateReadingWidth`，使布局依据实际预览控件宽度更新；阅读宽度设置变化时也调用。不要只依赖外层窗口 `SizeChanged`，侧栏或分屏会单独改变内容宽度。方法不重建文档或映射、不重读或解码图片。分屏 `MarkdownPreview` 已直接复用同一个布局计算，无须额外接线。

2026-11-10 表格列宽回归：

```powershell
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj --no-build --no-restore -- --table-only
```

`TablePreview` 从每列已经显示的单元格统计最大内容权重，区分普通字符与宽字符、忽略组合附加符等零宽字符，计入少量单元格内边距估算。每格最多检查 128 个 Unicode 码点，权重限制在 10～40，图片单元格使用上限；列之间最多相差四倍。使用 Star 比例而非固定像素宽度，窄屏继续折行。统计不会改变 Table/Cell/Run、源块 Tag 或映射，也不读取图片目标和长 Base64。

先确认失败：680 正文宽度下，序号/项目/路径三列原来均为 226.3；修复后约为 109.5/131.4/438.1，40 字符路径前缀保持同一行。另覆盖 121 条数据行、5000 字符无断点路径、1000 → 420 → 320 → 1000 宽度恢复和表尾查找；320 宽时序号列仍有约 34.3 像素。12/24/32 字号下检查列文字边界与精确选择。最新完整独立检查 26/26 通过（19 组图片、4 组 resize、3 组表格），构建 0 警告、0 错误；本轮全部使用合成资料。

保留限制：表格仍使用 Core 已生成的可见单元格文本，没有新增单元格 inline 装饰或冒号对齐解析。窄屏或大字号下允许单元格多行显示；列数极多时不承诺每个表头保持单行。

2026-11-10 字号与呈现设置回归：

```powershell
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj --no-build --no-restore -- --presentation-only
```

先运行 4 组失败回归：基础字号从 16 改为 20 后 H1 仍为 32、分屏阅读宽度无法原地更新、完整阅读局部字号不跟随、后续正文更新丢失新设置。新增原地更新接口：

```csharp
markdownPreview.UpdatePresentation(fontSize, readingWidth);
renderedReading.UpdatePresentation(host, fontSize, readingWidth);
```

两者均要求 UI 线程，后一接口要求 `host.Document` 已是当前 `renderedReading.Document`。同步基础字号、H1～H6 字号/行高/间距、代码与表格字号和图注字号（保留 11 下限），保留富列表缩进。Mermaid 复用现有控件，同时缩放文字与图形几何。没有重建 Document、Block、Run、映射或位图；设置相同且留白未变化时跳过样式遍历和主动重排。设置变化同步重排后恢复原像素滚动偏移，不排队延迟恢复回调；若文档变短，偏移按新末尾范围限制。

分屏主控在仅字号/阅读宽度变化时调用 `UpdatePresentation`，不再新建 `MarkdownPreview`；正文改变仍调用 `Update(text)`，沿用新设置。`RenderedReading.UpdateLayout(host, readingWidth)` 继续兼容纯 resize 调用；若主控在 Loaded/SizeChanged 使用 `UpdatePresentation`，相同字号不会重复更新局部样式。

4 组呈现回归覆盖 16→20→12→32→16、选区端点与选中文本、非零滚动偏移、重复相同设置、富阅读与单 Run 阅读、图前图后/图注/列表/表格/Mermaid 源文查找、图片位图复用及后续正文更新。500k 正文回归同时覆盖字号变化后的文档/源指针复用。所有资料均为合成内容，不创建或操作 GUI。

最终独立项目构建 0 警告、0 错误，完整检查 30/30 通过（19 组图片、4 组 resize、3 组表格、4 组呈现设置）。主窗口真实 ReadingTests 和 review 探针由主控另行验证。
