# 轻阅 · 开发空间

当前版本：**0.4.0 · Mac 构建 14（发布准备）**。目标为 Intel 与 Apple Silicon Mac / macOS 13+，使用 Swift、AppKit 和系统文本组件，无第三方运行时。

## 目录

- `Sources/`：Mac 界面、文件读写、编辑、搜索、阅读、主题、工作区与批注。
- `Resources/`：Info.plist、四款图标资源。
- `Samples/`：TXT / Markdown / JSON / YAML / 配置示例。
- `Tests/`：文件、文本、阅读、搜索、编辑、主题、布局、窗口与工作区流程测试。
- `scripts/build.sh`：编译、生成图标、签名、打包交付目录和 ZIP。
- `scripts/test.sh`：完整回归测试入口。
- `scripts/check-artifact.sh`：复核交付包版本、架构、签名、校验和与 ZIP 内容。
- `Windows/`：独立 WPF 客户端源码与 Windows 构建脚本，需在 Windows 环境构建和验收。

## 构建

```bash
bash scripts/build.sh
```

输出：`../交付产品/轻阅-macOS-0.4.0/轻阅.app`、`SHA256SUMS.txt` 和 `轻阅-macOS-0.4.0-交付.zip`。构建缓存写入 `build/`，不提交到 Git。

默认目标为 universal（x86_64 + arm64）。可用 `QINGYUE_ARCH=x86_64` 或 `QINGYUE_ARCH=arm64` 选择单架构构建；`QINGYUE_MACOS_TARGET` 可显式提高最低系统版本，默认 macOS 13.0。

## 测试

```bash
bash scripts/test.sh
bash scripts/check-artifact.sh
```

覆盖文件核心、文本格式、阅读模型、行索引、搜索、编辑、主题对比度、锚点、书签、批注、工作区、全局搜索、Markdown、Mermaid、富阅读、窗口菜单、布局和完整 UI 流程。

## 约定

- 文件写回必须由用户明确保存；草稿只用于本机恢复，不自动覆盖原文件。
- 阅读与编辑共用主题 token，但阅读布局与编辑布局分别处理。
- 主题变更只影响显示，不改变编码、换行、保存策略或正文。
- 新增 Markdown 块、图片、图表和交互时，应同时补源文/显示范围、资源预算和错误降级测试。
