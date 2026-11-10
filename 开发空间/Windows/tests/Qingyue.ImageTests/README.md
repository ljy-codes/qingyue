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
