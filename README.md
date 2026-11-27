# 轻阅 Qingyue

轻量的本地文本阅读与编辑工具，面向 Intel 与 Apple Silicon Mac / macOS 13+。

当前 Mac 版本：**0.4.0 · 构建 14（预览版）**。Windows 历史版本为 0.18.0。
2026-11-24 已将 Windows 源码中的 Markdown `<br>` 行为与 Mac 对齐，并重新生成 Windows ZIP/NSIS 安装包后安装到本机 `D:\Program Files\Qingyue`；全量自动回归退出码为 0，目标软件人工粘贴验收仍待完成。

## GitHub 分支与版本

长期分支为 `main`、`macos`、`windows`：`main` 保存项目基线、功能约定和说明，两个平台分支分别维护原生实现和发布记录。Mac 使用偶数版本，Windows 后续使用奇数版本；新标签格式为 `v<X.Y.Z>-macos` / `v<X.Y.Z>-windows`。Windows 历史版本保持原编号。详见[跨平台版本与功能对齐规则](docs/跨平台版本与功能对齐规则.md)。

两端最终功能和最终应用图标必须一致。任一端可先行开发并为另一端预留版本号，但不得代写另一端实现或修改其分支；任何跨端修改都必须事先取得用户明确授权。

## 本轮跨平台同步

- Windows 已同步 Mac 的 Markdown 段内 `<br>` 行为：正文、表格、预览、编辑器高亮、纯文本、RTF/XamlPackage 和 HTML 导出均保留实际换行；反引号代码跨度中的标签保持字面内容。
- 该同步只覆盖已验收的 `<br>` 子集，不代表 Windows 已完整对齐 Mac 0.4.0。Windows 大文件搜索、独立设置窗口、7 套主题和 1024px 图标等差异仍未移植。
- Windows 仍使用历史身份 **0.18.0 · Windows 构建 1**，但本轮已重新生成 ZIP 和 NSIS 安装包，并安装到 `D:\Program Files\Qingyue`；安装包未签名，未创建新 Release，Windows 人工验收仍待完成。

## 安装与交付

- [下载 Mac 最新版（v0.4.0-macos）](https://github.com/ljy-codes/qingyue/releases/tag/v0.4.0-macos)
- 本机安装：`/Applications/轻阅.app`
- 本地交付目录：`交付产品/轻阅-macOS-0.4.0/`
- 本地压缩包：`交付产品/轻阅-macOS-0.4.0-交付.zip`
- 校验文件：交付目录中的 `SHA256SUMS.txt`（GitHub Release 同步提供）

仓库不提交生成的 App/ZIP；GitHub 下载使用上面的 Release，以下交付路径仅供本机构建。外部实机验收范围见验收记录。

## 主要功能

- TXT、Markdown、JSON/JSONC、YAML、INI/CONF、XML 文本阅读与编辑。
- 多标签、最近文件、快速打开、拖入打开、标签排序、重命名、保存全部。
- 阅读模式、源码编辑、Markdown 分屏预览、目录跳转和阅读位置返回。
- 本地相对/绝对路径图片、内嵌 PNG、宽表阅读、有限离线 Mermaid 时序图/流程图。
- 选区图文复制、全文复制、纯文本复制、Markdown 源文复制、自包含 HTML 导出。
- 查找替换：大小写、全词、正则、捕获组、匹配高亮；字面查找支持 5000 万 UTF-16 单元。
- 会话恢复、独立恢复草稿、外部修改检测、安全保存、编码与换行保留。
- 工作区、全局搜索、阅读清单、书签、批注与阅读简报。
- 7 套外观主题：经典、咖色暖读、青瓷古色、海盐蓝、烟雨紫、墨蓝深色、纸白极简。
- 四款应用图标：天空蓝、樱花紫、薄荷珊瑚、蜜桃紫。

## 设置入口

- 主窗口右上角 **“设置”**
- 顶部菜单 **轻阅 → 设置…**（`⌘,`）
- **视图 → 阅读外观与设置…**

设置窗口会实时预览阅读与编辑效果，并保存主题、明暗、图标、字体、行距、段距和版心。

## 构建与测试

依赖 Xcode Command Line Tools。

```bash
bash 开发空间/scripts/build.sh
bash 开发空间/scripts/test.sh
bash 开发空间/scripts/check-artifact.sh
```

构建输出到 `交付产品/轻阅-macOS-0.4.0/轻阅.app`，同时生成 SHA256SUMS 和交付 ZIP；`check-artifact.sh` 会复核版本、架构、签名、校验和及 ZIP 内容。默认构建 universal（x86_64 + arm64），使用 macOS 13 SDK 和 ad-hoc 本机签名；尚未做 Developer ID、公证和 Apple Silicon 实机验收。

需要单架构包时，可以使用 `QINGYUE_ARCH=x86_64` 或 `QINGYUE_ARCH=arm64`；检查单架构产物时相应设置 `QINGYUE_EXPECTED_ARCH=x86_64` 或 `QINGYUE_EXPECTED_ARCH=arm64`。

CI 配置保存在 `docs/ci/macos.yml.template`，目前未启用；本地完整回归和产物检查已执行。

## 文档

- [Mac 0.4.0 方案与验收](docs/Mac0.4.0方案与验收.md)
- [Mac 构建 14 验收](docs/Mac构建14验收.md)
- [跨平台版本与功能对齐规则](docs/跨平台版本与功能对齐规则.md)
- [Windows 用户反馈问题记录（2026-11-27）](docs/issues/2026-11-27-windows-feedback.md)
- [GitHub 与本地整理记录](docs/GitHub与本地整理记录.md)
- [轻阅配色方案（Palette Atlas 版）](docs/配色方案.md)
- [使用说明](操作指导/使用说明.md)

## 平台说明

- Intel Mac：universal 包包含 x86_64 架构。
- Apple Silicon：universal 包包含 arm64 架构，尚未做实机启动和性能验收。
- Windows：独立 WPF 实现保留在 `开发空间/Windows`，版本 0.18.0；本轮已完成 Markdown `<br>` 源码对齐、全量自动回归和安装包重建，并安装到 `D:\Program Files\Qingyue`；目标软件人工粘贴验收及完整 Mac 0.4.0 功能移植仍待完成。
