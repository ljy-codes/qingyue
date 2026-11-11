# Executes the shared production removal macro with a hard-coded isolated
# identity. There is deliberately no production command-line purge option.
param()
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$build = Join-Path $windowsRoot 'artifacts/installer/test'
$target = Join-Path $build '中文 安装目录'
$data = Join-Path $env:LOCALAPPDATA 'QingyueInstallerTest'
$unreg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QingyueInstallerTest'
$record = [Collections.Generic.List[object]]::new()
function Check([string]$name,[bool]$condition) {
    if(-not $condition) {throw "FAIL: $name"}
    $record.Add(@{name=$name;passed=$true});Write-Output "PASS: $name"
}
if((Get-Content -LiteralPath (Join-Path $target 'Qingyue.install') -Raw).Trim() -ne 'QingyueInstallerTest') {
    throw '测试安装身份不匹配。请先运行 test-installer.ps1。'
}
$external = Join-Path $build '清理范围之外'
New-Item -ItemType Directory -Path $external -Force | Out-Null
Set-Content -LiteralPath (Join-Path $external '外部文档.txt') -Value 'must never change'
$externalHash = (Get-FileHash -LiteralPath (Join-Path $external '外部文档.txt')).Hash
$link = Join-Path $data '外部目录联接'
New-Item -ItemType Junction -Path $link -Target $external | Out-Null
$unknown = Join-Path $target '保留的额外文件.txt'
Set-Content -LiteralPath $unknown -Value 'unknown installed folder file'
$harness = @'
Unicode true
RequestExecutionLevel user
SilentInstall silent
!include LogicLib.nsh
!include nsDialogs.nsh
!include "@SOURCE@\uninstall-actions.nsh"
!addplugindir "@BUILD@"
!define DATA_LEAF "QingyueInstallerTest"
!define PRODUCT_ID "QingyueInstallerTest"
!define REGKEY "Software\QingyueInstallerTest\Windows"
!define UNKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\QingyueInstallerTest"
!define LINK_NAME "轻阅安装器测试"
!define REMOVE_FILES "@BUILD@\remove-files.nsh"
Var PurgeChoice
Var UserDataPath
Var Result
OutFile "@BUILD@\purge-fixture.exe"
Section
SetRegView 64
SetShellVarContext current
StrCpy $INSTDIR "@TARGET@"
StrCpy $UserDataPath "$LOCALAPPDATA\QingyueInstallerTest"
StrCpy $PurgeChoice ${BST_CHECKED}
!insertmacro QingyueRemoveActions
SectionEnd
'@
$text=$harness.Replace('@SOURCE@',(Join-Path $windowsRoot 'installer')).Replace('@BUILD@',$build).Replace('@TARGET@',$target)
$script=Join-Path $build 'purge-fixture.nsi'
Set-Content -LiteralPath $script -Value $text -Encoding utf8
& "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe" /INPUTCHARSET UTF8 /V2 $script
if($LASTEXITCODE) {throw '清理测试程序编译失败。'}
$exe=Join-Path $build 'purge-fixture.exe'
$lockedPath=Join-Path $data 'Drafts/未保存草稿.txt'
$lock=[IO.File]::Open($lockedPath,'Open','ReadWrite','None')
try {
    $p=Start-Process -FilePath $exe -WindowStyle Hidden -Wait -PassThru
    Check '草稿被占用时明确报错' ($p.ExitCode -eq 4)
    Check '清理失败保留卸载入口以便重试' ((Test-Path $unreg) -and (Test-Path -LiteralPath (Join-Path $target 'Uninstall.exe')))
    Check '被占用的测试草稿仍在' (Test-Path -LiteralPath $lockedPath)
    Check '嵌套目录联接不触及外部文档' ((Get-FileHash -LiteralPath (Join-Path $external '外部文档.txt')).Hash -eq $externalHash)
} finally {$lock.Dispose()}
$readonly=Join-Path $data '只读设置.json'
Set-Content -LiteralPath $readonly -Value '{}'
[IO.File]::SetAttributes($readonly,[IO.FileAttributes]::ReadOnly)
$p=Start-Process -FilePath $exe -WindowStyle Hidden -Wait -PassThru
Check '共享生产清理流程完成彻底卸载' ($p.ExitCode -eq 0)
Check '彻底清理移除默认测试数据目录含只读文件' (-not (Test-Path -LiteralPath $data))
Check '彻底卸载移除程序及应用列表登记' (-not (Test-Path $unreg) -and -not (Test-Path -LiteralPath (Join-Path $target 'Qingyue.exe')))
Check '彻底卸载移除专用安装目录中的未知文件且不影响外部文档' (
    (-not (Test-Path -LiteralPath $unknown)) -and
    (-not (Test-Path -LiteralPath $target)) -and
    (Get-FileHash -LiteralPath (Join-Path $external '外部文档.txt')).Hash -eq $externalHash)
# The fixed data root itself must never be a junction followed by deletion.
New-Item -ItemType Junction -Path $data -Target $external | Out-Null
$rootHarness=@'
Unicode true
RequestExecutionLevel user
SilentInstall silent
!addplugindir "@BUILD@"
OutFile "@BUILD@\purge-linked-root.exe"
Section
QingyueSafety::PurgeData /NOUNLOAD "$LOCALAPPDATA\QingyueInstallerTest" "QingyueInstallerTest"
Pop $0
StrCmp $0 "" bad
SetErrorLevel 4
Goto done
bad:
SetErrorLevel 0
done:
SectionEnd
'@
Set-Content -LiteralPath (Join-Path $build 'purge-linked-root.nsi') -Value $rootHarness.Replace('@BUILD@',$build) -Encoding utf8
& "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe" /INPUTCHARSET UTF8 /V2 (Join-Path $build 'purge-linked-root.nsi')
if($LASTEXITCODE) {throw '根联接校验程序编译失败。'}
try {
    $p=Start-Process -FilePath (Join-Path $build 'purge-linked-root.exe') -WindowStyle Hidden -Wait -PassThru
    Check '拒绝将链接根目录作为清理目标' ($p.ExitCode -eq 4)
    Check '链接目标文件哈希完全保持一致' ((Get-FileHash -LiteralPath (Join-Path $external '外部文档.txt')).Hash -eq $externalHash)
} finally {
    # Delete the link object, not the linked directory tree.
    [IO.Directory]::Delete($data)
}
[ordered]@{verifiedAt=(Get-Date).ToString('o');passed=$record.Count;checks=$record;
    method='Shared production removal macro with isolated product identity; no GUI purge automation'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $build 'purge-results.json') -Encoding utf8
Write-Output "彻底清理检查通过：$($record.Count) 项。"
