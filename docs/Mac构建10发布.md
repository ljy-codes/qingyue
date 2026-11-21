# Mac 构建 10 发布与完整重装

日期：2026-11-21。产品版本为 **0.1.0**，Mac 构建编号为 **10**。本次基于构建 9 的已验证功能重新打包发布，没有新增功能；保留 Markdown 表格 `<br>` 换行、图文复制、主题及此前功能。

## 构建和发布

- 通过 `开发空间/scripts/build.sh` 构建 Intel x86_64、macOS 13+ 应用，包内 `CFBundleShortVersionString=0.1.0`、`CFBundleVersion=10`。
- ad-hoc 签名通过 `codesign --verify --deep --strict` 验证。
- 本轮重新编译运行 `MarkdownBreakTests`：58 项通过，验证段内换行、表格身份、代码文字保护、实际 TextKit 排版和复制/导出。
- 交付 ZIP：`qingyue-macos-x64-v0.1.0-build10.zip`。发布标识为 `macos-build-10`，历史发布保留。

## 本机完整重装验证

旧应用已退出。应用包、应用支持数据和偏好设置先归档到本地、被 Git 忽略的交付目录；该备份不上传到 GitHub。

清理范围为 `/Applications/轻阅.app`、`~/Library/Application Support/LightReader`、`local.xiaoyu.LightReader` 偏好域及实际存在的专属缓存/状态。先用 `defaults delete` 清理偏好域，避免只删 plist 后被系统缓存恢复。缓存、保存状态、HTTPStorage、WebKit、容器及日志路径经过检查，本轮没有发现相应目录。

清理后安装新应用，签名和可执行文件与构建产物一致。真实启动验证：

- 从 `/Applications/轻阅.app` 启动，版本为 0.1.0 / 10。
- 主窗口为“新文件 1.txt”，编辑区内容为空。
- 最近文件和位置记录为空，没有恢复旧工作区。
- 旧书签、批注、阅读清单和旧恢复快照已从运行数据目录移除。

原始 TXT、Markdown、配置文件、文档旁 `.assets` 和 Git 工作区不在卸载清理范围内。开发/验收用的独立偏好域和构建产物不属于当前安装的运行数据。

当前包仍是 Intel macOS 13+ 本机签名版本，未新增 Developer ID 公证或 Apple Silicon 实机验收。
