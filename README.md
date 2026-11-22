# 轻阅 Qingyue

轻量的本地文本阅读与编辑工具，面向 Intel 与 Apple Silicon Mac / macOS 13+。

当前版本：**0.4.0 · Mac 构建 14（发布准备）**。

## 安装与交付

- 本机安装：`/Applications/轻阅.app`
- 交付目录：[轻阅-macOS-0.4.0](交付产品/轻阅-macOS-0.4.0/)
- 交付压缩包：[轻阅-macOS-0.4.0-交付.zip](交付产品/轻阅-macOS-0.4.0-交付.zip)
- 校验文件：[SHA256SUMS.txt](交付产品/轻阅-macOS-0.4.0/SHA256SUMS.txt)

交付目录由构建脚本生成；发布准备阶段以验收记录中的检查结果为准。

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

![外观主题设置](docs/screenshots/macos-build12-settings.png)

## 构建与测试

依赖 Xcode Command Line Tools。

```bash
bash 开发空间/scripts/build.sh
bash 开发空间/scripts/test.sh
bash 开发空间/scripts/check-artifact.sh
```

构建输出到 `交付产品/轻阅-macOS-0.4.0/轻阅.app`，同时生成 SHA256SUMS 和交付 ZIP；`check-artifact.sh` 会复核版本、架构、签名、校验和及 ZIP 内容。默认构建 universal（x86_64 + arm64），使用 macOS 13 SDK 和 ad-hoc 本机签名；尚未做 Developer ID、公证和 Apple Silicon 实机验收。

需要单架构包时，可以使用 `QINGYUE_ARCH=x86_64` 或 `QINGYUE_ARCH=arm64`；检查单架构产物时相应设置 `QINGYUE_EXPECTED_ARCH=x86_64` 或 `QINGYUE_EXPECTED_ARCH=arm64`。

## 文档

- [Mac 0.4.0 方案与验收](docs/Mac0.4.0方案与验收.md)
- [Mac 构建 14 验收](docs/Mac构建14验收.md)
- [跨平台版本与功能对齐规则](docs/跨平台版本与功能对齐规则.md)
- [Mac 构建 12 验收（历史）](docs/Mac构建12验收.md)
- [Mac 构建 12 挑刺记录（历史）](docs/Mac构建12挑刺记录.md)
- [轻阅配色方案（Palette Atlas 版）](docs/配色方案.md)
- [使用说明](操作指导/使用说明.md)

## 平台说明

- Intel Mac：universal 包包含 x86_64 架构。
- Apple Silicon：universal 包包含 arm64 架构，尚未做实机启动和性能验收。
- Windows：独立 WPF 实现保留在 `开发空间/Windows`，版本 0.18.0；Windows 构建、安装器和验收需在 Windows 环境单独进行，不计入本轮 Mac 交付。
