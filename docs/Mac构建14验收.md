# Mac 构建 14 验收记录

产品版本：**0.4.0**。目标构建号：**14**。记录状态：**自动检查通过，外部实机项目保留待验收**。

本记录用于收集 Mac 0.4.0 的可复现检查结果。完整范围、延期项和外部验收条件见 [Mac 0.4.0 方案与验收](Mac0.4.0方案与验收.md)。未执行的项目保留未勾选状态，不以文档默认值代替实际结果。

## 自动检查

- [x] `bash 开发空间/scripts/test.sh`：完整单元和 UI 流程测试通过；TextSearch 1050、UIWorkflow 89、MacParity 33、AppearanceParity 77、LayoutParity 117 等均通过。
- [x] `bash 开发空间/scripts/build.sh`：生成 `交付产品/轻阅-macOS-0.4.0/轻阅.app`、SHA256 文件和 ZIP。
- [x] `bash 开发空间/scripts/check-artifact.sh`：版本、构建号、universal（x86_64 + arm64）架构、签名、校验和、四套 1024px 图标和 ZIP 内容全部通过。
- [x] `git diff --check`：无空白错误。

## 人工验收

- [ ] 在目标 Mac 上启动交付版，关于窗口显示 `0.4.0 · Mac 构建 14`。
- [ ] 打开、编辑、保存、另存为、外部修改和恢复草稿流程完成。
- [ ] Markdown 阅读、分屏、图片、表格、Mermaid、全局搜索、阅读清单、书签、批注和 HTML/Markdown 导出完成。
- [ ] 七套主题在浅色/深色外观下可切换，设置窗口在小屏和大字号下可操作。
- [ ] 图标在 Dock、Finder 和设置窗口中显示正确；16/32/128 px 关键尺寸可辨认。

## 外部验收

以下项目依赖当前机器之外的证书、硬件或系统服务，未完成前不得写成“通过”：

- Apple Silicon 原生或 universal 包的实机启动与性能验收。
- Developer ID 签名、Gatekeeper 检查和公证票据验证。
- 真实系统剪贴板、多个显示器和不同缩放比例下的交互验收。

## 结果记录

执行日期：2026-11-22。执行环境：Intel Mac，macOS 13 SDK，Swift 命令行工具；交付包按 universal 目标构建。

构建产物 SHA256（交付目录内 `SHA256SUMS.txt`）：

```text
efe05c107bb4ebc5a9635d8a1ac14d6558c2aeb6faeb6ae3fa63f2ff20fd64bb  轻阅.app/Contents/MacOS/LightReader
6e2e3be658d7a9b0427dc0ea77f227c45a619a382f8fa2c6aac4275bfcea5d19  轻阅.app/Contents/Info.plist
998884b9da9c6c4e77118dd88656c3669e90a5ce9617ba68b92aa7859697da83  轻阅.app/Contents/Resources/AppIcon.icns
be14fcc835bbeba4a709006c66d066d5597da1f97b516b5017574478937edf6a  使用说明.md
```

失败或延期项应记录复现命令、日志位置和后续负责人；不要用手工截图或历史构建结果代替本构建的检查结果。
