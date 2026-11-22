# Mac 构建 12 挑刺记录

审查对象：`/Applications/轻阅.app`，0.2.0 / Mac 构建 12；分支 `codex/build11-themes`。构建 11 挑刺记录中的高优先级问题已处理，以下为构建 12 后仍需关注的问题。

## 已处理

- 大文件查找：5000 万单元上限 + ASCII 快速路径 + 6M 回归。
- 关闭窗口：保留标签会话，UIWorkflow 更新为“重开恢复原标签”。
- 设置窗口：独立可滚动窗口、7 张主题卡、实时阅读/编辑预览、主窗口设置按钮和视图菜单入口。
- 经典深色 accent：`#4F86F7`，对比度约 3.63:1；ThemeTests 门槛恢复 3.0。
- 图标留白：默认 AppIcon 增加 5% 外边距。
- 设置控件可访问名称：主题、图标、字号、字体、行距、段距、版心。

## 仍存在的问题

| 优先级 | 问题 | 证据 | 说明 |
|---|---|---|---|
| P1 | 大文件搜索仍无进度/取消细节 | `TextSearch.swift` 仍为内存内枚举，UI 只显示“正在查找…” | 50M 上限已放开，但超大文件/复杂正则可能只看到超时；没有分块索引 |
| P1 | 非 ASCII 大文本没有字节快速路径 | `literalMatches` 仅对 ASCII 走 `asciiLiteralMatches` | 中文/Emoji 大日志仍走 Foundation，可能比 ASCII 慢很多 |
| P2 | 主题仍为全局预设 | `ReaderPreferencesState.themeID` 无文档级字段 | 不能为单个文档保留不同主题；无自定义主题导入 |
| P2 | 预览高度固定 | `ThemePreviewPanel` 高度 158 | 字号 30 或长段落会裁剪；设置窗口整体可滚动，但单个预览不滚动 |
| P2 | 主题 main/hover 派生色缺少对比度测试 | `ThemeTests` 只测 body/muted/accent/accentText | 新主题的工具栏、标签行、hover 仍未自动校验 |
| P2 | 四款图标资源仍是 512 PNG | `Resources/IconThemes/*.png` | 大尺寸可能偏软；无 dark/tinted 变体；Finder 仍固定天空蓝 |
| P2 | 设置窗口较大 | 默认 900×740，滚动兜底 | 小屏/低缩放下需要滚动；不是原生 Preferences 风格的分组页 |
| P2 | 状态栏会被设置应用覆盖 | `applyPreferences` 最后 `updateStatus("外观设置已更新")` | 会暂时覆盖文件/行列状态，几秒后不会自动恢复 |
| P2 | 旧 build 5 分支仍存在 | `codex/color-themes` | 误用会重新构建出缺少构建 10 功能的版本；发布前应删除或明确废弃 |
| P2 | README 下载入口仍是构建 10 | `README.md` | 构建 12 是本地验证版，未创建发布标签 |
| P2 | 测试环境警告 | `MacParityWorkflowTests` 输出 colorspace 警告；`RichReadingTests` 无专用粘贴板服务 | 测试通过，但真实渲染/剪贴板路径未完全覆盖 |
| P2 | 设置 UI 无自动化回归 | 主题卡、图标、字号等只做人工辅助功能检查 | 没有测试逐项切换并断言主窗口 token 变化 |

## 建议下一步

1. 为超大日志增加分块/增量搜索与进度、取消提示；非 ASCII 文本评估更低成本的 Unicode 折叠路径。
2. 主题改为 token + 可导入自定义主题；可选按文档主题。
3. 把设置窗口分成“外观 / 阅读 / 编辑 / 图标”分组页，预览区独立滚动。
4. 图标输出 1024 资源并补 dark/tinted 变体；为四款图标做 16/32/128px 清晰度检查。
5. 删除旧 build 5 分支，更新 README 发布入口，避免再次误发降级版本。
