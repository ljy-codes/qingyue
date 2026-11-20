# 轻阅 Windows 0.7.0 Markdown 阅读与复制实施计划

> 只修改 Windows 工程和 Windows 验收资料。每项先补回归，再实现最小变更，最后运行定向测试。

**目标：** 让 Markdown 阅读内容在浏览器、腾讯文档、Word/WPS 和图片接收方中保留结构、普通图片和 Mermaid 图形，并把复制过程收敛为同一份安全快照。

**基线：** Windows 0.6.0，当前提交 `fb795e0`，现有异步 Markdown、图片缓存和多格式复制继续复用。

## Task 1：锁定 Mermaid 和单图复制的失败回归

**Files:**

- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/SharingChecks.cs`
- Modify: `开发空间/Windows/tests/Qingyue.MermaidViewTests/Program.cs`

- [ ] 增加包含 `sequenceDiagram`、普通图片、标题、表格和代码的复制样本。
- [ ] 断言 Mermaid HTML 中存在自包含 PNG，纯文本仍包含 Mermaid 源码。
- [ ] 断言单图片选区提供 `DataFormats.Bitmap`，像素尺寸和颜色正确。
- [ ] 断言 Mermaid 解析失败时没有空图片，源码和提示仍可复制。
- [ ] 先运行定向测试，确认新增断言在当前实现中失败。

## Task 2：增加 Mermaid 的受限位图快照

**Files:**

- Modify: `开发空间/Windows/src/Qingyue.Windows/MermaidSequenceView.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingHtmlExport.cs`

- [ ] 在 `MermaidSequenceView` 增加 UI 线程专用的冻结位图渲染方法。
- [ ] 使用当前主题资源，限制最大宽高和总像素；超限返回空并保留源码。
- [ ] 在导出器的 `BlockUIContainer` 和 `InlineUIContainer` 中识别 Mermaid 视图。
- [ ] 复用现有 PNG 编码、大小预算和 `ExportedImage`，避免复制路径重新读取。
- [ ] 单图选区时写入 `Bitmap`；多图或混合内容不写入误导性的单图格式。

## Task 3：收敛复制格式和降级

**Files:**

- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Clipboard.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/MainWindowSharingChecks.cs`

- [ ] 为全文、选区、当前代码块和当前表格统一使用同一份导出结果。
- [ ] 保留 UnicodeText 作为最终兜底，避免富文本生成失败时阻断纯文本复制。
- [ ] 不改变原选区、焦点、滚动偏移、文件文本和撤销状态。
- [ ] 增加复制后关闭源文件、移动源图片仍可解析剪贴板内容的检查。
- [ ] 验证 CF_HTML UTF-8 字节偏移和自包含图片。

## Task 4：检查大 Markdown、阅读位置和常用格式入口

**Files:**

- Modify: `开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingRenderer.Markdown.cs`
- Modify: `开发空间/Windows/src/Qingyue.Core/LanguageCatalog.cs`
- Modify: `开发空间/Windows/src/Qingyue.Core/FormattingServices.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.TextTests/Program.cs`

- [ ] 保持 50 万字符预览上限，补充大文档分批构造、取消和重复更新回归。
- [ ] 检查图片和 Mermaid 完成后不替换当前文档、不覆盖滚动位置。
- [ ] 对常用语言补充安全整理入口或明确的能力说明，不引入不安全的猜测性重排。
- [ ] 运行阅读、图片、文本和 Markdown 定向测试。

## Task 5：版本、文档和发布

**Files:**

- Modify: `开发空间/Windows/Directory.Build.props`
- Modify: `开发空间/Windows/src/Qingyue.Windows/Qingyue.Windows.csproj`
- Modify: `开发空间/Windows/installer/version.nsh`
- Modify: `开发空间/Windows/scripts/test.ps1`
- Modify: `CHANGELOG.md`
- Modify: `docs/test.md`
- Modify: `开发空间/Windows/README.md`

- [ ] 升级到 `0.7.0`、文件版本 `0.7.0.1`、Windows 构建 `1`。
- [ ] 更新复制目标、Mermaid 图形、图片降级和格式化边界说明。
- [ ] 执行 `pwsh -NoProfile -File scripts/test.ps1 -Configuration Release`。
- [ ] 生成 Windows 自包含目录、ZIP 和安装 EXE。
- [ ] 覆盖安装到本机 `D:\Program File\Qingyue`，验证启动、Markdown 阅读、复制和卸载默认保留资料。
- [ ] 提交并推送 `feat/windows-client`，用 `git ls-remote` 核对远端提交。

## 风险与控制

- Mermaid 位图只从当前 WPF 视图生成，不能在后台线程访问视觉树；渲染前后检查 UI 线程。
- Mermaid 图形有宽高和像素预算，超限时保留源码，防止复制动作卡住。
- 富文本格式生成失败时仍发布 UnicodeText；不发布半成品 HTML。
- 只升级 Windows 版本，不触碰 Mac 工程和用户资料结构。
