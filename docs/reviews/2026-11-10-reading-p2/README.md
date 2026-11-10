# 阅读字号与版心 P2 验收探针

本目录只保存合成文本的无窗口行为探针与实际运行日志，不修改产品代码。**两项 P2 已在主控集成后的 Release 上复核通过：原探针 21/21 通过，退出码 0。** `red.txt` 保留修复前证据，`after-integration.txt` 保存修复后证据；探针及断言未修改。

## 运行

依赖 Windows、PowerShell 7 STA，以及已构建的 Qingyue Windows 产物。每次使用新进程运行，避免 WPF Application 与已加载程序集跨次复用。不需要测试框架，不会构建、显示窗口或调用 InitializeAsync。

仓库根目录下复跑（现有 ReadingTests Release 输出是默认产物目录）：

```powershell
pwsh -NoProfile -STA -File .\docs\reviews\2026-11-10-reading-p2\probe.ps1
$LASTEXITCODE
```

本次留档命令：

```powershell
pwsh -NoProfile -STA -File "D:\专用工具\阅读器\qingyue\docs\reviews\2026-11-10-reading-p2\probe.ps1" -AssemblyDirectory "D:\专用工具\阅读器\qingyue\开发空间\Windows\tests\Qingyue.ReadingTests\bin\Release\net10.0-windows" -LogPath "D:\专用工具\阅读器\qingyue\docs\reviews\2026-11-10-reading-p2\red.txt"
$LASTEXITCODE
```

修复集成并构建后，对相应输出目录运行同一脚本，用新的日志名保留对照，不覆盖 red.txt：

```powershell
$taskAssemblyDirectory = 'D:\专用工具\阅读器\qingyue\开发空间\Windows\tests\Qingyue.ReadingTests\bin\Release\net10.0-windows'
pwsh -NoProfile -STA -File .\docs\reviews\2026-11-10-reading-p2\probe.ps1 -AssemblyDirectory $taskAssemblyDirectory -LogPath .\docs\reviews\2026-11-10-reading-p2\after-integration.txt
$LASTEXITCODE
```

退出码：`0` 全部行为断言通过；`1` 存在行为红灯；`2` 探针宿主/程序集/反射入口或清理异常。宿主异常不能当成产品缺陷证据。

## 集成后复核：通过

2026-11-10 12:21:32 +08:00，使用主控已构建的 ReadingTests Release 输出，在全新 PowerShell STA 进程执行上述原探针，日志保存为 `after-integration.txt`。本轮没有重新构建产品。

| 产物 | 最后修改时间（+08:00） | SHA256 |
| --- | --- | --- |
| Qingyue.dll | 2026-11-10 12:20:40 | A5A2163BDAEC44C98693679FD2C9667BEC15BFE73341941773962BDF64ECF50D |
| Qingyue.Core.dll | 2026-11-10 12:16:45 | 621F11FCC7148E818CE86F1A1BA6A052D4C0872B2C7394559F336CCA31F5FA74 |

| 场景 | 集成后实测 |
| --- | --- |
| 全宽阅读字号 +4 | 正文 16→20，标题 32→40，代码/表格 14.4→18；FlowDocument 保持同一实例 |
| 分屏版心 720→820 | 预览控件和 FlowDocument 均未替换；滚动保持 800；8 字选区及起止偏移 1921..1929 保持 |
| 分屏字号 +4 | 正文/标题/代码/表格同比例缩放；预览控件、FlowDocument、滚动及选区保持 |

结果：**21 项断言通过、行为失败 0、宿主异常 0、退出码 0**。三份合成源文本均未改变，`profileWritten=False`、`visibleWindow=False`。两项 P2 在本探针覆盖的三个场景中均已关闭。

主控另报告真实 ReadingTests **59 项全通过**（含实际字号命令、窗口宽度/侧栏高亮与即时复制）；本轮独立执行的是此无窗口探针，未重复执行 ReadingTests。

## 基线与实际红灯

2026-11-10 12:16:58 +08:00 实测，PowerShell 7.6.9 / .NET 10.0.14 / Windows 10.0.26300 / STA。本次直接读取既有 Release 产物，**没有自行构建当前并行修改的源代码**。留档期间输出目录被并行构建更新，最终 red.txt 对应以下实际加载的产物，而不是此前 11:56 的产物：

| 产物 | 最后修改时间（+08:00） | SHA256 |
| --- | --- | --- |
| Qingyue.dll | 2026-11-10 12:16:52 | 77A47F2CE4B45847F9AB41587AD2887391E80E18F3054596404819D4B48D57EF |
| Qingyue.Core.dll | 2026-11-10 12:16:45 | 621F11FCC7148E818CE86F1A1BA6A052D4C0872B2C7394559F336CCA31F5FA74 |

完整时间、命令、DLL 大小/哈希、逐项断言及退出码见 `red.txt`。21 个断言中 11 个失败，退出码 **1**；没有 HOST ERROR。

| 缺陷 / 场景 | 预期 | 红灯 Release 实测 |
| --- | --- | --- |
| P2：全宽阅读字号 +4 | 正文 16→20，标题/代码/表格同比例变为 40/18/18，保持原文档 | 正文变为 20，标题/代码/表格仍为 32/14.4/14.4；3 项失败 |
| P2：分屏版心 720→820 | 保持原控件、文档、选区及滚动 | 控件/文档替换，滚动 800→0，选区 8→0；4 项失败 |
| 同一 P2：分屏字号 +4 | 保持原控件、文档、选区及滚动，同时缩放各类文字 | 字号随重建更新，但控件/文档替换，滚动 800→0，选区 8→0；4 项失败 |

三个场景的合成源文本均未改变。字体断言按实际初始字号与目标正文比例计算；选区同时比较文字和文档内起止偏移。滚动断言使用此固定 680×400 布局下的偏移，容差 1 DIP。

## 调用链与范围

- 全宽：`NewDocument → ToggleReading → settings.FontSize += 4 → ApplyEditorSettings → UpdateReadingWidth`。审查时 `MainWindow.Reading.cs:114–116` 只改 FlowDocument 字号，标题/代码/Table 的局部字号未更新。
- 分屏：`ToggleMarkdownPreview → 选择并滚动 → 修改 ReadingWidth / FontSize → UpdateReadingWidths / ApplyEditorSettings → RefreshMarkdownPresentation`。审查时 `MainWindow.Markdown.cs:81–89` 因设置变化重新创建预览。这里测试的是阅读宽度设置变化，不是普通窗口 resize。
- 探针以反射调用现有私有集成入口，无额外产品钩子。将来入口改名或夹具渲染失败应报退出码 2。

所有控件仅手动 Measure/Arrange/UpdateLayout；不调用 Show、Application.Run，不 pump Dispatcher。已设置 DispatcherSynchronizationContext，避免 QueueOutline 的异步续体因无效宿主转到线程池。最终调用 CloseForShutdown 和 Application.Shutdown，并记录 `profileWritten=False`、`visibleWindow=False`。

本探针不验证真实键盘焦点、窗口 Loaded 后的延迟任务、搜索框键入或复制行为；这些由对应集成测试覆盖。它也不能替代完整 ReadingTests。
