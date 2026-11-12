# 轻阅 Windows 0.8.0 Markdown 图片与跨应用复制设计

## 版本定位

0.8.0 的第一条主线是“图片稳定、复制一致”。轻阅的 Markdown 使用场景以阅读和复制为核心，复制目标包含浏览器 `contenteditable`、腾讯文档、Word/WPS、邮件编辑器和普通文本输入框。文档中的图片可能来自相对路径、绝对路径、中文路径、空格路径或 `data:image/png;base64` 内嵌内容。

本设计沿用 WPF、离线运行和现有 `MarkdownPreview` / `RenderedReading` 管线。所有输出都从当前预览的已渲染快照生成，不能在复制期间重新读取不稳定的源文件，也不能把 WPF 活动控件交给后台线程。

## 当前基线

0.7.0 已具备：

- 图片异步加载、视口优先调度、取消、缓存和字节/像素预算；
- 相对路径、绝对路径、`file:` 和 PNG data URI 的基础处理；
- HTML、RTF、XamlPackage、UnicodeText 复制；
- 普通图片和 Mermaid 时序图的 PNG 内嵌；
- 单图选区的 Bitmap 复制；
- 图片加载失败、尚未完成和超预算时的明确占位文本。

当前缺口：

1. 复杂 Windows 路径和 Markdown 转义组合需要稳定回归；
2. 图片失败后只能重新打开或刷新，缺少当前文档内的重试入口；
3. 图片点击放大可用，但复制动作需要明确包含在图片控件和选区快照中；
4. 全文、选区、代码块和表格复制的快照边界需要统一，避免目标应用读取到互相矛盾的格式；
5. 复制到浏览器、Word/WPS 时需要以同一份 HTML/RTF/XamlPackage/Bitmap/UnicodeText 快照验收。

## 目标

- 支持并回归相对路径、绝对路径、中文路径、空格路径、括号路径、`file:` 路径和 PNG data URI。
- 图片加载失败后，用户可以在图片占位区域点击“重试”，重试只影响该图片，不重建整个文档。
- 图片点击放大、适应窗口、复制图片继续保持当前选区、焦点和滚动位置。
- 全文、当前选区、当前代码块和当前表格使用同一份 `ReadingClipboard` 导出快照。
- HTML 使用安全白名单和自包含 PNG；RTF/XamlPackage 使用同一批冻结位图；UnicodeText 保留可读文本；单图选区额外提供 Bitmap。
- 复制后删除、移动或修改源图片不会影响已经写入剪贴板的内容。
- 浏览器 `contenteditable` 粘贴后保留标题、列表、引用、表格、代码、普通图片和 Mermaid PNG。
- 富文本生成失败时仍保留 UnicodeText，并在界面上报告明确原因。

## 非目标

- 不支持网络图片请求，不引入 WebView、浏览器内核或脚本执行。
- 不把截图作为全文复制的唯一结果，结构化 HTML 和纯文本继续是主输出。
- 不扩大 Mermaid 支持范围，仍只对当前离线时序图提供图形快照。
- 不修改 Markdown 原文，不改图片文件，不改变用户资料和会话格式。
- 不改 Mac 工程。

## 用户可见行为

### 图片加载

图片成功加载后显示实际内容，加载失败时显示：

> 图片加载失败：原因。重试

点击“重试”只取消和替换当前图片请求。重试前保留文档其他内容和滚动位置；重试成功后自动隐藏状态文本。

图片超过单图或全文资源预算时继续显示预算提示，不提供无效重试。网络图片明确显示“网络图片未加载”。

### 复制快照

复制动作先在 UI 线程确认当前 `FlowDocument`、选择范围和已冻结图片，再创建不可变的 `ReadingClipboardSnapshot`。快照至少包含：

| 输出 | 用途 |
| --- | --- |
| `UnicodeText` / `Text` | 记事本、网页纯文本框和降级目标 |
| `HTML Format` | 浏览器、腾讯文档、Word/WPS、邮件编辑器 |
| `RTF` | Office/WPS 等优先读取 RTF 的目标 |
| `XamlPackage` | WPF 原生富文本目标 |
| `Bitmap` | 选区只有一张普通图片或 Mermaid 图形时 |

