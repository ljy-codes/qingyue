# GitHub 与本地整理记录

执行日期：2026-11-23。参考仓库：<https://github.com/ljy-codes/api-workbench>。

## GitHub

- 长期分支仅 `main`、`macos`、`windows`，默认 `main`。
- Mac 最新标签和 Release 为 `v0.4.0-macos`；交付 ZIP 为 `qingyue-macos-universal-0.4.0.zip`，附件同时提供 `SHA256SUMS.txt`。
- 清除 8 个旧 Mac Release、11 个旧/过渡 Mac 标签及临时分支，历史源码继续通过 Git 提交追溯。
- `windows` 直接接续原分支 `0869803047bbff82a1c38b1bf61ee1fe53a3e60e`；Windows 的 11 个历史 Release、附件及旧标签保持不变，待独立核实后再按新规则整理。
- 平台版本与功能契约见[跨平台规则](跨平台版本与功能对齐规则.md)和[功能对照表](功能对照表.md)。

## 验证

- Mac 既有完整回归通过；此次标签迁移新增的 `UpdateServiceTests` 为 37 项通过。
- 最终包版本 0.4.0 / 构建 14，包含 x86_64 + arm64；本地产物签名、SHA-256、图标和 ZIP 解包检查通过。
- GitHub 资产的大小和服务端 SHA-256 与本地 ZIP 一致：`2a7bcbd369007563dc32d564c1ac850aef0629dad16cc92be1da853d88c376dc`。
- 当前网络从 Release 下载资产超时，未把“下载后逐字节复核”记为通过；上传完整性已用 GitHub 返回的 SHA-256 核对。
- CI 以 `docs/ci/macos.yml.template` 保留，未启用；Apple Silicon 实机、签名公证等边界仍见[构建 14 验收](Mac构建14验收.md)。

## 本地

- `/Applications/轻阅.app` 与最终交付的可执行文件一致，重新启动成功；保留用户原有正文、草稿和设置。
- 项目只保留 0.4.0 交付目录及 ZIP。删除旧 0.2.0/0.3.0 ZIP 和可重建的 `开发空间/build`，清理约 810 MB 文件。
- 本地仅保留 `main`、`macos`、`windows` 三条分支及最新 Mac 平台标签；Windows 历史标签未删除。
- 删除过时的构建 12 文档与截图，并修复 README 引用。旧主题实验独有提交已在 `.git/qingyue-maintenance` 保留可恢复 bundle，普通工作目录不保留旧副本。
