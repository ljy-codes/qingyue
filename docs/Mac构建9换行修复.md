# Mac 构建 9：Markdown 换行修复

日期：2026-11-21。产品版本保持 **0.1.0**，Mac 构建编号为 **9**。修复用户反馈的 Markdown 表格内 `<br>` 被显示为字面文字的问题。

## 修复行为

- 表格、普通正文、标题、强调和链接文字中的 `<br>`、`<br/>`、`<br />` 以及大小写变体显示为实际换行。
- 使用 AppKit 段落内行分隔，保留同一 `NSTextTableBlock` 和同一单元格，避免增加表格行列或段落间距。
- 代码围栏、缩进代码、内联代码及转义标签保留字面内容；其他 HTML 仍当作文本，不执行 HTML 或脚本。
- HTML 复制/导出产生真正的 `<br>`；纯文本复制产生普通换行；系统 RTF/RTFD 保留原生行分隔。源文复制和磁盘 Markdown 保留原始标签。
- 保留块级源范围、目录跳转、表格对齐、选区复制范围和后续正文。

## 验证

| 检查 | 通过数 |
| --- | ---: |
| MarkdownBreakTests | 58 |
| TextFeatureTests | 98 |
| ReadingDocumentTests | 331 |
| MarkdownLinksTests | 5 |
| MarkdownLinkRenderingTests | 41 |
| RichReadingTests | 93 |

合计 626 项相关检查通过。换行专项包含实际 TextKit 字形行位置验证、原生表格行列/段落身份、中文和 emoji、RTF 经系统读取、HTML/纯文本/局部选区复制以及源范围映射。

构建通过 `开发空间/scripts/build.sh` 生成 Intel macOS 13+、ad-hoc 签名应用；包内版本为 0.1.0 / 9。未对 Windows WPF 工程和本机用户原文作修改。