快照创建后，复制不再访问源图片路径。HTML 中的图片全部为自包含 PNG data URI，RTF 和 XamlPackage 使用相同的冻结 `BitmapSource`。

### 降级

- 图片仍在加载：HTML、RTF 和 XamlPackage 写入占位文字，计入 `Pending`；
- 图片损坏：写入“图片未包含：加载失败”，计入 `Failed`；
- 图片不受支持：写入“图片未包含：不支持的图片来源”，计入 `Unsupported`；
- 超过资源预算：写入“图片未包含：超过资源上限”，计入 `Limited`；
- 富文本导出异常：只保留 UnicodeText，并提示“富文本未生成，已复制纯文本”。

## 技术方案

### 1. 图片源解析与身份

在 `MarkdownImageLoader.LocalPath` 中集中处理路径：

1. 解码 `file:` URI；
2. 对普通目标执行 `Uri.UnescapeDataString`；
3. 以文档目录为基准解析相对路径；
4. 使用 `Path.GetFullPath` 规范化；
5. 拒绝 UNC、网络盘、网络 URI 和超长路径；
6. 缓存键包含规范化绝对路径、文件长度和 UTC 修改时间。

不改变当前网络图片拒绝策略。所有异常都转换为面向用户的中文原因，不泄露无关绝对路径。

### 2. 图片重试

`MarkdownImageView` 增加 `Retry()` 和 `RetryRequested` 事件：

- 清理旧取消源；
- 重置 `LoadingStarted`、`finished`、图片可见性和状态文本；
- 重新注册到 `MarkdownImageScheduler`；
- 复用 `MarkdownImageLoader` 和 `MarkdownImageCache`；
- 成功后调用 `result.Accept()`，失败后保留错误状态。

重试不替换宿主 `FlowDocument`，只更新当前图片控件。

### 3. 复制快照

在 `ReadingClipboard` 内部增加 `ReadingClipboardSnapshot` 和 `CreateSnapshot`：

- `CreateSnapshot(FlowDocument document, TextRange range)` 只遍历选区；
- `Export` 负责生成 HTML、纯文本、图片报告和已编码图片；
- `CreateNativeFormats` 用导出后的白名单 `FlowDocument` 生成 RTF/XamlPackage；
- `SingleBitmap` 仅在导出结果恰好包含一张图片且没有其他可见图片时提供；
- `Copying` 事件、全文复制、代码块复制、表格复制均调用相同入口。

快照的生命周期只在当前复制调用内存在，不保存到工作区，不引入新的持久化格式。

### 4. 浏览器兼容

继续使用 CF_HTML 的 UTF-8 字节偏移，片段由 `ReadingHtmlExport` 的白名单节点生成。输出只允许标题、段落、列表、引用、表格、代码、粗体、斜体、删除线、链接和自包含图片。所有属性和文本经过 XML/HTML 转义，禁止输出源路径、`SourceURL` 和外部资源地址。

## 错误处理与安全边界

- 任何单图解码、PNG 编码、RTF/XamlPackage 写入都有字节、像素和元素数量上限；
- 图片、Mermaid 位图必须冻结后才能跨异步边界；
- 复制期间文档发生 `TextChanged` 或 `Document` 替换时，放弃富文本快照并让调用方重新复制；
- 失败降级只发布 UnicodeText，不发布半成品 HTML；
- 重试和复制操作捕获 `IOException`、`InvalidOperationException`、`ArgumentException`、`COMException`、`SecurityException`，不让 UI 线程崩溃。

## 验收标准

1. 中文、空格、括号路径和 `file:` 路径图片能稳定显示，网络图仍明确降级。
2. 失败图片可在当前文档内单独重试，成功后不重建其他段落。
3. 复制前后选区、滚动偏移、焦点、文本和撤销状态不变。
4. 复制后删除源图片，HTML、RTF、XamlPackage 和 Bitmap 仍能解析。
5. 浏览器 `contenteditable` 粘贴保留结构、普通图片和 Mermaid PNG。
6. 单图选区能从 `DataFormats.Bitmap` 读取正确尺寸和像素。
7. 富文本输出失败时 UnicodeText 仍存在，且没有空 HTML 图片。
8. Windows Release 构建无警告，图片、复制、Markdown 和 UI 定向测试全部通过。
