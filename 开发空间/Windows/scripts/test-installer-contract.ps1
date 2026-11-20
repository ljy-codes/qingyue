param()
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$installerRoot = Join-Path $windowsRoot 'installer'
$nsi = Get-Content -LiteralPath (Join-Path $installerRoot 'qingyue.nsi') -Raw
$pages = Get-Content -LiteralPath (Join-Path $installerRoot 'uninstall-pages.nsh') -Raw
$actions = Get-Content -LiteralPath (Join-Path $installerRoot 'uninstall-actions.nsh') -Raw
$safety = Get-Content -LiteralPath (Join-Path $installerRoot 'safety.cpp') -Raw
$buildScript = Get-Content -LiteralPath (Join-Path $windowsRoot 'scripts/build-installer.ps1') -Raw
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
Assert-Contract '文件关联使用稳定 ProgID 和 OpenWithProgids' (
    $nsi -match '(?m)^\s*!define ASSOCIATION_PROGID "Qingyue\.Document"\s*$' -and
    $nsi -match '(?m)^\s*!define ASSOCIATION_ROOT "Software\\Classes"\s*$' -and
    $nsi -match 'ASSOCIATION_ROOT\}\\\$\{ASSOCIATION_PROGID\}' -and
    $nsi -match 'OpenWithProgids' -and
    $nsi -match '(?m)^\s*!insertmacro QingyueRegisterAssociation txt\s*$' -and
    $nsi -match '(?m)^\s*!insertmacro QingyueRegisterAssociation md\s*$' -and
    $nsi -match '(?m)^\s*!insertmacro QingyueRegisterAssociation json\s*$' -and
    $nsi -match '(?m)^\s*!insertmacro QingyueRegisterAssociation yaml\s*$' -and
    $nsi -match '(?m)^\s*!insertmacro QingyueRegisterAssociation xml\s*$'
)
Assert-Contract '文件关联命令行安全引用路径和参数' (
    $nsi -match 'ASSOCIATION_PROGID\}\\shell\\open\\command".*Qingyue\.exe" "%1"'
)
Assert-Contract '卸载只删除轻阅拥有的文件关联值' (
    $actions -match 'QingyueUnregisterAssociation' -and
    $nsi -match 'DeleteRegValue \$\{REG_ROOT\} "\$\{ASSOCIATION_ROOT\}\\\.\$\{EXT\}\\OpenWithProgids" "\$\{ASSOCIATION_PROGID\}"' -and
    $actions -match 'DeleteRegKey \$\{REG_ROOT\} "\$\{ASSOCIATION_ROOT\}\\\$\{ASSOCIATION_PROGID\}"'
)
Assert-Contract '安装器支持可选签名且不把证书机密写入仓库' (
    $buildScript -match '\[switch\]\$Sign' -and
    $buildScript -match '\$CertificatePath' -and
    $buildScript -match '\$CertificateThumbprint' -and
    $buildScript -match '\$CertificatePassword' -and
    $buildScript -match '\$TimestampUrl' -and
    $buildScript -match 'Get-AuthenticodeSignature' -and
    $buildScript -match '\$signed = \$false' -and
    $buildScript -notmatch 'BEGIN (RSA )?PRIVATE KEY' -and
    $buildScript -notmatch 'QINGYUE_SIGN_CERT_PASSWORD\s*=\s*["''][^$]'
)
Assert-Contract '系统登记错误检查不被旧用户登记清理污染' (
    $nsi -match '(?s)WriteRegDWORD \$\{REG_ROOT\} "\$\{UNKEY\}" "EstimatedSize".*?IfErrors installRegistryFailed.*?DeleteRegKey HKCU "\$\{UNKEY\}"' -and
    $nsi -notmatch '(?s)DeleteRegKey HKCU "\$\{UNKEY\}".*?IfErrors installRegistryFailed'
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
