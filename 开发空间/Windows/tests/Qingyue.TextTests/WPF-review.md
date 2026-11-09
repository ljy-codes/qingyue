# WPF 搜索与阅读只读审查

日期：2026-11-09。只读审查，未修改 UI 文件。文件行号对应本次审查版本，父代理可继续修改。

## 最终处理结果

以下保留最初审查记录，所列五项问题在最终集成中均已处理：

- 阅读恢复和切类型传递实际源文锚点；WPF 流程检查覆盖重启与阅读中切换。
- 编辑后立即失效旧目录并防抖刷新；WPF 流程检查覆盖新增标题。
- 接入不可变文本位置索引，以二分转换行号和 LF/CRLF 偏移；5 组边界、随机对照及百万字符检查通过。
- 仅查找时不传隐藏的替换模板；WPF 流程检查覆盖大替换模板残留。
- YAML 明确说明仅检查缩进 Tab；Plain、Markdown、INI 不再宣称完整语法检查通过。

最终 Release 为 0 警告、0 错误；WPF 流程 33 项通过，独立映射审查 0 失败。完整结果见仓库 `docs/test.md`。
下文行号仅供理解原始问题，不代表修正后的文件位置。

已运行独立 WPF renderer harness：中文、emoji、单 Run 多行、跨 Markdown 块与表格的 `OffsetAt(PointerAt(offset))` 全部正确。没有证据表明这些样本存在 CRLF 导致的映射漂移。

## P2：恢复阅读和阅读中切换类型会覆盖真实阅读锚点

定位：

- `src/Qingyue.Windows/MainWindow.xaml.cs:71` 恢复已保存 `SourceAnchor`，第 74 行调用 `EnterReading(tab)`。
- `src/Qingyue.Windows/MainWindow.Reading.cs:30` 无条件用编辑器的旧 `CaretIndex` 重写 `SourceAnchor`。
- `src/Qingyue.Windows/MainWindow.Commands.cs:202` 清空旧模型，第 203 行同样调用 `EnterReading`，没有先保存预览选区对应的源偏移。

复现：在编辑器开头进入阅读，跳至文章末尾后退出并恢复会话；或保持阅读在末尾时切换文本类型。阅读位置回到编辑器旧光标处，而非阅读位置。恢复项中的阅读锚点实际未被使用。

建议：`EnterReading` 接收显式源锚点；恢复时传保存值，普通进入时取编辑器光标，阅读中重建时先通过旧 `Rendered.OffsetAt` + 旧 `Reading.DisplayToSource` 获取锚点，再丢弃旧模型。
另需注意大文档原文阅读：`ToggleReading` 第 16 行在 `Rendered == null` 时使用进入阅读时的旧锚点，阅读中搜索或移动光标后切回编辑会跳回原处；应取当前只读编辑器的标准化偏移。

## P2：编辑事件只更新状态，目录长期保留旧内容

定位：`src/Qingyue.Windows/MainWindow.xaml.cs:93` 的 `TextChanged` 处理器清空 `tab.Reading`，但第 101 行只执行 `RefreshStatus()` 和 `QueueSearch()`；目录的 `ItemsSource` 只在第 139 行 `RefreshCurrent()` 中重建。

复现：空文档输入 `# 新标题`，或在已有标题前插入正文/删除标题。停留当前标签时目录不更新；旧目录项的 `SourceStart` 继续用于跳转，点击可能跳到无关位置。切换标签或阅读模式后才重建。

建议：给目录独立的防抖刷新与版本检查；内容变更后立即使旧目录项失效，刷新时校验文档版本，避免同步重建每个按键的阅读模型。

## P2：光标、滚动及会话捕获在 UI 线程重复扫描全文前缀

定位：

- `MainWindow.xaml.cs:104-105` 每次选区变更同步触发 `RefreshLocation`；第 171 行从 0 遍历到当前光标。
- `EditorTab.cs:73` 每次滚动触发 `UpdateLineNumbers`；第 91 行将 `previousOffset` 置 0，第 97 行重新扫描到当前首行（已看到最新加入的重入保护，该保护不消除扫描成本）。
- `MainWindow.Reading.cs:54-55` `SourceOffset` 截取并规范化整个光标前缀；`EditorOffset` 第 61 行也从 0 扫描。
- `MainWindow.Persistence.cs:19-24` 对每个标签获取多次 `SourceOffset`，此阶段在 UI 线程、跨 await 之前完成。

触发：接近 50 MiB 文件末尾连续方向键、拖动选区、滚动，或同时打开很多长文档进行自动保存。这些路径的工作量随文件前缀长度增长，导致重复 O(n) 扫描和大字符串分配；后台搜索并不能消除这部分 UI 线程成本。本次未做窗口交互延迟基准，不声称具体卡顿时长。

建议：按文档版本维护 LF/CRLF 与行首索引，使用二分转换源/编辑偏移和行号；滚动事件仅处理可见行，状态更新合并到低频 dispatcher；会话捕获复用每个标签的索引和计算结果。

## P2：仅查找也会受隐藏替换文本的结果上限影响

定位：`MainWindow.Search.cs:40` 无条件读取 `ReplacementText.Text`，第 49 行连同替换文本传入 `SearchEngine.Find`。

复现：先设置较长替换内容，再 Ctrl+F 切换到仅查找；大量匹配本可正常导航，但 Find 会因预测替换结果超过 1000 万而返回错误。搜索的资源保护本身正确，问题是仅查找模式传入了隐藏的替换模板。

建议：仅查找时传空替换；显示替换面板时才展开和检查替换内容，或把匹配与替换预览任务分开。

## P2：YAML “检查通过”会暗示并未执行的语法校验

定位：`MainWindow.Commands.cs:112` 对所有格式将 `Diagnostic == null` 统一展示为“检查通过”。

复现：YAML 文本 `a: [not complete` 无缩进 Tab，TextServices 按约定返回 null，UI 显示“检查通过”。这不代表 YAML 语法正确。

建议：YAML 明确展示“未发现缩进 Tab；未进行完整 YAML 语法校验”，Plain/Markdown/INI 也应说明当前没有完整语法校验器。
