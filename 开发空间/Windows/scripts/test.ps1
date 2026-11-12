param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$versionSource = Get-Content -LiteralPath (Join-Path $windowsRoot 'installer/version.nsh') -Raw
$expectedProductVersion = [regex]::Match($versionSource, '!define VERSION "([^"]+)"').Groups[1].Value
$expectedFileVersion = [regex]::Match($versionSource, '!define BUILD_VERSION "([^"]+)"').Groups[1].Value
$expectedWindowsBuild = [regex]::Match($versionSource, '!define WINDOWS_BUILD "([^"]+)"').Groups[1].Value
$builtExe = Join-Path $windowsRoot "src/Qingyue.Windows/bin/$Configuration/net10.0-windows/Qingyue.exe"
if (-not (Test-Path -LiteralPath $builtExe)) { throw "构建产物不存在：$builtExe" }
$builtVersion = (Get-Item -LiteralPath $builtExe).VersionInfo.FileVersion
if ($builtVersion -ne $expectedFileVersion) {
    throw "文件版本不正确：期望 $expectedFileVersion，实际 $builtVersion"
}
if (-not $expectedProductVersion -or -not $expectedWindowsBuild) {
    throw '版本配置不完整。'
}
Write-Output "版本检查通过：产品 $expectedProductVersion · Windows 构建 $expectedWindowsBuild · 文件 $builtVersion"
foreach ($testName in @('Qingyue.FileTests', 'Qingyue.TextTests', 'Qingyue.UITests', 'Qingyue.EditingTests', 'Qingyue.MarkdownTests', 'Qingyue.SequenceTests', 'Qingyue.MermaidViewTests', 'Qingyue.ReadingTests', 'Qingyue.ImageTests', 'Qingyue.ClipboardTests', 'Qingyue.InstanceTests', 'Qingyue.ExternalChangesTests', 'Qingyue.NavigationTests', 'Qingyue.RecoveryTests', 'Qingyue.ReaderExperienceTests')) {
    $framework = if ($testName -in @('Qingyue.FileTests', 'Qingyue.TextTests', 'Qingyue.SequenceTests')) { 'net10.0' } else { 'net10.0-windows' }
    $testPath = if ($testName -eq 'Qingyue.RecoveryTests') {
        Join-Path $windowsRoot "artifacts/recovery-v030/build/wpf/bin/$Configuration/$framework/$testName.dll"
    } else { Join-Path $windowsRoot "tests/$testName/bin/$Configuration/$framework/$testName.dll" }
    if ($testName -eq 'Qingyue.RecoveryTests') {
        dotnet $testPath --root (Join-Path $windowsRoot 'artifacts/recovery-v030/runs')
    } else { dotnet $testPath }
    if ($LASTEXITCODE -ne 0) { throw "$testName 失败：$LASTEXITCODE" }
}
dotnet (Join-Path $windowsRoot "tests/Qingyue.FileTests/bin/TextPositionTests/$Configuration/net10.0/TextPositionTests.dll")
if ($LASTEXITCODE -ne 0) { throw "文本位置索引测试失败：$LASTEXITCODE" }
dotnet (Join-Path $windowsRoot "tests/Qingyue.TextTests/Audit/bin/$Configuration/net10.0-windows/WpfAudit.dll")
if ($LASTEXITCODE -ne 0) { throw "阅读指针映射检查失败：$LASTEXITCODE" }
