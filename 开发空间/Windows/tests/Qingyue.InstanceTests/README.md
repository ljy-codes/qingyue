# Windows 单实例文件转发

## 使用及兼容性

```powershell
& '.\Qingyue.exe' 'D:\资料\第一份.txt' 'D:\资料\第二份 文件.txt'
```

第二次启动将全部路径交给同资料目录的窗口；无参数启动只恢复、激活窗口。
相对路径按发送进程的工作目录解析，接收端收到绝对路径。
沿用 `QINGYUE_SUPPORT_DIRECTORY` 的完整路径大写 UTF-8 SHA256 前 24 位身份，
互斥量仍为 `Local\Qingyue-{identity}`，未更改现有资料格式和版本。
管道额外带 Windows 会话 ID，匹配 `Local\` 互斥量的会话作用域。

## 调用及 IPC 约定

- `SingleInstanceChannel.PreparePaths(paths)`：整体校验、转绝对路径。最多 200 个路径，
  编码后消息最多 64 KiB。任何超限均整批拒绝，避免 `OpenPathsAsync` 的 `Take(200)` 静默截断。
- `new SingleInstanceChannel(pipeName, handler, reportFailure)`：立即创建监听端点，
  后台读取并串行调用异步 handler；App 的 handler 等待初始化和首批启动文件处理完成，
  随后通过 WPF Dispatcher 调用现有 `MainWindow.OpenPathsAsync`。
- `TryForwardAsync(pipeName, paths, connectMilliseconds: 250, cancellationToken)`：
  仅建立连接前的超时返回 `false`，完成回执返回 `true`；拒绝、断连、无效回执抛出异常。
- App 在 10 秒内重试连接，并检查互斥量是否可以接管，支持所有者退出/放弃的启动竞态。
  消息发送后不自动重试；完成回执等待最多 2 分钟，超时明确提示检查窗口再重试。
- 管道名：`Qingyue-{WindowsSessionId}-{identity}-open-v1`。
  服务端和客户端使用 `PipeOptions.CurrentUserOnly`，发送方通过操作系统获取真实服务端 PID，
  尝试授予前台激活权限。主窗口恢复最小化前的状态并激活，无法前置时闪烁任务栏。
- 每条连接仅一问一答；帧为 **4 字节小端有符号长度 + 指定长度的 UTF-8 JSON**。
  请求：`{"Version":1,"Paths":["D:\\资料\\第一份.txt"]}`；空数组表示激活。
  完成回执：`{"Version":1,"Success":true,"Error":null}`。
  失败回执：`{"Version":1,"Success":false,"Error":"失败原因"}`。
- 接收端校验版本、长度、路径数量及绝对路径。并发连接及队列均有界（各 16），
  读取不完整消息最多 5 秒；坏消息和断连不终止监听。
- 成功回执在 UI handler 完成且请求路径均出现在窗口标签后发送。
  窗口关闭、标签限额、部分文件失败等情况不会伪报成功。文件读取错误沿用窗口提示。
- `OnExit` 先取消监听、队列等待和在途管道，再释放互斥量。
  同步 `Dispose` 不阻塞 WPF；独立测试可 `await DisposeAsync()` 等待清理完成。

## 验证

不需要额外 NuGet；完整测试已接入解决方案及 `scripts/test.ps1`，独立 IPC 项目可单独运行。

在 `开发空间/Windows` 下运行：

```powershell
# 真实 App 双进程 + IPC：需要交互式 Windows 桌面。
dotnet build tests/Qingyue.InstanceTests/Qingyue.InstanceTests.csproj -m:1 -p:UseSharedCompilation=false
& ./tests/Qingyue.InstanceTests/bin/Debug/net10.0-windows/Qingyue.InstanceTests.exe

# 独立 console IPC：直接链接生产通信类，不依赖 WPF/主项目构建。
dotnet build tests/Qingyue.InstanceIpcTests/Qingyue.InstanceIpcTests.csproj -m:1 -p:UseSharedCompilation=false
& ./tests/Qingyue.InstanceIpcTests/bin/Debug/net10.0-windows/Qingyue.InstanceIpcTests.exe
```

窗口测试还支持 `--filter 第二进程`；`--ipc-only` 只执行 IPC 用例。
测试使用 GUID 临时资料目录，清理仅针对自己启动的进程和对应目录。
测试宿主的初始化阻塞、诊断窗口和异常记录均位于新测试项目，生产代码没有测试开关。

已观察的首次失败：原代码第二进程弹“已在运行”并阻塞，回归 `0/1`。
实现后用例覆盖：不同工作目录/Unicode/空格/多路径、初始化期间排队、空参数激活、
最小化及最大化恢复、六进程并发冷启动、放弃互斥量接管、资料目录隔离、
大小写身份兼容、正常退出重启，以及 8 项真实命名管道边界/错误/清理测试。

风险边界：转发队列位于内存；进程崩溃或回执丢失后，发送方会显示未确认提示，
用户应检查已打开文件后重试。通信层不保证进程崩溃后的会话持久化。
