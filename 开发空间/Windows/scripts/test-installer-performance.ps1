param(
    [string]$PayloadPath,
    [string]$Label = 'current',
    [ValidateSet('Solid32','Solid8','Lzma8','Zlib')][string[]]$Modes = @('Solid32','Solid8','Lzma8','Zlib'),
    [ValidateRange(1,5)][int]$Runs = 3,
    [string]$NsisPath = "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe"
)
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
if (-not $PayloadPath) { $PayloadPath = Join-Path $windowsRoot '../../交付产品/轻阅-Windows-x64' }
$payload = (Resolve-Path -LiteralPath $PayloadPath).Path
if ($Label -notmatch '^[a-zA-Z0-9-]+$') { throw 'Label 仅允许字母、数字和连字符。' }
$root = [IO.Path]::GetFullPath((Join-Path $windowsRoot "artifacts/installer/QingyueInstallerTest/performance/$Label"))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)
if (-not $files.Count) { throw '发布目录为空。' }
$expected = @{}
foreach ($file in $files) {
    $expected[[IO.Path]::GetRelativePath($payload,$file.FullName)] = (Get-FileHash -LiteralPath $file.FullName).Hash
}
function NsisLiteral([string]$value) { $value.Replace('$','$$').Replace('"','$\"') }
$results = @(
    foreach ($mode in $Modes) {
        $modeRoot = Join-Path $root $mode
        New-Item -ItemType Directory -Path $modeRoot -Force | Out-Null
        $target = Join-Path $modeRoot ('extract-' + [guid]::NewGuid().ToString('N'))
        $exe = Join-Path $modeRoot 'QingyueInstallerTest-extract.exe'
        $compressor = switch ($mode) {
            Solid32 { "SetCompressor /SOLID lzma`nSetCompressorDictSize 32" }
            Solid8 { "SetCompressor /SOLID lzma`nSetCompressorDictSize 8" }
            Lzma8 { "SetCompressor lzma`nSetCompressorDictSize 8" }
            Zlib { 'SetCompressor zlib' }
        }
        # Pure extraction benchmark: fixed workspace destination, no registry,
        # shortcuts, app launch, profile access, or production installation.
        $text = @"
Unicode true
RequestExecutionLevel user
SilentInstall silent
$compressor
OutFile "$(NsisLiteral $exe)"
Section
SetOutPath "$(NsisLiteral $target)"
File /r "$(NsisLiteral $payload)\*"
IfErrors 0 +3
SetErrorLevel 5
Abort
SectionEnd
"@
        $script = Join-Path $modeRoot 'extract.nsi'
        Set-Content -LiteralPath $script -Value $text -Encoding utf8
        $clock = [Diagnostics.Stopwatch]::StartNew()
        & $NsisPath /INPUTCHARSET UTF8 /V2 $script
        if ($LASTEXITCODE) { throw "压测包编译失败：$mode" }
        $compileSeconds = $clock.Elapsed.TotalSeconds
        $seconds = @(
            for ($run = 1; $run -le $Runs; $run++) {
                $clock.Restart()
                $process = Start-Process -FilePath $exe -WindowStyle Hidden -Wait -PassThru
                $elapsed = $clock.Elapsed.TotalSeconds
                if ($process.ExitCode) { throw "解压失败：$mode / $($process.ExitCode)" }
                $actual = @(Get-ChildItem -LiteralPath $target -Recurse -File)
                if ($actual.Count -ne $files.Count) { throw "解压文件数不同：$mode" }
                foreach ($file in $actual) {
                    $relative = [IO.Path]::GetRelativePath($target,$file.FullName)
                    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected[$relative]) {
                        throw "解压内容不一致：$relative"
                    }
                }
                # Guard the resolved absolute path before each recursive removal.
                $resolved = (Resolve-Path -LiteralPath $target).Path
                if (-not $resolved.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase) -or
                    (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
                    throw '压测目录越界或为链接，拒绝清理。'
                }
                Remove-Item -LiteralPath $resolved -Recurse -Force
                [math]::Round($elapsed,3)
            }
        )
        $sorted = @($seconds | Sort-Object)
        [ordered]@{
            mode=$mode; installerBytes=(Get-Item -LiteralPath $exe).Length
            compileSeconds=[math]::Round($compileSeconds,3); extractionSeconds=$seconds
            medianSeconds=$sorted[[int][math]::Floor($sorted.Count / 2)]
            allFileHashesVerified=$true
        }
    }
)
$record = [ordered]@{
    measuredAt=(Get-Date).ToString('o'); label=$Label
    payloadPath=$payload; payloadFiles=$files.Count
    payloadBytes=($files | Measure-Object Length -Sum).Sum
    method='Isolated pure extraction; stopwatch excludes SHA256 verification and cleanup; sequential warm-cache runs, not end-to-end setup'
    results=$results
}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
$record | ConvertTo-Json -Depth 6
