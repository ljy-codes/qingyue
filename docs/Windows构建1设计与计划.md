# Windows 构建 1：设计与实施计划

日期：2026-11-09。产品版本 0.1.0，Windows 构建 1。

## 目标与边界

在 Windows 本机交付原生文本阅读与编辑器，使用现有 Mac 版的行为约定、配色和样例。
新增工程全部放在 `开发空间/Windows`，不修改 Swift 源码、Mac 构建脚本和历史验收记录。
当前工作在新分支 `feat/windows-client` 中进行，不发布 GitHub Release、不修改系统文件关联。

## 方案选择

- 采用 .NET 10 + WPF：本机已有 SDK、WindowsDesktop 引用包及运行时缓存，使用系统文本与输入法能力，无浏览器引擎和前端运行时。
- WinUI 3 需要额外 Windows App SDK 部署；Avalonia 可跨平台但增加组件依赖。本轮目标是独立 Windows 客户端，优先选择可本机验证的 WPF。
- 普通编辑使用每标签独立 TextBox 与撤销栈；阅读使用原生 FlowDocument。沿用 Mac 的深蓝灰与蓝色强调，增加 Windows Ctrl 快捷键、菜单与键盘焦点。
- 产品分为 Core（文件、搜索、格式、阅读模型、会话）、Windows（窗口与原生排版）、独立控制台行为测试及 WPF 流程测试。

## 本轮交付

新建、打开、拖入、保存/另存为/保存全部、多标签及独立撤销、最近文件、重命名、路径复制与资源管理器定位；
阅读/编辑、专注布局、目录、正文版心、字号、明暗主题；
普通/正则/全词搜索与捕获替换、JSON 检查与保留数字词法的格式化；
UTF-8/BOM、UTF-16 LE/BE、GB18030，LF/CRLF/CR 保持，混合换行显式转换；
外部修改检查、Windows 临时文件与替换、只读/占用失败保护；
会话与独立草稿串行保存，损坏隔离、原文件重新读取、关闭保存/放弃/保留草稿的明确语义。

单文件上限 50 MiB，富文本阅读上限 200,000 UTF-16 单元，搜索上限 5,000,000 单元且正则有超时。
不实现 Office、云同步、插件、多窗口、目录全文搜索、远程图片或脚本执行。
Mac 与 Windows 的草稿格式和系统偏好独立，不能将旧 Mac 数据直接视为 Windows 会话。

## 核心接口

命名空间 `Qingyue.Core`。公共枚举及记录见 `DocumentTypes.cs`。

`DocumentFile` 提供 `Open(path, codec?)`、`Save(destination?, allowOverwrite)`、`Rename(name)`、
`DiskChanged()`、`CaptureSnapshot()`、`Restore(snapshot)`；文档的 `Text` 内部使用 LF，
保存以显式 `Codec` 和 `LineEnding` 输出；保存失败不得更新磁盘基线或丢弃正文。

`WorkspaceStore` 提供异步 `SaveAsync(session, snapshots)`、`LoadAsync()`、`ClearDraftsAsync()`，
保存不可变副本，所有操作经过同一串行门闩。

`TextServices` 提供 `DetectKind`、`Diagnostic`、`FormatJson`；
`SearchEngine.Find(text, query, replacement, options)` 与 `ReplaceAll`；
`ReadingModel.Build(text, kind)` 提供显示文字、块与目录，以及双向块级 UTF-16 映射。

## 实施与验收

- [x] 文件/草稿核心：真实临时文件测试 25/25 组通过，覆盖编码逐字节往返、外部冲突、ACL、只读/占用、链接、混合换行及损坏恢复。
- [x] 搜索/阅读/格式：Unicode、全词、捕获替换、超时、JSON 大整数及 Markdown 映射测试 23/23 组通过。
- [x] WPF 主窗口：每标签独立编辑器、菜单、查找栏、目录和底部编码/换行状态已接入；文本位置索引 5/5 组通过。
- [x] 集成验收：Release 零警告零错误，WPF 流程 33 项通过、映射审查零失败；已检查深浅主题、窄窗口和实际包的首次行号与阅读边距。
- [x] 打包：生成 win-x64 自包含便携目录及 ZIP，确认运行时随包；记录最终包哈希、体积、启动采样与正常退出。
- [x] 文档：已更新 README、Windows 平台记录、Windows 使用说明及 `docs/test.md`，保留人工验收缺口。

## 阶段状态

已确认：基线 `d80950e54eb627222b2830480fbf63ecd4796a8c`，工作分支 `feat/windows-client`。
本机 .NET SDK 10.0.400 / WindowsDesktop 10.0.11，Windows x64 build 26300.9818。
采用独立 WPF 工程，无第三方 NuGet 组件；最终包使用独立测试数据目录启动、打开 Windows 示例、
切换阅读并正常退出。结果、采样限制与 SHA256 见 `docs/test.md`。

已修改：Windows Core、WPF 窗口、构建/测试/发布脚本、Windows 示例和相关文档、忽略规则。
修复了阅读锚点、目录刷新、仅查找误用替换模板、空文档命名恢复、退出放弃草稿、
首次行号与正文边距，以及自定义编辑行距导致的行号偏移。未修改 Swift 源码和 Mac 构建配置。

风险：Windows ACL/链接/文件占用与 Mac 不同，采用保守拒绝和另存为；复杂 Markdown 仅作轻量原生排版；
中文输入法候选交互保留 Windows 控件默认行为，但尚未做候选交互人工验收。
未完成项为不同 Windows 版本/缩放、网络盘、屏幕阅读器和完整输入法组合输入矩阵；
本轮不将这些描述为通过。后续先完成这些人工验收，再评估正式签名与对外分发。

安装器与图标追加需求已完成：深蓝书页多尺寸图标、可选目录的深色 NSIS 安装/卸载向导、
默认保留用户信息，以及单独确认和勾选后才启用的彻底清理。最终 25 项安装/默认卸载、
10 项清理验收与 12 项正式本机安装检查通过；本机已更新，真实资料摘要一致。
完整验收范围与限制见 `docs/test.md`，阶段状态见 `docs/Windows安装器状态.md`。
