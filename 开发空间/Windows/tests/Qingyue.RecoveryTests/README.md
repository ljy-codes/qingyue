# Windows 0.3.0 草稿与退出安全回归

本项目无第三方依赖，直接编译生产源码，使用真实 WorkspaceStore、DocumentFile、MainWindow、
WPF 控件和 Dispatcher。不会调用真实 App 的启动入口、单实例通信、系统剪贴板或安装程序。
每次运行在 `--root` 内创建独立 GUID 子目录，测试资料保留供审查。
输出固定隔离于 ignored `artifacts/recovery-v030/build/core|wpf`，不占用主项目的 bin/obj。

## 主控集成

在 `MainWindow.InitializeAsync()` 的 `if (initialized) return;` 后、任何会话加载和标签打开前：

```csharp
await PrepareRecoveryAsync();
```

该调用与 `restoreSession`、`settings.RestoreSession`、`settings.RecoveryEnabled` 无关。
准备失败应中止本次初始化，不应跳过准备后继续自动保存。`PersistWorkspaceAsync` 也有防御性准备，
准备失败返回错误而不写入会话。准备幂等，关闭会话恢复时只保留旧草稿，不自动打开旧标签。

在 `RenameTab` 内，必须先成功完成磁盘改名，再迁移排除路径：

```csharp
var old = tab.File.Path;
tab.File.Rename(name); // 抛异常时不会调用下面的方法
OnRecoveryPathRenamed(old, tab.File.Path);
```

最终接口：

```csharp
// MainWindow
public Task PrepareRecoveryAsync();
public void OnRecoveryPathRenamed(string? oldPath, string? newPath);

// WorkspaceStore
public Task PrepareRecoveryAsync();
public Task<WorkspaceSaveResult> SaveAsync(
    WorkspaceSession session, IReadOnlyList<DocumentSnapshot> drafts,
    IReadOnlyCollection<Guid> openedDocumentIds);
```

原来的双参数 `SaveAsync` 保留。存储层只记录原草稿文件名，不为保留旧草稿加载全部正文；
已打开 ID 在成功提交会话后解除保护，包括首次自动保存前就被明确关闭的标签。
未打开草稿不并入当前会话，不占用会话的 200 标签配额；仍能在后续启动从孤立草稿恢复。
明确“清除所有草稿”会清除该保护。关闭全局恢复不会隐式删除未打开旧草稿。

`PersistWorkspaceAsync` 调用主控的 `CaptureWorkspaceEntry(EditorTab)`，保留其视口属性；
在结果上应用 `CanRecover`，并保持原来的 LF 源文选区坐标。
不新增同名捕获方法、不修改 ReaderSettings 或版本文件。

## 执行命令

工作目录：`D:\专用工具\阅读器\qingyue\开发空间\Windows`。

```powershell
dotnet build tests/Qingyue.RecoveryTests/Qingyue.RecoveryTests.csproj -c Release -p:RecoveryCoreOnly=true --disable-build-servers -m:1 -nr:false
dotnet artifacts/recovery-v030/build/core/bin/Release/net10.0/Qingyue.RecoveryTests.dll --root artifacts/recovery-v030/runs

dotnet build tests/Qingyue.RecoveryTests/Qingyue.RecoveryTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet artifacts/recovery-v030/build/wpf/bin/Release/net10.0-windows/Qingyue.RecoveryTests.dll --root artifacts/recovery-v030/runs

dotnet build tests/Qingyue.FileTests/Qingyue.FileTests.csproj -c Release --artifacts-path artifacts/recovery-v030/legacy --disable-build-servers -m:1 -nr:false
dotnet artifacts/recovery-v030/legacy/bin/Qingyue.FileTests/release/Qingyue.FileTests.dll
```

核心模式运行 8 个场景，完整模式运行核心加 WPF 集成共 19 个场景，退出码 0 表示全通过。
既有文件安全回归运行 25 个场景，其所有资料也位于独立临时目录。
WPF 测试使用自己的隐藏任务栏窗口，自动应答测试窗口的合成对话框。
沙箱若拒绝 Windows `File.Replace`，应在批准的隔离目录运行，不能将“访问被拒绝”算作缺陷红灯。

主控并行落盘时可使用 `-p:RecoveryWindowsSource=<快照目录> -p:RecoveryBuildFlavor=baseline`
验证既有 Windows 源码。该属性只改变测试编译输入，不改生产文件。
基线来源与 SHA256 记录在 ignored `artifacts/recovery-v030/baseline-provenance.json`。

## 场景与断言

| 组别 | 验证 |
| --- | --- |
| 未打开草稿 | 空会话自动保存、新会话写入、全局关闭恢复、重复准备均保留旧内容及原字节；明确清除不复活 |
| 打开后关闭 | 已打开后明确放弃只删除对应草稿；首次自动保存前关闭恢复标签也不会将其当作未打开稿保留 |
| 退出等待 | 持有真实 WorkspaceStore 信号量，保证处于 await 保存阶段；编辑器只读，编码/换行/标签操作禁用；WPF TextInput 无法改变正文 |
| 退出失败/取消 | 偏好文件独占冲突使保存失败；恢复原有可编辑/只读状态、界面可用性和自动保存；清除本次放弃标记后可以重试 |
| 最终关闭取消 | 后续 Closing 处理器取消最终关闭时，窗口恢复可编辑状态，不滞留 approvedShutdown |
| 改名中断 | 确定性构造“新路径草稿已提交、旧路径 Session 仍在”的状态，恢复为脏的无路径副本，保留 ID、正文、建议名称 |
| 原文件安全 | 无路径副本无目标保存失败；再次保存/恢复不获原路径写入权；两条原路径的基线字节均不改写 |
| 异常回归 | 可捕获会话写入失败回滚旧稿；真正损坏快照仍隔离；明确关闭恢复的记录不能从错配稿复活 |
| 路径排除 | 成功改名持久化新路径排除；同名冲突失败不迁移；大小写改名去重；null 参数不移除原排除 |
| 排除与清理失败 | 文件句柄阻止删除时，会话仍禁用全局或路径排除稿，不恢复遗留正文 |
| 选区兼容 | CRLF 编辑器内的 beta 选区保存为 LF 源文 start=6、length=4 |

这套测试没有执行真实断电或终止用户进程，也没有覆盖物理键盘和中文 IME 候选提交。
中断验证是持久化检查点的确定性模拟；输入验证走 WPF TextBox 的真实 TextInput 处理。
