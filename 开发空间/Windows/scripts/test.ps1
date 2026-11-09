param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
foreach ($testName in @('Qingyue.FileTests', 'Qingyue.TextTests', 'Qingyue.UITests', 'Qingyue.EditingTests', 'Qingyue.MarkdownTests')) {
    $framework = if ($testName -in @('Qingyue.UITests', 'Qingyue.EditingTests', 'Qingyue.MarkdownTests')) { 'net10.0-windows' } else { 'net10.0' }
    dotnet (Join-Path $windowsRoot "tests/$testName/bin/$Configuration/$framework/$testName.dll")
    if ($LASTEXITCODE -ne 0) { throw "$testName 失败：$LASTEXITCODE" }
}
dotnet (Join-Path $windowsRoot "tests/Qingyue.FileTests/bin/TextPositionTests/$Configuration/net10.0/TextPositionTests.dll")
if ($LASTEXITCODE -ne 0) { throw "文本位置索引测试失败：$LASTEXITCODE" }
dotnet (Join-Path $windowsRoot "tests/Qingyue.TextTests/Audit/bin/$Configuration/net10.0-windows/WpfAudit.dll")
if ($LASTEXITCODE -ne 0) { throw "阅读指针映射检查失败：$LASTEXITCODE" }
