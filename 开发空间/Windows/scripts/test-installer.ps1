param([switch]$SkipBuild, [switch]$NoGui)
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$repo = (Resolve-Path (Join-Path $windowsRoot '../..')).Path
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build-installer.ps1') -TestBuild }
$build = Join-Path $windowsRoot 'artifacts/installer/test'
$setup = Join-Path $build 'Qingyue-installer-test.exe'
$target = Join-Path $build '中文 安装目录'
$data = Join-Path $env:LOCALAPPDATA 'QingyueInstallerTest'
$reg = 'HKCU:\Software\QingyueInstallerTest\Windows'
$unreg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QingyueInstallerTest'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) '轻阅安装器测试.lnk'
$menuLink = Join-Path ([Environment]::GetFolderPath('Programs')) '轻阅安装器测试.lnk'
$results = [Collections.Generic.List[object]]::new()
function Assert-Check([string]$name,[bool]$condition) {
    if (-not $condition) { throw "FAIL: $name" }
    $results.Add([ordered]@{name=$name;passed=$true})
    Write-Output "PASS: $name"
}
function Invoke-Setup([string]$destination) {
    $p = Start-Process -FilePath $setup -ArgumentList "/S /D=$destination" -WindowStyle Hidden -Wait -PassThru
    return $p.ExitCode
}
function Invoke-Removal {
    # Run a test copy with NSIS's documented final _?= argument, so Wait observes
    # the real exit code and the installed uninstaller itself can be removed.
    $copy = Join-Path $build 'test-uninstaller.exe'
    Copy-Item -LiteralPath (Join-Path $target 'Uninstall.exe') -Destination $copy -Force
    $p = Start-Process -FilePath $copy -ArgumentList "/S _?=$target" -WindowStyle Hidden -Wait -PassThru
    return $p.ExitCode
}
function Get-ProfileDigest([string]$root) {
    if (-not (Test-Path -LiteralPath $root)) { return 'ABSENT' }
    return (@(Get-ChildItem -LiteralPath $root -Recurse -File |
        Sort-Object FullName | ForEach-Object {
            [IO.Path]::GetRelativePath($root,$_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }) -join "`n")
}
if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $data) -or
    (Test-Path $reg) -or (Test-Path $unreg) -or
    (Test-Path -LiteralPath $desktopLink) -or (Test-Path -LiteralPath $menuLink)) {
    throw '发现已有同名测试数据或安装，已停止，避免覆盖。请先检查测试状态。'
}
$realProfile = Join-Path $env:LOCALAPPDATA 'Qingyue'
$realDigest = Get-ProfileDigest $realProfile
# Dangerous destinations are tested in a validation-only executable: its only
# file output is a fixed result in the workspace, and it contains no install,
# registry, shortcut, extraction or deletion operations.
$validationScript = @'
Unicode true
RequestExecutionLevel user
SilentInstall silent
!addplugindir "@BUILD@"
OutFile "@BUILD@\validate-only.exe"
Section
QingyueSafety::RequestedDirectory /NOUNLOAD
Pop $0
QingyueSafety::ValidateRoot /NOUNLOAD "$0" "$LOCALAPPDATA\QingyueInstallerTest"
Pop $1
StrCmp $1 "" allowed
SetErrorLevel 2
Goto done
allowed:
SetErrorLevel 0
done:
SectionEnd
'@
$validationPath = Join-Path $build 'validate-only.nsi'
Set-Content -LiteralPath $validationPath -Value $validationScript.Replace('@BUILD@',$build) -Encoding utf8
& "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe" /INPUTCHARSET UTF8 /V2 $validationPath
if ($LASTEXITCODE) { throw '只读校验程序编译失败。' }
$validation = Start-Process -FilePath (Join-Path $build 'validate-only.exe') -ArgumentList '/D=C:\' -WindowStyle Hidden -Wait -PassThru
Assert-Check '只读校验：解析请求路径并拒绝磁盘根目录' ($validation.ExitCode -eq 2)
Assert-Check '拒绝用户数据目录作为安装位置' ((Invoke-Setup $data) -eq 2)
$foreign = Join-Path $build '其他应用'
New-Item -ItemType Directory -Path $foreign -Force | Out-Null
Set-Content -LiteralPath (Join-Path $foreign '不得改动.txt') -Value 'unrelated'
Assert-Check '拒绝已有其他文件的非轻阅目录' ((Invoke-Setup $foreign) -eq 2)
$junction = Join-Path $build '链接安装目录'
New-Item -ItemType Junction -Path $junction -Target $foreign | Out-Null
Assert-Check '拒绝目录联接，目标文件不受影响' (((Invoke-Setup $junction) -eq 2) -and
    (Get-Content -LiteralPath (Join-Path $foreign '不得改动.txt') -Raw).Trim() -eq 'unrelated')
