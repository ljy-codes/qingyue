# 轻阅 · 开发空间

产品版本保持 **0.1.0**，本轮 **Mac 构建 2**。当前目标为 Intel macOS 13+，使用 Swift 5.8、AppKit 与系统文本组件，无第三方运行时。运行时不要求另装 Swift、Node.js 或 Rust。

本机只维护 Mac 客户端。Windows 的独立实现要求记录在上一级 `docs/Windows后续实现.md`，没有 Windows 工程、客户端或安装包。

## 目录

- `Sources/`：Mac 界面、文件读写、编辑、搜索与阅读模型。
- `Sources/ReadingDocument.swift`：阅读内容、源文/显示块范围对应和目录。
- `Resources/Info.plist`：版本、构建编号、应用与文本关联元数据。
- `Tests/`：文件往返、冲突、Unicode、搜索、编辑操作与阅读映射测试。
- `Samples/`：Markdown、JSON、YAML 和配置示例。
- `scripts/build.sh`：优化编译、图标生成、本机签名与应用打包。
- `scripts/test.sh`：独立测试入口；不等同于全部界面流程或性能验收。
- `build/`：本地模块缓存和中间产物，不提交到 Git。

## 构建

需要 Xcode Command Line Tools。终端进入本目录后运行：

```bash
bash scripts/build.sh
```

产品为上一级 `交付产品/轻阅.app`。脚本读取 Info.plist 中的 `CFBundleShortVersionString=0.1.0` 和 `CFBundleVersion=2`，以 `x86_64-apple-macosx13.0` 为目标优化编译，模块缓存写入 `build/ModuleCache`。图标由构建时 Swift/AppKit 脚本绘制。

脚本执行 ad-hoc 签名和 `codesign --verify --deep --strict` 验证，不等同于 Developer ID、公证或 Apple Silicon 验收，也不自动替换 `/Applications` 中的已安装副本。文件关联仍使用 `Alternate`，不抢系统默认编辑器。

产品版本与构建编号分别管理。旧 `v0.1.0` 预发布保留，本轮使用 `macos-build-2`；发布包必须对应实际提交与验收结果，不能用相同文件名悄悄替换历史内容。

## 测试

```bash
bash scripts/test.sh
```

测试脚本分别编译运行文件核心、格式、阅读模型、行索引、搜索和编辑操作。阅读模型测试编译 `TextFeatures.swift + ReadingDocument.swift + ReadingDocumentTests.swift`，不需要运行主应用。

源文和显示范围均需明确 UTF-16 单位。Markdown 块级映射不等同于任意字符级精准映射；新增渲染块要有原文范围、显示范围、中文/Emoji、首末位置与降级路径测试。不同字体或排版不能改变原文语义。

文件测试至少覆盖编码/BOM、CRLF、混合换行、外部修改、ACL/扩展属性、软硬链接、保存失败及恢复。特别验证：恢复后或原始字节未缓存的 mixed 文件，明确覆盖时也不能静默转为 LF；没有足够原始信息时要求显式转换。

界面流程测试和人工验收应另行记录，尤其包括阅读内查找/目录、Esc、模式切换定位、标签快捷键、红叉与退出分离、窗口位置、自动类型识别、中文输入法候选和键盘焦点。断言数量不代表用户流程覆盖率。

本轮菜单约定：专注 `⌘⇧F`，文档目录 `⌘⇧O`，上下标签 `⌘⇧[` / `⌘⇧]`，`⌘1`–`⌘8` 选择对应标签、`⌘9` 选择最后标签。红叉完成文档关闭后清空标签，但进程存活；Dock 重开建立空白文档。`⌘W` 关闭最后标签时也关闭窗口，不自动补空白标签。

设置提供系统外观跟随、10–30 字号、正文阅读字体与行距，以及紧凑/标准/宽/全宽版心。配置阅读强制等宽；版式变化按源文锚点尝试恢复，而不是依赖固定像素 Y。

## 阅读和资源边界

TXT/Markdown 使用正文阅读布局；JSON/YAML/INI/XML 等保留源文、等宽与适用高亮，阅读视图不自动写回或格式化配置。标准 JSON 可手动格式化，JSONC 只做语法检查，YAML 仍只有缩进 Tab 基础提示。

Markdown 使用原生 TextKit，支持基本标题、列表、任务、引用、代码、链接、删除线、分隔线和基础表格。表格限制 32 列/1024 单元格，图片占位；HTML 不执行，无 WebView 或自动联网取图。

单文件上限 50 MiB；超过 200,000 UTF-16 单元时暂停高亮和 Markdown 排版；搜索上限 5,000,000 单元，复杂正则有时间预算。使用统一指标记录首屏、输入延迟、搜索取消、草稿写入、峰值和关闭后的回收，不能以历史空闲快照当作本轮性能保证。

会话恢复、多窗口、文件夹搜索、完整 Markdown、完整 YAML 和本地图片仍为后续工作。各平台的文件权限、原子替换、输入法、正则和安装行为需独立实现与验收。
