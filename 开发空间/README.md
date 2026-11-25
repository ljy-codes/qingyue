# 轻阅 · 开发空间

当前版本：

- Mac **0.4.0 · 构建 14**，Swift / AppKit，目标 macOS 13+，universal `x86_64 + arm64`。
- Windows **0.19.0 · Windows 构建 1**，.NET 10 / WPF，目标 Windows 10 22H2 / Windows 11 x64。

两端分别使用原生实现，共享文件语义、功能契约、测试样例和验收标准。Windows 工程位于 `Windows/`，Mac 工程位于当前目录的 `Sources/`、`Resources/`、`Tests/` 和 `scripts/`。

## 目录

- `Sources/`：Mac 界面、文件读写、编辑、搜索、阅读、主题、工作区与批注。
- `Resources/`：Mac Info.plist 与图标资源。
- `Tests/`：Mac 文件、文本、阅读、搜索、编辑、主题、布局、窗口与工作区测试。
- `scripts/build.sh`：Mac 编译、签名、打包和校验文件生成。
- `scripts/test.sh`：Mac 完整回归测试入口。
- `scripts/check-artifact.sh`：复核交付包版本、架构、签名、校验和与 ZIP 内容。
- `Windows/`：Windows WPF 源码、测试、安装器和构建脚本。
- `Samples/`：TXT、Markdown、JSON、YAML 和配置示例。

## Mac 构建与测试

需要 Xcode Command Line Tools。

```bash
bash scripts/build.sh
bash scripts/test.sh
bash scripts/check-artifact.sh
```

交付输出为 `交付产品/轻阅-macOS-0.4.0/轻阅.app`、`SHA256SUMS.txt` 和 `轻阅-macOS-0.4.0-交付.zip`。构建缓存写入 `build/`，不提交 Git。

默认构建 universal，使用 macOS 13 SDK 与 ad-hoc 本机签名。Apple Silicon 包已生成，但尚未完成 Apple Silicon 实机、Developer ID 和公证验收。构建不会自动替换 `/Applications` 中的已安装副本。

## Windows 构建与测试

需要 .NET 10 SDK 和 Windows Desktop targeting pack；自包含发布另需 win-x64 运行时包。安装器构建需要 NSIS 3.11、MSVC x86 Build Tools 和 Windows SDK。

```powershell
pwsh -File Windows/scripts/build.ps1
pwsh -File Windows/scripts/test.ps1
pwsh -File Windows/scripts/publish.ps1
pwsh -File Windows/scripts/build-installer.ps1
```

Windows 输出：

- `交付产品/轻阅-Windows-x64/Qingyue.exe`
- `交付产品/轻阅-0.19.0-Windows构建1-x64.zip`
- `交付产品/轻阅-0.19.0-Windows构建1-x64-setup.exe`

Windows 构建和验收细节见[Windows README](Windows/README.md)及[验收记录](../docs/test.md)。

## 共同功能契约

- TXT、Markdown、JSON/JSONC、YAML、INI/CONF/CONFIG 和 XML。
- 多标签、快速打开、工作区、全局搜索、阅读清单、书签、批注和阅读简报。
- 阅读/编辑、Markdown 分屏、目录、块级位置映射、本地图片、表格编辑和有限 Mermaid。
- 七套主题、独立设置、四款 1024px 应用图标、系统外观跟随和阅读排版设置。
- 会话恢复、独立草稿、草稿排除/清理、外部修改检测和三路处理。
- 安全保存、编码/BOM、换行保持、ACL/权限/占用/链接保护。
- 选区图文复制、纯文本/Markdown/代码块/表格复制和自包含 HTML 导出。

系统菜单、快捷键、文本组件、剪贴板、文件对话框、签名和安装器允许使用平台原生实现。新增共同能力必须同时记录两端实现和测试结果。

## 文件与恢复约定

文件写回必须由用户明确保存；恢复草稿只用于本机恢复，不自动覆盖原文件。会话只保存文档引用、标签顺序、活动标签和显示状态；草稿使用每文档独立不可变快照。

Mac 与 Windows 的用户数据目录相互独立。Windows 默认位于 `%LOCALAPPDATA%\Qingyue`，可通过 `QINGYUE_SUPPORT_DIRECTORY` 指定隔离目录。清理草稿必须经过持久化串行队列，防止旧快照在清理后重新出现。

## 资源边界

- 单文件上限 50 MiB。
- 字面搜索上限 5,000 万 UTF-16 单元，复杂正则有超时和结果数量保护。
- Markdown 富预览正文上限 50 万 UTF-16 单元；JSON 结构视图另有 200 万单元上限。
- 单图最多 8 MiB、1600 万像素、单边 32768，解码最长边 4096。
- 单篇最多 32 张图、32 MiB 图片数据、4800 万解码像素。
- Mermaid、剪贴板和 HTML 导出各有独立源码、图形和输出载荷预算。

源文与阅读显示范围使用 UTF-16 块级映射，不承诺任意字符或像素级互转。图片、Markdown 和图表错误必须保留源码或明确降级，不能生成空白成功结果。

## 验收原则

自动测试通过不能替代目标软件人工粘贴、Apple Silicon 实机、不同 Windows 版本/缩放、输入法候选和真实设备性能验收。平台等价语义必须分别验证，不能把 macOS POSIX ACL/xattr 直接等同于 Windows ACL/重解析点。
