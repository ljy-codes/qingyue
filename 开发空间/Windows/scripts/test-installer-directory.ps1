param([string]$NsisPath = "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe")
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$build = Join-Path $windowsRoot 'artifacts/installer/directory-selection'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$fixture = Join-Path $build '父目录 有其他软件'
$existing = Join-Path $build '现有自定义安装目录'
New-Item -ItemType Directory -Path $fixture, $existing -Force | Out-Null
$sentinel = Join-Path $fixture '其他软件.txt'
Set-Content -LiteralPath $sentinel -Value 'must remain unchanged'
$sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
foreach ($file in @('Qingyue.exe', 'Qingyue.runtimeconfig.json')) {
    Set-Content -LiteralPath (Join-Path $existing $file) -Value 'read-only path fixture'
}
$cases = @(
    @{ name = '公共软件父目录自动添加轻阅子目录'; input = 'D:\Program File'; expected = 'D:\Program File\Qingyue' },
    @{ name = '含其他软件的中文空格父目录'; input = $fixture; expected = "$fixture\Qingyue" },
    @{ name = '选择Qingyue目录不重复追加'; input = "$fixture\Qingyue"; expected = "$fixture\Qingyue" },
    @{ name = '已有目录名不区分大小写'; input = "$fixture\qingyue"; expected = "$fixture\qingyue" },
    @{ name = '选择轻阅中文目录不重复追加'; input = "$fixture\轻阅"; expected = "$fixture\轻阅" },
    @{ name = '现有轻阅自定义安装目录原样保留'; input = $existing; expected = $existing },
    @{ name = '父目录尾部分隔符不会重复'; input = 'D:\Program File\'; expected = 'D:\Program File\Qingyue' },
    @{ name = '磁盘根目录选择得到专用子目录'; input = 'D:\'; expected = 'D:\Qingyue' },
    @{ name = '空选择保持为空'; input = ''; expected = '' }
)
function ConvertTo-NsisLiteral([string]$value) {
    return $value.Replace('$','$$').Replace('"','$\"').Replace("`r",'').Replace("`n",'')
}
$output = Join-Path $build 'selected-directories.txt'
$sourcePath = Join-Path $windowsRoot 'installer/install-directory.nsh'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('Unicode true')
$lines.Add('RequestExecutionLevel user')
$lines.Add('SilentInstall silent')
$lines.Add('!include LogicLib.nsh')
$lines.Add('!include FileFunc.nsh')
$lines.Add('!define PRODUCT_ID "Qingyue"')
$lines.Add('!include "' + (ConvertTo-NsisLiteral $sourcePath) + '"')
$lines.Add('OutFile "' + (ConvertTo-NsisLiteral (Join-Path $build 'directory-selection-check.exe')) + '"')
$lines.Add('Section')
$lines.Add('FileOpen $9 "' + (ConvertTo-NsisLiteral $output) + '" w')
for ($index = 0; $index -lt $cases.Count; $index++) {
    $lines.Add('Push "' + (ConvertTo-NsisLiteral $cases[$index].input) + '"')
    $lines.Add('Call ResolveBrowseDirectory')
    $lines.Add('Pop $0')
    $lines.Add('FileWriteUTF16LE $9 "' + $index + '|$0$\r$\n"')
}
$lines.Add('FileClose $9')
$lines.Add('SectionEnd')
$script = Join-Path $build 'directory-selection-check.nsi'
Set-Content -LiteralPath $script -Value $lines -Encoding utf8
& $NsisPath /INPUTCHARSET UTF8 /V2 $script
if ($LASTEXITCODE) { throw '安装目录只读回归程序编译失败。' }
$process = Start-Process -FilePath (Join-Path $build 'directory-selection-check.exe') -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "安装目录只读回归程序失败：$($process.ExitCode)" }
$actual = [IO.File]::ReadAllLines($output, [Text.Encoding]::Unicode)
if ($actual.Count -ne $cases.Count) { throw '安装目录回归结果数量不符。' }
$checks = @(
    for ($index = 0; $index -lt $cases.Count; $index++) {
        $selected = ($actual[$index] -split '\|', 2)[1]
        [ordered]@{
            name = $cases[$index].name
            input = $cases[$index].input
            expected = $cases[$index].expected
            actual = $selected
            passed = ($selected -ceq $cases[$index].expected)
        }
    }
    [ordered]@{
        name = '目录选择不写入父目录也不创建安装文件'
        passed = ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash -and
            -not (Test-Path -LiteralPath (Join-Path $fixture 'Qingyue')))
    }
)
$failed = @($checks | Where-Object { -not $_.passed })
[ordered]@{
    verifiedAt = (Get-Date).ToString('o')
    passed = ($failed.Count -eq 0)
    checkCount = $checks.Count
    checks = $checks
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $build 'results.json') -Encoding utf8
$checks | ForEach-Object {
    Write-Output (('{0}: {1}' -f $(if ($_.passed) { 'PASS' } else { 'FAIL' }), $_.name))
}
if ($failed.Count) { throw "安装目录回归失败：$($failed.Count) 项；详见 $build/results.json" }
