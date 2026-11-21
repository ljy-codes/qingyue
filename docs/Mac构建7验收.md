# Mac 构建 7 验收

日期：2026-11-17。产品版本保持 **0.1.0**，Mac 构建编号 **7**。本轮拉取 Windows 最新 `v0.14.0`（`04ac225`），沿用构建 6 的窗口布局和主题，完成 Mac 原生 Markdown 写作能力移植。

## 本轮已接入

- Markdown 表格：源码中的 `Tab` / `Shift+Tab` 单元格移动；菜单和工具栏支持增删行列、当前列左中右对齐。
- TSV 粘贴：将 Excel/WPS 或其他表格复制出的制表符文本转换成标准 Markdown 表格。
- 图片附件：已保存 Markdown 可通过菜单选择、拖入文件或粘贴截图；图片写入文档旁 `<文件名>.assets`，使用唯一 PNG 文件和相对链接，不覆盖已有附件。
- 本机批注与阅读简报核心：`Annotations.json` 原子保存、损坏隔离、容量限制、路径迁移、锚点状态，以及 Markdown/HTML 简报导出已具备；批注侧栏和简报菜单入口留给后续 build。

## 自动验证

- [x] `MarkdownTableEditingTests`：6 项。
- [x] `MarkdownAssetWriterTests`：5 项。
- [x] `AnnotationStoreTests`：18 项。
- [x] 原有 Mac 回归：文件核心 32 组、阅读模型 331、搜索 1038、编辑 1086、会话 21 组/102 项、Mermaid 74、格式化 192、富阅读 93、UI 87、菜单 42、Mac 流程 33、外观 52、布局 115 项全部通过。
- [x] `bash 开发空间/scripts/build.sh`：Intel x86_64、macOS 13+，产品版本 0.1.0 / build 7。

## 交付边界

Mac 仍使用 AppKit 和本机文件处理，不引入 WebView、网络图片下载、账号或云同步。图片只处理本地文件和剪贴板位图，单个输入受 8 MiB / 1600 万像素限制。批注数据核心尚未接入界面，Windows 的工作区全局搜索、阅读清单、流程图、更新服务和完整 Markdown 列表快捷编辑将按后续 Mac build 分阶段移植。

Windows WPF 工程、安装器和 Windows 版本号保持独立；本轮只修改 Mac 源码、Mac 测试、Mac 文档和 Mac 构建编号。
