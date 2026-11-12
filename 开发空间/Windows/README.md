# 轻阅 Windows

.NET 10 + WPF 原生 Windows 客户端。Core 不依赖第三方 NuGet 包，文件、文本、阅读模型和界面流程分别测试。

在仓库根目录用 PowerShell 7 执行：

```powershell
pwsh -File 开发空间/Windows/scripts/build.ps1
pwsh -File 开发空间/Windows/scripts/test.ps1
pwsh -File 开发空间/Windows/scripts/publish.ps1
pwsh -File 开发空间/Windows/scripts/build-installer.ps1 -SkipPublish
```

开发需要 .NET 10 SDK 及 Windows Desktop targeting pack。首次自包含发布需要下载 win-x64 运行时包，
有完整 NuGet 缓存时可以离线构建。构建脚本遇到失败会抛出异常并停止。
安装器构建另需 NSIS 3.11、MSVC x86 Build Tools 和 Windows SDK；可通过 `-NsisPath` 指定编译器。
这些构建命令生成产物，不自动安装或签名。

输出：`交付产品/轻阅-Windows-x64/Qingyue.exe`、`交付产品/轻阅-0.6.0-Windows构建1-x64.zip`
及 `交付产品/轻阅-0.6.0-Windows构建1-x64-setup.exe`。
采用自包含目录发布，整个目录必须一起保留。版本见 `Directory.Build.props` 与 Windows 项目 InformationalVersion。

工程入口：

- `src/Qingyue.Core`：文件与会话安全、搜索、格式、原生阅读模型。
- `src/Qingyue.Windows`：每标签独立 WPF 编辑器、FlowDocument 阅读、菜单、搜索、恢复。
- `tests/Qingyue.FileTests`：真实临时文件与 Windows 文件系统行为。
- `tests/Qingyue.TextTests`：Unicode、搜索与文本转换。
- `tests/Qingyue.UITests`：STA/WPF 真实控件流程与深浅主题截图。
- `tests/Qingyue.EditingTests`：命名、选择、修改行、拖动排序与恢复。
- `tests/Qingyue.MarkdownTests`：Markdown 分色、预览与阅读映射兼容。
- `tests/Qingyue.SequenceTests`：有限 Mermaid 时序语法、错误与资源限制。
- `tests/Qingyue.MermaidViewTests`：原生时序图几何范围、滚动、主题和文字布局。
- `tests/Qingyue.ReadingTests`：默认全宽预览、富文本导航、自动查找与实际刷新钩子。
- `tests/Qingyue.ImageTests`：本地与内嵌图片、路径、资源限制及实际样本（`--samples 文档目录`）。
- `tests/Qingyue.ClipboardTests`：结构化富文本、UTF-8 剪贴板偏移、局部选区及内嵌图片；使用合成数据，不读取系统剪贴板。
- `tests/Qingyue.InstanceTests`：真实多进程启动转发、激活和单实例通信。
- `tests/Qingyue.ExternalChangesTests`：磁盘刷新、未保存保护、异步竞态和视图状态。
- `tests/Qingyue.NavigationTests`：打开取消、真实视口、查找返回与目录跳转。
- `tests/Qingyue.RecoveryTests`：未加载草稿保护、改名中断和退出冻结。
- `tests/Qingyue.ReaderExperienceTests`：图片窗口、表格对齐、展开阅读及资源边界。

构建 3 支持 `mermaid` 围栏中的离线 `sequenceDiagram`，不引入 WebView 或脚本运行时。
语法边界及总量限制见 `操作指导/Windows使用说明.md`，示例为 `Samples/时序图.md`。

测试产物在 `artifacts`，不纳入 Git。GUI 流程测试需要可用的 Windows 交互桌面。
手工验收范围与结果见仓库 `docs/test.md`，用户操作见 `操作指导/Windows使用说明.md`。

安装器使用自定义 NSIS 对话框和原生深色控件，提供可选路径、快捷方式、系统卸载入口。
默认卸载保留用户数据；彻底清理需明确选择和再次确认，仅作用于轻阅默认数据目录。
程序文件按构建生成的清单删除，保留未知文件。静默卸载永远保留用户数据。

`scripts/test-installer.ps1` 编译轻量测试包，使用独立产品身份、注册项、数据目录及快捷方式；
会创建和卸载这些测试对象，需要当前用户目录及 HKCU 写权限。测试结束保留隔离安装供 GUI 选项验收。
继续运行 `scripts/test-installer-purge.ps1`，可通过共用生产删除宏验证彻底清理并移除测试安装；
运行 `scripts/build-wizard-preview.ps1` 生成卸载页面预览，预览不包含文件、注册、安装或删除动作。
危险路径只通过不含安装操作的校验程序检查。最终安装/清理测试分别通过 25/10 项；
正式本机安装另有 12 项验收，记录见 `docs/test.md`。原生图标可用
`powershell.exe -NoProfile -STA -File 开发空间/Windows/scripts/build-brand-assets.ps1` 离线重建。
安装器第三方来源和许可见 `installer/UPSTREAM.md`。
