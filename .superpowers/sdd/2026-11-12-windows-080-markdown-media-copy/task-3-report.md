# Windows 0.8.0 Task 3 实施报告

## 当前状态

- 基线分支为 `feat/windows-client`，实现前 HEAD 为 `135a77f`。
- Task 3 已完成，只修改 Windows 剪贴板复制链路和对应测试；未修改 Mac 工程，未引入运行时依赖。
- 全文、选区、代码块和表格复制均经过快照路径；富文本失败时保留纯文本并记录降级原因。
- 已提交：`f86bbcd feat(windows): unify markdown clipboard snapshots`。

## 红测

命令：

```powershell
dotnet build tests/Qingyue.ClipboardTests/Qingyue.ClipboardTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ClipboardTests/bin/Release/net10.0-windows/Qingyue.ClipboardTests.dll
```

结果：

- 构建通过：0 警告，0 错误。
- 实现前测试结果：`Clipboard checks: 53/55 passed`。
- 预期失败项：

```text
FAIL 复制快照只依赖已冻结资源
FAIL 富文本导出失败仍保留 UnicodeText
Clipboard checks: 53/55 passed
```

- 失败原因分别是 `CreateSnapshot`/冻结快照行为尚未实现，以及富文本导出失败时尚未返回只含 `UnicodeText` 的降级对象。

## 设计决定

- `ReadingClipboardSnapshot` 保存纯文本、CF_HTML、RTF、XamlPackage、Bitmap 和 `ReadingImageReport`；图片在进入快照前冻结，XamlPackage 在组装 `DataObject` 时复制流，避免继续依赖实时文档或可变资源。
- `CreateSnapshot` 先完成白名单 HTML 导出和脱离原文档的 `RichRange()`，再由 `CreateData(snapshot)` 统一生成全文、代码块和表格的 `DataObject`；选区的 WPF 原生复制扩展也先经过同一快照。
- `DataObject` 先写入纯文本，再尝试 RTF、XamlPackage、Bitmap 和 HTML。任一富文本格式遇到 `ExportLimitException`、`IOException`、`InvalidOperationException`、`COMException` 或 `SecurityException` 时，丢弃半成品并返回只含 `UnicodeText` 的对象。
- WPF `Attach` 路径把 HTML 放在富文本格式写入最后，避免失败时对外发布不完整 HTML；主窗口状态栏显示降级原因。
- `ReadingHtmlExport` 已经复用同一白名单导出与缓存像素逻辑，本任务无需改动；现有 `MainWindowSharingChecks` 覆盖导出链路，本任务没有扩展无关测试。

## 修改文件

- `开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs`
  - 增加快照、统一数据组装、冻结图片和原子纯文本降级。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs`
  - 显示富文本降级原因。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml.cs`
  - 显示富文本降级原因。
- `开发空间/Windows/tests/Qingyue.ClipboardTests/Program.cs`
  - 增加完整格式、自包含图片和 Bitmap 数量断言。

## 最终验证

命令：

```powershell
dotnet build tests/Qingyue.ClipboardTests/Qingyue.ClipboardTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ClipboardTests/bin/Release/net10.0-windows/Qingyue.ClipboardTests.dll
```

结果：

- 构建通过：0 警告，0 错误。
- ClipboardTests 通过：`Clipboard checks: 56/56 passed`。
- 退出码为 0。
- `git diff --check` 通过；仅有 Git 关于工作区 LF/CRLF 转换的提示。

## 兼容性与风险

- 保留 `ReadingClipboardResult` 及既有导出接口；无 Mac 工程影响，无新增运行时依赖。
- 富文本生成失败时会主动丢弃 RTF、XamlPackage、Bitmap 和 HTML，仅保留 `UnicodeText`，调用方需使用 `DegradationReason` 识别该状态。
- 快照冻结和 XamlPackage 流复制会增加一次资源复制开销；现有复制预算仍限制文本、元素、图片和原生格式大小。
- ClipboardTests 覆盖了 WPF 内存格式回读和 CF_HTML 片段结构；不同第三方目标应用的格式优先级仍可能存在差异。

## Fix round 1：SingleBitmap 严格语义

- 根因：`Export` 的 `singleBitmap` 赋值只按 `EmbeddedImages == 1` 更新；渲染器产生的“图表暂未显示”占位不会增加 `OmittedImages`，因此“图片 + 占位”仍可能发布 `DataFormats.Bitmap`。
- 修改：在 `Export` 中记录可见非图片内容（换行和渲染器占位），两处 `singleBitmap` 赋值统一经过 `EmbeddedImages == 1`、无省略图片、无文本和无其他可见内容的资格判断。
- 回归断言：`ClipboardSnapshotFormats` 增加“有效图片 + 图表占位不发布 Bitmap”断言；修复前失败，修复后通过。
- 修改文件：`开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs`、`开发空间/Windows/tests/Qingyue.ClipboardTests/Program.cs`。
- 验证：`dotnet build tests/Qingyue.ClipboardTests/Qingyue.ClipboardTests.csproj -c Release --disable-build-servers -m:1 -nr:false` 通过，0 警告、0 错误；`dotnet tests/Qingyue.ClipboardTests/bin/Release/net10.0-windows/Qingyue.ClipboardTests.dll` 通过，`Clipboard checks: 56/56 passed`。

## Fix round 1：Attach 原子降级与快照一致性

- 根因：`Attach` 的 `SettingData`/`Copying` 处理曾把 RTF、XamlPackage、Bitmap 和 HTML 逐项写入 WPF 原生 `DataObject`，异常发生后可能留下半成品；选择纯文本还会回读实时 `preview.Selection.Text`。
- 修改：`SelectionPayload` 现在只保存同一冻结快照生成的 `ReadingClipboardResult`。`SettingData` 准备成功后始终取消 WPF 实时 XAML；`Copying` 在局部完成结果判断和格式读取后再发布，发生降级时先取消原生 Copying，再通过独立 `DataObject` 发布 UnicodeText/Text，并触发可选降级回调。
- 兼容性：`Attach(RichTextBox, Func<bool>)` 两参数调用保持兼容；无图选区也使用快照生成的 RTF/XamlPackage；XamlPackage 进入事件对象前复制 MemoryStream 并重置 Position。
- UI：主窗口阅读预览收到降级原因后显示“复制已降级：…”，代码块和表格右键复制也显示同一原因。
- 回归断言：ClipboardTests 覆盖 Attach 降级结果仅保留 UnicodeText/Text、SettingData 取消 WPF 实时 XAML、Copying 使用已缓存快照纯文本；生产降级路径在取消原生 Copying 后通过独立 DataObject 发布纯文本。
