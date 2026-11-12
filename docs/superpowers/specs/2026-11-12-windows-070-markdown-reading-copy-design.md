# 轻阅 Windows 0.7.0 设计方案

## 版本定位

0.7.0 定位为“Markdown 深度阅读与跨应用复制”版本。

用户使用轻阅打开 Markdown 的核心动作是阅读和复制。复制目标包含浏览器编辑区、腾讯文档、Word/WPS、邮件编辑器和普通文本输入框，文档中还可能包含本地图片、内嵌图片和 Mermaid 时序图。本版本围绕这条主链路收敛复制格式和阅读输出，继续沿用 Windows 原生 WPF、离线运行和现有异步 Markdown 管线。

## 当前基线

0.6.0 已具备：

- Markdown 默认阅读预览、源码编辑和分屏预览；
- 50 万正文字符限制；
- 图片异步读取、首屏优先、缓存和资源预算；
- HTML、RTF、XamlPackage、纯文本复制；
- HTML 图片使用自包含 PNG；
- 外部更新、搜索高亮和阅读位置恢复；
- Mermaid `sequenceDiagram` 的离线阅读展示和源码保留。

本版本主要补齐三个实际缺口：

1. Mermaid 当前主要作为 WPF 图形阅读，复制到网页时容易只得到源码或空白块；
2. 选区只包含单张图片时，剪贴板缺少直接的 `Bitmap` 图片格式；
3. HTML、RTF、XamlPackage 和纯文本的内容快照边界分散，复杂选区需要更明确的统一兜底。

## 目标

- Markdown 中已成功渲染的 Mermaid 时序图，复制到网页、Word/WPS 和支持图片的编辑器时保留 PNG 图形，同时保留纯文本中的 Mermaid 源码。
- 单张图片选区同时提供 HTML、PNG Bitmap、RTF、XamlPackage 和纯文本兜底。
- 图片关闭原文件、移动原文件后，已经生成的复制内容仍然可用。
- HTML 继续使用 CF_HTML 的 UTF-8 字节偏移，保留标题、段落、标题层级、列表、表格、引用、代码和图片。
- 复制不改变当前选区、阅读位置、焦点、未保存状态和撤销栈。
- 常用配置和脚本语言至少提供安全的空白整理入口；无法可靠格式化的语言不伪装成完整格式化器。
- 版本升级为 `0.7.0.1`，只构建和发布 Windows。

## 非目标

- 不引入 WebView、浏览器内核、网络图片请求或新的第三方运行时。
- 不声称支持全部 Mermaid 图表类型；本版本仍只处理当前已实现的离线时序图。
- 不改变用户资料、会话、草稿和原文编码格式。
- 不改 Mac 工程。
- 不把 Markdown 复制改成图片截图作为唯一结果；结构化 HTML 和纯文本仍是主输出。

## 用户可见行为

### Markdown 阅读

打开 Markdown 仍默认进入阅读预览。标题、表格、代码、引用、列表、图片和 Mermaid 图形保持现有层次。大文档继续使用可取消的异步构造，正文先可读，图片按视口加载。

### 复制全文和选区

复制阅读内容从同一个结构化快照生成：

| 格式 | 主要接收方 |
| --- | --- |
| `UnicodeText` / `Text` | 记事本、网页纯文本框和不支持富文本的输入框 |
| `HTML Format` | 浏览器编辑区、腾讯文档、Word/WPS、邮件编辑器 |
| `RTF` | Office/WPS 等优先读取 RTF 的接收方 |
| `XamlPackage` | WPF 原生富文本接收方 |
| `Bitmap` | 选区仅包含一张图片或 Mermaid 图形时的图片粘贴兜底 |

普通本地图片继续以内嵌 PNG 输出。Mermaid 图形在 HTML 中输出自包含 PNG，纯文本中保留源码段落；因此网页可以看到图，纯文本仍能得到可编辑的 Markdown 内容。

### 失败和降级

- 图片尚未加载、损坏或超过资源预算时，HTML 保留明确占位符；
- Mermaid 渲染失败或图形超出渲染预算时，HTML 保留源码，不生成空白图片；
- 生成富文本失败时仍保留 UnicodeText；
- 接收方主动丢弃 HTML、图片或 CSS 时，按接收方能力降级，不影响轻阅和剪贴板稳定性。

## 技术方案

### 1. 统一复制快照

在现有 `ReadingClipboard.Export` 之上收敛一个 UI 线程内的不可变导出结果。结果包含：

- 纯文本；
- 白名单 HTML 片段；
- RTF 和 XamlPackage 使用的安全 WPF 文档；
- 已编码的内嵌图片；
- 图片状态报告；
- 可选的单图 Bitmap。

不把活动 WPF 控件交给后台线程。图片和 Mermaid 位图只使用已冻结的 `BitmapSource`，复制时重新编码为 PNG，避免依赖源文件路径。

### 2. Mermaid 图形复制

`MermaidSequenceView` 增加受限的 UI 线程渲染快照：

- 使用当前主题和字体；
- 渲染宽度、高度和像素总量有上限；
- 输出冻结的 `BitmapImage`；
- 不访问网络、不执行脚本、不读取 Markdown 源路径。

导出器在 `BlockUIContainer` 中识别 Mermaid 视图，并将其作为普通内嵌图片写入 HTML、RTF 和 XamlPackage。单个 Mermaid 选区可以额外写入 `Bitmap`。

### 3. 安全格式化

保留 JSON/JSONC、TXT、Markdown 和 XML 的现有完整或保守格式化行为。为 YAML、TOML、SQL、Shell、Python、Java、C#、JavaScript、TypeScript、HTML、CSS、Go、Rust 增加明确的“整理空白”能力或现有能力说明：

- 不做语法树重排；
- 不改字符串内部内容；
- 不改变注释、缩进语义和代码块结构；
- 操作支持撤销；
- 入口文案明确区分“格式化”和“整理空白”。

## 验收标准

1. 含普通图片和 Mermaid 图的 Markdown 阅读保持可操作，图片加载完成不跳回顶部。
2. 复制到本地 `contenteditable` 页面后，标题、列表、表格、代码、普通图片和 Mermaid 图形存在。
3. 复制到 Word/WPS 或腾讯文档时，至少保留标题、表格、普通图片和 Mermaid PNG；未安装的目标记录为未验收。
4. 单图选区能从剪贴板读取 PNG Bitmap，粘贴到图片编辑器后像素尺寸正确。
5. Mermaid 解析失败时复制得到源码和提示，不得到损坏的空图片。
6. CF_HTML 的 StartHTML、EndHTML、StartFragment、EndFragment 按 UTF-8 字节偏移有效。
7. 复制前后选区、焦点、滚动位置和文档状态一致。
8. Release 构建无警告、全量 Windows 回归通过，版本为 `0.7.0.1`。