# Remove the link itself only; never recursively delete its target.
[IO.Directory]::Delete($junction)
New-Item -ItemType Directory -Path $target -Force | Out-Null
Assert-Check '中文和空格自定义路径安装成功' ((Invoke-Setup $target) -eq 0)
$fixture = @(Get-ChildItem -LiteralPath (Join-Path $build 'payload') -Recurse -File)
$equal = $true
foreach ($file in $fixture) {
    $relative = [IO.Path]::GetRelativePath((Join-Path $build 'payload'),$file.FullName)
    $installed = Join-Path $target $relative
    if (-not (Test-Path -LiteralPath $installed) -or
        (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { $equal=$false }
}
Assert-Check '安装文件逐个哈希一致' $equal
Assert-Check '存在卸载程序和归属标记' ((Test-Path -LiteralPath (Join-Path $target 'Uninstall.exe')) -and
    (Test-Path -LiteralPath (Join-Path $target 'Qingyue.install')))
Assert-Check '系统应用列表登记安装位置与版本' ((Get-ItemProperty $unreg).InstallLocation -eq $target)
$shell = New-Object -ComObject WScript.Shell
Assert-Check '桌面和开始菜单快捷方式指向正确位置' (
    $shell.CreateShortcut($desktopLink).TargetPath -eq (Join-Path $target 'Qingyue.exe') -and
    $shell.CreateShortcut($menuLink).TargetPath -eq (Join-Path $target 'Qingyue.exe'))

New-Item -ItemType Directory -Path (Join-Path $data 'Drafts') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $data 'Drafts/未保存草稿.txt') -Value '测试草稿，不是真实用户资料'
Set-Content -LiteralPath (Join-Path $data 'Preferences.json') -Value '{"theme":"Dark"}'
$digest = Get-ProfileDigest $data
Set-Content -LiteralPath (Join-Path $target '用户自己添加的文件.txt') -Value 'must survive'
Assert-Check '同版本覆盖安装成功' ((Invoke-Setup $target) -eq 0)
Assert-Check '覆盖安装保留草稿和设置' ((Get-ProfileDigest $data) -eq $digest)
Assert-Check '覆盖安装保留未知文件' (Test-Path -LiteralPath (Join-Path $target '用户自己添加的文件.txt'))
Set-ItemProperty -Path $reg -Name BuildVersion -Value '99.0.0.0'
Assert-Check '拒绝降级' ((Invoke-Setup $target) -eq 2)
Set-ItemProperty -Path $reg -Name BuildVersion -Value (Get-Item -LiteralPath $setup).VersionInfo.FileVersion
Assert-Check '已有安装时拒绝另一个目录，避免遗留旧安装' ((Invoke-Setup (Join-Path $build '其他位置')) -eq 2)
$locked = [IO.File]::Open((Join-Path $target 'Qingyue.dll'),'Open','ReadWrite','None')
try {
    Assert-Check '文件被独占时阻止覆盖' ((Invoke-Setup $target) -eq 5)
    Assert-Check '文件被独占时阻止卸载' ((Invoke-Removal) -eq 5)
    Assert-Check '占用失败仍保留卸载登记和数据' ((Test-Path $unreg) -and (Get-ProfileDigest $data) -eq $digest)
} finally { $locked.Dispose() }
$app = Join-Path $repo '交付产品/轻阅-Windows-x64/Qingyue.exe'
$stopFile = $null
if ($NoGui) {
    # Exercise production process-name detection without opening WPF or touching
    # any real profile. This silent sentinel exits on our isolated signal file,
    # or automatically after 60 seconds if the test is interrupted.
    $busyRoot = Join-Path $build ('QingyueInstallerTest/busy-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $busyRoot -Force | Out-Null
    $stopFile = Join-Path $busyRoot 'stop'
    $app = Join-Path $busyRoot 'Qingyue.exe'
    $sentinel = @'
Unicode true
RequestExecutionLevel user
SilentInstall silent
OutFile "@APP@"
Section
StrCpy $0 0
wait:
IfFileExists "@STOP@" done
Sleep 50
IntOp $0 $0 + 1
IntCmp $0 1200 done wait done
done:
SectionEnd
'@
    $sentinelScript = Join-Path $busyRoot 'busy-sentinel.nsi'
    Set-Content -LiteralPath $sentinelScript -Value $sentinel.Replace('@APP@',$app).Replace('@STOP@',$stopFile) -Encoding utf8
    & "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe" /INPUTCHARSET UTF8 /V2 $sentinelScript
    if ($LASTEXITCODE) { throw '无窗口占用检测测试程序编译失败。' }
}
$savedSupport = $env:QINGYUE_SUPPORT_DIRECTORY
try {
    $env:QINGYUE_SUPPORT_DIRECTORY = Join-Path $build 'busy-profile'
    $running = Start-Process -FilePath $app -WindowStyle Hidden -PassThru
    if (-not $NoGui -and -not $running.WaitForInputIdle(10000)) { throw '轻阅测试进程未就绪。' }
    Assert-Check '运行中的轻阅阻止覆盖安装' ((Invoke-Setup $target) -eq 3)
    Assert-Check '运行中的轻阅阻止卸载' ((Invoke-Removal) -eq 3)
} finally {
    $env:QINGYUE_SUPPORT_DIRECTORY=$savedSupport
    if ($running -and -not $running.HasExited) {
        if ($NoGui) { Set-Content -LiteralPath $stopFile -Value 'exit' }
        else { $null = $running.CloseMainWindow() }
        if (-not $running.WaitForExit(10000)) { throw '测试轻阅窗口未正常退出，请检查。' }
    }
}
Assert-Check '默认静默卸载成功' ((Invoke-Removal) -eq 0)
Assert-Check '卸载默认保留所有用户信息' ((Get-ProfileDigest $data) -eq $digest)
Assert-Check '卸载只删除清单文件，保留用户新增文件' (
    -not (Test-Path -LiteralPath (Join-Path $target 'Qingyue.exe')) -and
    (Get-Content -LiteralPath (Join-Path $target '用户自己添加的文件.txt') -Raw).Trim() -eq 'must survive')
Assert-Check '卸载移除快捷方式、注册和归属标记' (
    -not (Test-Path $reg) -and -not (Test-Path $unreg) -and
    -not (Test-Path -LiteralPath $desktopLink) -and -not (Test-Path -LiteralPath $menuLink) -and
    -not (Test-Path -LiteralPath (Join-Path $target 'Uninstall.exe')) -and
    -not (Test-Path -LiteralPath (Join-Path $target 'Qingyue.install')))
Assert-Check '真实轻阅用户数据逐个哈希保持一致' ((Get-ProfileDigest $realProfile) -eq $realDigest)
# Leave an installed, isolated fixture for the visual purge/cancel verification.
# The unknown-file test above intentionally leaves that file; remove only our fixture.
Remove-Item -LiteralPath (Join-Path $target '用户自己添加的文件.txt')
Assert-Check '保留数据后可重新安装' ((Invoke-Setup $target) -eq 0)
[ordered]@{
    verifiedAt=(Get-Date).ToString('o'); passed=$results.Count
    checks=$results; guiFixture=$target; testData=$data
    realUserProfileUnchanged=$true
    noGui=[bool]$NoGui; busyDetectionMethod=$(if ($NoGui) {'silent process-name sentinel'} else {'isolated WPF app'})
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $build 'integration-results.json') -Encoding utf8
Write-Output "安装器集成检查通过：$($results.Count) 项。GUI 清理选项待实测。"
