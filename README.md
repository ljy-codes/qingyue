# 轻阅 Qingyue

轻量文本阅读与编辑器，从 macOS 起步，逐步支持 Windows 和 Linux。

A lightweight text reader and editor, starting with macOS.

当前版本 **0.1.0**。macOS 客户端使用 Swift、AppKit 与系统文本组件，完全本机读写，无账号、后台服务或第三方运行时。

## 当前功能

- TXT、Markdown、JSON/JSONC、YAML、INI/CONF/CONFIG 和 XML 文本编辑。
- 多标签、最近文件、拖入编辑区、行号、自动换行、跳转行、撤销与重做。
- 查找替换：区分大小写、正则、全词、匹配计数和高亮，支持正则捕获替换。
- Tab / Shift+Tab 整行缩进，换行继承缩进。
- JSON 检查与保留数字原始精度的格式化；JSONC 支持注释和尾逗号检查。
- Markdown 标题、列表、任务列表、删除线、代码、链接与基础表格的原生阅读。
- 明暗主题、字号、TXT 阅读字体与行距、行号设置、文件位置记忆。
- UTF-8/BOM、UTF-16 LE/BE、GB18030，保留原编码与换行；安全保存、外部修改冲突提示、本机恢复草稿。

完整操作与限制见[使用说明](操作指导/使用说明.md)。

![macOS 原生阅读与表格](docs/screenshots/macos-reading.png)

## 平台

| 平台 | 状态 |
| --- | --- |
| Intel Mac，macOS 13+ | 已在 macOS 13.7.8 验证 |
| Apple Silicon | 尚未做实机验收 |
| Windows / Linux | 后续计划，当前尚无客户端 |

## 构建与测试

安装 Xcode Command Line Tools 后，在仓库根目录运行：

```bash
bash 开发空间/scripts/build.sh
bash 开发空间/scripts/test.sh
```

应用生成到 `交付产品/轻阅.app`。编译缓存和应用产物不会提交到 Git；仓库包含完整源码、示例与测试。当前打包为 Intel macOS 13 目标、ad-hoc 本机签名，对外正式签名和公证另行配置。

## 范围与性能

优先保证轻量与可靠读写。单文件上限 50 MiB；超过 200,000 个 UTF-16 单元时暂停高亮与 Markdown 排版；搜索上限 5,000,000 单元，复杂正则有时间预算。大日志处理、完整 YAML 校验、完整 CommonMark/GFM、Office 文档、云同步与插件均不在当前范围。

Markdown 不执行脚本，不自动加载远程图片；图片显示文字占位。关闭文件不会自动写回原文，恢复草稿存放在本机应用支持目录。

当前先打磨 macOS 客户端；后续扩展平台时，再逐步分离可复用的文本规则、文件逻辑与各平台界面。
