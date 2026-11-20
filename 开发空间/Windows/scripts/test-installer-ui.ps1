param(
    [string]$SetupPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/installer/test/Qingyue-installer-test.exe')
)
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetFileName($SetupPath) -ne 'Qingyue-installer-test.exe') { throw '仅允许隔离测试安装器。' }
$version = (Get-Item -LiteralPath $SetupPath).VersionInfo.FileVersion
$versionSource = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/version.nsh') -Raw
$expectedVersion = [regex]::Match($versionSource,'!define BUILD_VERSION "([^"]+)"').Groups[1].Value
if ($version -ne $expectedVersion) { throw "FAIL: 安装器文件版本应为 $expectedVersion，实际为 $version" }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$process = Start-Process -FilePath $SetupPath -WindowStyle Normal -PassThru
try {
    if (-not $process.WaitForInputIdle(15000)) { throw '安装器未进入空闲状态。' }
    $limit = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "安装器提前退出：$($process.ExitCode)" }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            $root = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $elements = $root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
            $names = @($elements | ForEach-Object { $_.Current.Name })
    $edits = @($elements | Where-Object {
        $_.Current.ClassName -eq 'Edit' -and -not $_.Current.IsOffscreen
    })
            if ($edits.Count -and $names -contains '开始安装') { break }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $limit)
    if (-not $edits.Count -or $names -notcontains '开始安装') { throw "FAIL: 首屏必须直接提供路径输入和开始安装。实际控件：$($names -join ' | ')" }
    $result = [ordered]@{
        verifiedAt=(Get-Date).ToString('o'); version=$version
        visiblePathInput=$true; firstPageHasStartInstall=$true
        noWelcomeStep=$true; names=$names
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $SetupPath) 'ui-results.json') -Encoding utf8
    $result | ConvertTo-Json -Depth 4
} finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { throw '测试向导未正常关闭，请检查；不会强杀进程。' }
    }
}
