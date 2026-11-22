# Mac 构建 11 挑刺记录

审查日期：2026-11-22。审查对象：`/Applications/轻阅.app`，产品版本 0.2.0、CFBundleVersion=11；代码分支 `codex/build11-themes`。

## 总体结论

本轮已把基线从错误的 main/build 5 改回完整的 Mac 构建 10（`feat/macos-030-parity`），构建 10 的 40 个源文件和既有功能没有回退；新增 7 套外观主题、主题 token、旧设置迁移和 ThemeTests。完整测试套件通过，安装版关于窗口显示 0.2.0 · Mac 构建 11。

但仍有以下问题，按严重度记录。

## P0 / P1

| 优先级 | 问题 | 证据 | 影响 |
|---|---|---|---|
| P0 | 大文件“能打开、不能搜索” | `TextFile.swift:170` 允许 50 MiB；`TextSearch.swift:41` 搜索上限 500 万 UTF-16 单元 | 7 MB 级日志可打开，按 ⌘F 直接报超限 |
| P1 | 关闭文档窗口会清空标签会话 | `ReaderWindow.swift:1030-1037` 确认后 `tabs.removeAll()` 并写空会话 | 红叉关闭窗口后，Dock 重开是空白；与 VS Code/Notepad++ 预期不同 |
| P1 | 设置仍是模态 NSAlert，不是独立 Preferences 窗口 | `ReaderWindow.swift:1370-1385` | 无实时预览、无主题卡片缩略图，必须点“应用”后才知道效果 |
| P1 | 主题选择没有预览和快速切换入口 | 只能从“轻阅 → 设置…”进入；`ReaderView` 菜单无主题子菜单 | 用户需要反复打开设置试色，切换成本高 |
| P1 | 经典深色强调色对比不足 | `Appearance.swift:123` accent `#2F6FDB` 对 editor `#30343A` 约 2.63:1 | 光标、选中边缘、搜索高亮在深色 editor 上偏弱；低于 3:1 UI 建议值 |
| P2 | “明暗外观”在“外观跟随系统”打开时仍可操作 | `ReaderWindow.swift:1368` 与 `1385` | 用户选择的明暗被系统覆盖，控件状态与实际结果不一致 |
| P2 | 暖纸迁移只在初始化时处理，缺少 UI 回归 | `ReaderWindow.swift:223-235` | 逻辑已修正为“仅未跟随系统时迁移为咖色浅色”，但没有自动化 UI 测试覆盖该分支 |
| P2 | 主题 swatches 与主题目录重复维护 | `Appearance.swift:75-84` 的浅色 swatch 与 `ReaderThemeCatalog` 两处色值 | 后续改色容易漂移；目前设置弹窗只用文字下拉，swatches 实际未使用 |
| P2 | 新主题的 main/hover 派生色缺少对比度测试 | `ThemeTests` 只检查 body/muted/accent/accentText，`main/hover` 由 `readerMixed` 计算 | 新主题的工具栏、标签行、hover 可能在极端色卡下对比不足 |
| P2 | 主题对比度测试把 accent 门槛放宽到 2.5 | `ThemeTests.swift:23` | 经典深色 2.63 才能通过，说明测试是在适配现状而不是守住 3:1 目标 |
| P2 | 设置弹窗高度固定、无滚动 | `ReaderWindow.swift:1384` accessory 360×400；实测窗口约 392×596 | 低分辨率或缩放环境下可能超出可用高度 |
| P2 | 咖色暖读的强调色不是文章里的草莓红 | `Appearance.swift` 咖色浅色 accent `#A75832` | 为兼容构建 10 的暖纸 token 做了折中，和配色资料里的 `#C35A64` 有差异 |

## 图标问题

| 优先级 | 问题 | 证据 / 数据 |
|---|---|---|
| P2 | 默认图标内容偏大 | `make-icon.swift:29-32` 用 `Qingyue-sky.png` 铺满 1024 画布；实测内容约 942×954，四周留白约 4%，macOS 常规图标视觉网格约 10% |
| P2 | 1024 ICNS 由 512 PNG 放大 | `Resources/IconThemes/Qingyue-sky.png` 为 512×512；大尺寸 Finder/预览可能偏软 |
| P2 | 四款图标只有固定 PNG，没有深浅/着色变体 | `AppIconTheme.swift` 四款 512 PNG | macOS 新图标的 tinted/dark 变体不会自动适配 |
| P2 | Dock 图标与 Finder 图标不一致 | `AppIconTheme.apply` 只设置 `NSApplication.shared.applicationIconImage`；Finder 仍用 `AppIcon.icns` 天空蓝 | 这是构建 10 已记录的既有行为，但用户可能误以为图标切换失败 |

## 测试与发布

- 完整测试通过；但 `MacParityWorkflowTests` 输出多条 `Unrecognized colorspace number -1` 警告，`RichReadingTests` 提示 CLI 无专用粘贴板服务。这些没有导致失败，但说明部分真实渲染/粘贴板路径未被完整覆盖。
- 设置窗口的主题、图标、字号等控件只做了人工辅助功能检查，没有自动化 UI 测试逐项切换并断言主窗口 token 改变。
- 仓库仍保留旧 `codex/color-themes` 分支（0.2.0 build 5，功能更少）；如果误用它构建或发布，会再次造成功能倒退。发布前应删除/标记该分支，或只从 `codex/build11-themes` 构建。
- README 下载入口仍是 Mac 构建 10；构建 11 尚未创建发布标签，属于本地验证版，不应被描述成已发布下载。

## 本轮已避免的回归

- 没有继续基于 main/build 4 构建；改为 `feat/macos-030-parity`（Mac 构建 10）为基线，恢复 55 个提交、22 个缺失源文件对应的功能。
- 保留分屏、图片、Mermaid、工作区、全局搜索、阅读清单、书签、批注、复制/导出、JSON 结构、格式化、修改行标记和四款图标。
- 修复旧暖纸设置可能被迁移成咖色深色的问题；现在暖纸只在构建 10 原本未跟随系统时迁移为咖色暖读浅色。
- 安装包内使用说明已更新为 0.2.0 / Mac 构建 11，不再带 build 4 的旧版本头。

## 建议修复顺序

1. 大文件搜索改为分块/可取消，至少覆盖 50 MiB 打开上限；或在大文件打开时明确提示“可阅读但不可搜索”。
2. 关闭窗口保留标签会话，或提供独立开关。
3. 设置改为独立 Preferences 窗口：主题卡片、阅读/编辑实时预览、明暗联动。
4. 把经典深色 accent 提升到至少 3:1，并重新设定 ThemeTests 的 accent 下限。
5. 清理 `ReaderThemeID.swatches` 的重复色值，或真正用于主题卡片。
6. 图标按 macOS 80% 视觉网格重做，并保留四款变体；补充 16/32/128px 清晰度检查。
7. 删除旧 build 5 分支、更新 README 发布入口，避免再次误发降级版本。
