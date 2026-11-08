# 轻阅 Qingyue

轻量的本地文本阅读与编辑工具：打开方便、阅读舒服、修改可靠。

A lightweight local text reader and editor, starting with macOS.

当前产品版本仍为 **0.1.0**，本轮为 **Mac 构建 2**。下载：[Mac 构建 2 体验包](https://github.com/ljy-codes/qingyue/releases/tag/macos-build-2)；[首个 0.1.0 体验包](https://github.com/ljy-codes/qingyue/releases/tag/v0.1.0)作为历史版本保留。

macOS 客户端使用 Swift、AppKit 与系统文本组件，日常文件读写在本机完成，无账号、后台服务、WebView 或第三方运行时。

## 当前功能

- TXT、Markdown、JSON/JSONC、YAML、INI/CONF/CONFIG 和 XML 文本编辑。
- 多标签、最近文件、拖入编辑区、行号、自动换行、跳转行、撤销与重做。
- 查找替换支持大小写、正则、全词、匹配计数与高亮，以及正则捕获组替换。
- 阅读模式内查找和目录导航，保持阅读模式；源文与阅读视图采用块级对应，便于切换时继续处理同一段内容。
- 正文版心和专注布局；TXT/Markdown 侧重正文排版，JSON/YAML/配置等文本保留等宽缩进与高亮。
- Markdown 标题、列表、任务列表、删除线、代码、链接和基础表格的原生阅读。
- 阅读与编辑外观设置、系统外观跟随、位置记忆；每个文件可保留自己的阅读/编辑模式。
- 标签切换快捷键与右键文件操作，复制路径、在 Finder 中显示；窗口位置记忆，关闭窗口与退出应用分开。
- Tab / Shift+Tab 整行缩进，换行继承缩进。
- JSON 检查与保留数字原始精度的格式化；JSONC 支持注释和尾逗号检查。
- UTF-8/BOM、UTF-16 LE/BE、GB18030，保留编码与换行；安全保存、外部修改冲突提示、本机恢复草稿。

完整操作和边界见[使用说明](操作指导/使用说明.md)，本轮变化见[变更记录](CHANGELOG.md)。

常用入口：`⌘F` 阅读内查找，`⌘⇧O` 文档目录，`⌘⇧F` 专注，`⌘⇧[` / `⌘⇧]` 切换标签。关闭窗口后应用仍在运行，从 Dock 重开为空白文档；完整标签会话恢复仍在后续范围。

![macOS 原生阅读与表格](docs/screenshots/macos-reading.png)

## 平台安排

| 平台 | 当前状态 |
| --- | --- |
| Intel Mac，macOS 13+ | 当前客户端和构建目标 |
| Apple Silicon Mac | 尚未交付原生 ARM 包，也未做实机验收 |
| Windows | 单独实现，当前没有客户端或安装包；[后续实现记录](docs/Windows后续实现.md) |
| Linux | 后续方向，尚未实施 |

本机只处理 Mac 版本。Windows 的需求、差异和验证项记录在本仓库，留待 Windows 环境独立处理；不会将当前 Mac 界面或 Darwin 文件操作直接当作 Windows 实现。

## 构建与测试

安装 Xcode Command Line Tools 后，在仓库根目录运行：

```bash
bash 开发空间/scripts/build.sh
bash 开发空间/scripts/test.sh
```

应用生成到 `交付产品/轻阅.app`。产品版本与构建编号读取自 `开发空间/Resources/Info.plist`：当前分别为 `0.1.0` 和 `2`。构建缓存与应用产物不提交到 Git，仓库保存源码、示例、测试与平台记录。

当前脚本编译 Intel macOS 13 目标，执行 ad-hoc 本机签名；正式 Developer ID 签名、公证及自动更新仍待后续处理。构建不会自动替换 `/Applications` 中已安装的副本。

## 范围与性能

优先保证轻量与可靠读写。单文件上限 50 MiB；超过 200,000 个 UTF-16 单元时暂停高亮与 Markdown 排版；搜索上限 5,000,000 单元，复杂正则有时间预算。安装体积与占用应以每个构建的实际验收为准，历史小文件快照不是大文档性能保证。

Markdown 仍为轻量原生排版，图片显示文字占位，不执行脚本或自动下载远程图片。原文与阅读内容的定位是块级近似，不承诺任意字符选区都精确互转。完整 CommonMark/GFM、完整 YAML 校验、会话恢复、多窗口、目录全局搜索、超大日志、Office 文档、云同步与插件尚未实现。

文件写回仍由用户明确保存；恢复草稿存放在本机应用支持目录，不等同于保存原文，也不等同于完整标签会话。

![Mac 构建2的专注阅读](docs/screenshots/macos-focus-build2.png)

验证细节见[Mac构建2验收](docs/Mac构建2验收.md)，Windows的独立处理记录见[Windows后续实现](docs/Windows后续实现.md)。
