# 轻阅 · 开发空间

0.1.0 本机体验原型。目标为 Intel macOS 13+，使用 Swift 5.8、AppKit 与系统文本组件，无第三方运行时依赖。安装后运行不要求用户安装 Swift、Node.js 或 Rust。

## 目录

- `Sources/`：原生应用与文件、文本功能实现。
- `Resources/Info.plist`：应用元数据与文本文件关联。
- `Tests/`：文件往返、冲突、编码及文本格式化等独立测试。
- `Samples/`：体验用的 Markdown、JSON、YAML 和配置文件。
- `scripts/build.sh`：优化编译、图标生成、本机签名与应用打包。
- `build/`：构建缓存和中间产物。

## 构建

需要 Xcode Command Line Tools。终端进入本目录后运行：

```bash
bash scripts/build.sh
```

产物为上一级 `交付产品/轻阅.app`。脚本以 `x86_64-apple-macosx13.0` 为目标，开启优化，并将模块缓存限制在本地 `build/ModuleCache` 中。图标由构建时 Swift/AppKit 脚本绘制，不引入运行时依赖。

脚本执行本机 ad-hoc 签名及 `codesign --verify --deep --strict` 验证，不代表 Developer ID 签名或 Apple 公证。产物不自动安装到 Applications，也不会修改系统默认文件打开方式。文件关联声明为 `Alternate`。

## 核心测试

完整运行全部测试：`bash scripts/test.sh`。也可以分别编译核心模块：

```bash
mkdir -p build/ModuleCache
xcrun swiftc -module-cache-path build/ModuleCache Sources/TextFile.swift Tests/FileCoreTests.swift -o build/FileCoreTests
build/FileCoreTests
xcrun swiftc -module-cache-path build/ModuleCache -framework AppKit Sources/TextFeatures.swift Tests/TextFeatureTests.swift -o build/TextFeatureTests
build/TextFeatureTests
```

测试范围以实际测试文件为准。核心检查应包括 UTF-8/BOM、UTF-16、中文编码、CRLF、混合换行、外部修改、符号链接、保存失败、草稿恢复，以及 JSON 格式化保留大整数。

## 界面与资源验收

在实际 Mac 上验证：打开、编辑、保存、另存为、拖放、多标签切换、未保存关闭提示、中文输入法、查找替换、主题、换行与阅读模式。

资源记录需注明文件大小、标签数量和测量状态：安装体积、启动时间、空闲 CPU/内存、编辑峰值、关闭标签后的回收。统计应用及相关子进程。未经过实测的数据不能作为性能承诺。

单文件上限为 `50 * 1024 * 1024` 字节。编辑文字超过 200,000 个 UTF-16 单元时，界面暂停高亮与实时诊断；Markdown 阅读也降级为纯文本。JSON/JSONC 诊断由源码解析器实现，JSONC 接受注释和尾逗号；只允许标准 JSON 格式化。YAML 仅检查缩进 Tab，不含完整语法校验。

Markdown 当前为原生轻量阅读视图：基本标题、列表、任务、引用、代码、文字链接、删除线、分隔线与基础表格；图片显示文字占位，复杂嵌套仍有限。HTML 不执行，无 WebView 或自动联网取图。文件读写、冲突与草稿行为以源码和测试为准。
