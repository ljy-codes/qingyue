# 轻阅 Windows 三阶段发布路线设计

**日期：** 2026-11-16

**目标：** 在现有 `feat/windows-client` 基础上，连续交付 0.8.1、0.9.0 和 0.10.0，解决安装可用性、升级可维护性和 Markdown/Mermaid 兼容性问题。

**已确认现状：**

- 当前安装器已修复卡在 98% 的问题，提交为 `c8df756`。
- 现有会话、阅读位置、书签和恢复草稿能力已经存在，0.9.0 不重复实现这些数据模型。
- 当前测试不是标准 xUnit/NUnit 测试，而是由 `scripts/test.ps1` 编译并执行多个独立检查程序。
- 当前 Mermaid 只渲染离线 `sequenceDiagram`，并保留源码和预算保护。
- 当前没有 GitHub Actions 工作流，也没有自动更新实现。

## 0.8.1：发布基线

### 文件关联

安装器注册一个稳定 ProgID `Qingyue.Document`，命令为：

```text
"<安装目录>\Qingyue.exe" "%1"
```

主要扩展名通过 `Software\Classes\<extension>\OpenWithProgids` 增加 `Qingyue.Document`：

```text
txt, log,
md, markdown, mdown, mkd,
json, jsonc,
yaml, yml,
ini, cfg, conf, properties, env,
xml, xaml, svg, plist, csproj,
csv, tsv
```

只写入 `OpenWithProgids` 和轻阅自己的 ProgID，不修改扩展名默认值，因此不会强制改变用户的默认程序。卸载只删除轻阅自己的 ProgID、命令和值，保留其他应用的注册内容；如果扩展名键变空，再删除空键。

### 自动化和签名准备

- GitHub Actions 在 Windows runner 上执行 .NET 10 Release 构建、现有 `scripts/test.ps1`、安装器契约测试和安装器集成测试。
- 工作流不发布正式 Release，不要求仓库内存在证书。
- `build-installer.ps1` 支持可选的 Authenticode 签名：
  - `QINGYUE_SIGN_CERT_THUMBPRINT`：从当前用户或本地计算机证书存储区按指纹选择证书。
  - `QINGYUE_SIGN_CERT_PATH`：显式 PFX 路径。
  - `QINGYUE_SIGN_CERT_PASSWORD`：PFX 密码，仅从环境变量读取。
  - `QINGYUE_SIGN_TIMESTAMP_URL`：时间戳服务地址。
- 未配置证书时输出未签名构建并明确记录 `signed=false`，不伪造签名结果。

### 版本和文档

安装器、WPF 项目、使用说明、README 和 CHANGELOG 统一描述当前构建，产物名称使用 ASCII 资产名，避免 GitHub Release 下载兼容性问题。

## 0.9.0：升级入口和状态兼容

### 自动更新

新增一个不阻塞启动的 Release 检查器：

- 默认读取 GitHub Releases API 的最新正式版本。
- 使用 `HttpClient`、短超时和取消令牌；网络失败只显示状态，不影响打开本地文件。
- 版本比较使用 `Version.TryParse`，只处理正式版本，不把预发布版误判为升级。
- 只在用户点击“检查更新”或启动后经过节流时间时检查。
- 显示版本、发布时间和 Release 页面；下载交给系统浏览器，不在应用内静默替换安装目录。
- 不把 Token、代理或下载凭据写入仓库。

### 现有状态兼容

- 保持 `WorkspaceSession`、`DocumentBookmark` 的 JSON 字段兼容。
- 只为缺失字段提供默认值，不改已有枚举值和路径。
- 增加自动更新检查时间的设置字段时，旧 `Settings.json` 仍能正常加载。
- 升级失败或网络不可用时，阅读位置、书签、草稿和会话恢复继续按现有流程工作。

## 0.10.0：Markdown 和 Mermaid

### Markdown

在现有轻量解析器上补充低风险的 GFM 常用形式：

- 自动链接 `<https://...>`、`<mailto:...>` 的可读文本。
- 参考链接定义和引用，显示链接文本而不是定义正文。
- HTML 标签继续作为文字，不加载脚本、不执行外部内容。
- 代码围栏、任务列表、表格、图片预算、源位置映射和超限降级继续保持。

### Mermaid

增加离线 `flowchart` / `graph` 的有限子集：

- 支持 `flowchart TD/LR/RL/BT` 和 `graph TD/LR/RL/BT`。
- 支持节点 `A[文字]`、`A(文字)`、`A{文字}`、`A((文字))`。
- 支持 `-->`、`---`、`-.->` 三类边和边标签 `|文字|`。
- 仅渲染安全、有限的节点和边；不支持的语句整体保留源码并显示原因。
- 新图形与时序图共用预算、源码保留、主题资源、横向滚动和 WPF 映射策略。
- 不引入 JavaScript、WebView 或网络 Mermaid runtime。

## 兼容性和风险

- 0.8.1 的文件关联在 Windows 注册表层面有系统权限要求；测试构建使用 HKCU 隔离注册表根，正式构建继续使用 HKLM。
- 自动更新只提供检查和浏览器下载入口，避免覆盖安装器导致进程、权限和回滚风险。
- Mermaid 解析器采用有限语法和资源上限；未知语法必须显示原文，不能静默丢失。
- 所有新增行为先通过独立失败测试，再实现最小代码；完成前运行现有全量脚本和针对性集成测试。
