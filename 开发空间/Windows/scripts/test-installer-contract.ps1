param()
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$installerRoot = Join-Path $windowsRoot 'installer'
$nsi = Get-Content -LiteralPath (Join-Path $installerRoot 'qingyue.nsi') -Raw
$pages = Get-Content -LiteralPath (Join-Path $installerRoot 'uninstall-pages.nsh') -Raw
$actions = Get-Content -LiteralPath (Join-Path $installerRoot 'uninstall-actions.nsh') -Raw
$safety = Get-Content -LiteralPath (Join-Path $installerRoot 'safety.cpp') -Raw
$checks = [Collections.Generic.List[object]]::new()
function Assert-Contract([string]$name, [bool]$condition) {
    if (-not $condition) { throw "FAIL: $name" }
    $checks.Add([ordered]@{name=$name;passed=$true})
    Write-Output "PASS: $name"
}

$installPages = @([regex]::Matches($nsi, '(?m)^\s*Page\s+'))
$uninstallPages = @([regex]::Matches($nsi, '(?m)^\s*UninstPage\s+'))
Assert-Contract '安装器只注册路径页和单进度页' (
    $installPages.Count -eq 2 -and
    $nsi -match '(?m)^\s*Page custom LocationPage LocationLeave\s*$' -and
    $nsi -match '(?m)^\s*Page instfiles InstallPre\s*$' -and
    $nsi -notmatch '(?m)^\s*Page custom FinishPage'
)
Assert-Contract '卸载器只注册选择页和单进度页' (
    $uninstallPages.Count -eq 2 -and
    $nsi -match '(?m)^\s*UninstPage custom un\.OptionsPage un\.OptionsLeave\s*$' -and
    $nsi -match '(?m)^\s*UninstPage instfiles un\.RemovePre\s*$' -and
    $nsi -notmatch '(?m)^\s*UninstPage custom un\.(WelcomePage|ConfirmPage|FinishPage)'
)
Assert-Contract '安装和卸载完成后自动关闭且不显示细节面板' (
    $nsi -match '(?m)^\s*AutoCloseWindow true\s*$' -and
    $nsi -match '(?m)^\s*ShowInstDetails hide\s*$' -and
    $nsi -match '(?m)^\s*ShowUninstDetails hide\s*$'
)
Assert-Contract '安装卸载不额外拦截运行中的轻阅进程' (
    $nsi -notmatch 'QingyueSafety::IsBusy' -and
    $safety -notmatch 'IsBusy|CreateToolhelp32Snapshot|Qingyue\.exe'
)
Assert-Contract '正式安装使用系统级卸载登记并请求管理员权限' (
    $nsi -match '(?m)^\s*RequestExecutionLevel \$\{EXECUTION_LEVEL\}\s*$' -and
    $nsi -match '(?m)^\s*!define REG_ROOT HKLM\s*$' -and
    $nsi -match 'WriteRegStr \$\{REG_ROOT\} "\$\{UNKEY\}" "DisplayName"'
)
Assert-Contract '卸载默认选择保留用户信息' (
    $nsi -match '(?m)^\s*StrCpy \$PurgeChoice \$\{BST_UNCHECKED\}\s*$'
)
Assert-Contract '彻底卸载调用专用安装目录清理' (
    $actions -match 'QingyueSafety::PurgeInstall' -and
    $safety -match 'extern "C" __declspec\(dllexport\) void __cdecl PurgeInstall'
)
Assert-Contract '专用安装目录清理包含安装标记和路径安全校验' (
    $safety -match 'Qingyue\.install' -and
    $safety -match 'Validate\(' -and
    $safety -match 'TreeHasLinks'
)
Assert-Contract '彻底卸载保留模式不调用用户数据清理' (
    $actions -match '\$\{If\} \$PurgeChoice == \$\{BST_CHECKED\}' -and
    $actions -match 'QingyueSafety::PurgeData'
)

[ordered]@{
    verifiedAt=(Get-Date).ToString('o')
    passed=$checks.Count
    checks=$checks
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $windowsRoot 'artifacts/installer-contract-results.json') -Encoding utf8
Write-Output "安装器契约检查完成：$($checks.Count) 项。"
