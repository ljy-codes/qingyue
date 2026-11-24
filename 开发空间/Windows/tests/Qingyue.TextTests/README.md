# 文本核心行为测试

覆盖 Windows Core 与真实 WPF 阅读渲染器的文本、搜索、格式化和 Markdown 映射行为。目标框架为 .NET 10，不使用第三方测试包。

## 运行

在仓库根目录运行：

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'
dotnet run --project '开发空间/Windows/tests/Qingyue.TextTests/Qingyue.TextTests.csproj' -c Release
dotnet run --project '开发空间/Windows/tests/Qingyue.TextTests/Audit/WpfAudit.csproj' -c Release
```

当前项目引用回归结果为 `47/47 passed`；WPF 指针映射审查为 `0 failures`。完整 Windows 回归仍以 `开发空间/Windows/scripts/test.ps1` 为准。

## 现行约定

- 搜索、块和目录均使用 UTF-16 偏移，不返回切断有效代理对的搜索范围。
- Windows 搜索上限为 5,000,000 个 UTF-16 单元；查询最多 4096，匹配最多 10 万，最终替换结果最多 1000 万单元。正则使用 350 ms 匹配超时。
- 字面替换不解释 `$` 或反斜杠；正则支持数字、命名捕获和 .NET 特殊替换 token，并在展开前限制分配。
- Plain/JSON/JSONC/YAML/INI/XML 保持 identity 映射；Markdown 使用块级近似映射，不承诺任意字符级反向编辑。
- JSON 格式化保留数值原词法和重复键，JSONC 仅检查不格式化；YAML 仅检查缩进 Tab；XML 禁止 DTD/外部实体。
- Markdown 是轻量解析器，不是完整 CommonMark/GFM。段内 `<br>`、`<br/>`、`<br />` 可换行，代码跨度中的标签保持字面内容。
- 富文本超过 200,000 UTF-16 单元时降级为原文；目录最多 500 项；表格最多 32 列、1024 格，超限退回文字。

## 状态

2026-11-24 复核时无待实现接口。历史审查问题已修复并由当前回归覆盖，不再保留单独的过程草稿。
