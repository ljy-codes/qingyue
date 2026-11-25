# 轻阅 Windows

.NET 10 + WPF 原生 Windows 客户端。Core 不依赖第三方 NuGet 包，文件、文本、阅读模型和界面流程分别测试。

## 当前版本

- 产品版本：`0.19.0`
- Windows 构建：`1`
- 文件版本：`0.19.0.1`
- InformationalVersion：`0.19.0+windows-build.1`
- 目标：Windows 10 22H2 / Windows 11 x64

Windows 0.19.0 已按 Mac 0.4.0 的用户功能契约完成对齐。功能矩阵与验证边界见[功能对照表](../../docs/功能对照表.md)。

## 已对齐能力

- 六组原生菜单：文件、编辑、搜索、视图、标签、帮助；菜单打开和关闭后恢复原编辑器焦点与选区。
- Ctrl 快捷键白名单与 Mac 命令契约对应；Ctrl+1…8 选择标签，Ctrl+9 选择最后标签。
- 非模态快速打开；独立设置窗口；七套主题与四款 1024px 应用图标。
- 工作区、本地文件树、类型/通配过滤、收藏目录和跨文件全局搜索。
- 全局搜索普通/正则/全词/大小写、替换预览、取消、结果上限和写回前磁盘复检。
- 阅读清单、固定、已读/未读、进度和上次位置恢复。
- 批注、标签、颜色、锚点修复、独立高亮、书签，以及 Markdown/HTML 阅读简报导出。
- Markdown `<br>`、表格编辑、TSV 粘贴为表格、图片插入、本地/内嵌图片和有限离线 Mermaid。
- 纯文本、Markdown、代码块、表格和富文本复制，以及自包含 HTML 导出。
- 外部文件修改三路处理：保留当前编辑、接纳磁盘版本、重新检查。
- 会话恢复、当前文件草稿排除、全局草稿开关、清除草稿和损坏数据隔离。
- 文件 ACL、只读、占用、硬链接、重解析点、编码/BOM、换行和安全原子替换保护。

Windows 使用原生设置窗口、资源管理器、文件对话框、Ctrl 快捷键和 WPF 文本控件，不照搬 AppKit 控件。Windows 内嵌编辑工具条属于平台呈现方式，不构成功能缺失。

## 验证结果

2026-11-25 全量回归：

| 检查 | 结果 |
| --- | --- |
| Release 构建 | 0 警告、0 错误 |
| WPF 流程 | 984 项通过 |
| 文件核心 | 44/47 通过，3 项符号链接因令牌权限跳过 |
| UI 行为回归 | 142 项通过 |
| 编辑/文本 | 57 + 34 + 198 项通过 |
| Markdown / 图表 | 43 + 29 项通过 |
| 阅读 / 图片 / 剪贴板 | 65 + 44 + 63 项通过 |
| 实例 / 外部刷新 / 导航 | 16 + 97 + 34 项通过 |
| 恢复 / 阅读体验 / 批注 / 阅读成果 | 22 + 21 + 34 + 198 项通过 |
| 文本位置与 WPF 指针映射 | 5/5 通过，0 失败 |

3 个符号链接用例需要 `SeCreateSymbolicLinkPrivilege`，当前普通令牌无法创建测试链接，因此明确记录为 SKIP，不宣称通过。

## 构建与打包

在仓库根目录执行：

```powershell
pwsh -File 开发空间/Windows/scripts/build.ps1
pwsh -File 开发空间/Windows/scripts/test.ps1
pwsh -File 开发空间/Windows/scripts/publish.ps1
pwsh -File 开发空间/Windows/scripts/build-installer.ps1
```

输出：

- `交付产品/轻阅-Windows-x64/Qingyue.exe`
- `交付产品/轻阅-0.19.0-Windows构建1-x64.zip`
- `交付产品/轻阅-0.19.0-Windows构建1-x64-setup.exe`

采用自包含目录发布，整个目录必须一起保留。版本源为 `Directory.Build.props` 和 `installer/version.nsh`。安装包默认未签名；正式外部分发前必须补充 Authenticode 签名。

## 工程入口

- `src/Qingyue.Core`：文件安全、会话、工作区、全局搜索、阅读清单、格式、批注、简报和阅读模型。
- `src/Qingyue.Windows`：WPF 窗口、编辑器、阅读渲染、设置、主题、图片、Mermaid、剪贴板和恢复。
- `tests/Qingyue.FileTests`：真实临时文件、ACL、链接、占用、外部冲突、编码与草稿。
- `tests/Qingyue.UITests`：真实 STA/WPF 菜单、快捷键、设置、快速打开、全局搜索、工作区、阅读清单和焦点流程。
- `tests/Qingyue.EditingTests`：选择、行编辑、Markdown 结构命令、输入法隔离和分屏定位。
- `tests/Qingyue.MarkdownTests`：分色、嵌套列表、预览、`<br>` 与阅读映射。
- `tests/Qingyue.ClipboardTests`：纯文本、RTF、XamlPackage、HTML、图片和选区复制。
- `tests/Qingyue.ReadingTests` / `ReaderExperienceTests`：阅读布局、表格、图片、导航与资源边界。
- `tests/Qingyue.ImageTests`：本地/内嵌图片、路径、解码预算和实际样本。
- `tests/Qingyue.AnnotationTests` / `ReadingOutcomeTests`：批注锚点、高亮、主题和简报。
- `tests/Qingyue.ExternalChangesTests` / `NavigationTests` / `RecoveryTests`：外部修改、取消、恢复和退出一致性。
- `tests/Qingyue.InstanceTests` / `InstanceIpcTests`：单实例转发、激活和命名管道。

## 安装器

安装器使用自定义 NSIS 深色界面，提供可选安装目录、开始菜单、可选桌面快捷方式、系统卸载登记和覆盖安装。正式安装写入 `HKLM`；隔离测试包使用 `HKCU`。

默认卸载保留用户数据；彻底清理需要明确勾选和二次确认，仅删除轻阅自己的默认数据目录。程序文件按安装清单删除，未知文件保留。静默卸载始终保留用户数据。

安装器回归：

```powershell
pwsh -File 开发空间/Windows/scripts/test-installer-contract.ps1
pwsh -File 开发空间/Windows/scripts/test-installer.ps1
pwsh -File 开发空间/Windows/scripts/test-installer-purge.ps1
```

## 实机边界

本项目已完成 Windows x64 开发机上的构建、自动回归和安装验收。以下项目仍需按目标环境另行验收：

- Word、Excel、WPS、浏览器和邮件客户端的人工富文本粘贴结果。
- 不同 Windows 版本、DPI 缩放、多屏和输入法候选矩阵。
- Authenticode 签名、SmartScreen 信誉和外部正式分发。
- 缺少符号链接创建权限时的真实链接写入场景。
