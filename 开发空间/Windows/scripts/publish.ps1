param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$totalClock = [Diagnostics.Stopwatch]::StartNew()
$windowsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $windowsRoot '../..')).Path
$output = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '交付产品/轻阅-Windows-x64'))
$jobRoot = [IO.Path]::GetFullPath((Join-Path $windowsRoot ('artifacts/publish/' + [guid]::NewGuid().ToString('N'))))
$stage = Join-Path $jobRoot '轻阅-Windows-x64'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$versionSource = Get-Content -LiteralPath (Join-Path $windowsRoot 'installer/version.nsh') -Raw
$version = [regex]::Match($versionSource,'!define VERSION "([^"]+)"').Groups[1].Value
$build = [regex]::Match($versionSource,'!define WINDOWS_BUILD "([^"]+)"').Groups[1].Value
if (-not $version -or -not $build) { throw '安装器版本配置不完整。' }
$env:DOTNET_CLI_HOME = Join-Path $windowsRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$publishClock = [Diagnostics.Stopwatch]::StartNew()
dotnet publish (Join-Path $windowsRoot 'src/Qingyue.Windows/Qingyue.Windows.csproj') `
    -c Release -r $Runtime --self-contained true -o $stage --nologo --disable-build-servers `
    -p:UseSharedCompilation=false -p:PublishSingleFile=false -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -m:1
if ($LASTEXITCODE -ne 0) { throw "发布失败：$LASTEXITCODE；原发布目录保留。" }
$publishSeconds = $publishClock.Elapsed.TotalSeconds
# Filter satellites in the staged output instead of passing an escaped MSBuild list:
# runtime-pack tasks can treat that escaped list as one culture and omit zh-Hans.
# Filter only recognized culture directories containing satellite DLLs only.
# All runtime and WPF main assemblies remain in the new staging directory.
$before = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
$removed = @(
    foreach ($directory in Get-ChildItem -LiteralPath $stage -Directory) {
        if ($directory.Name -in @('zh-Hans','en')) { continue }
        try { $culture = [Globalization.CultureInfo]::GetCultureInfo($directory.Name) } catch { continue }
        if (-not $culture.Name) { continue }
        $entries = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -Force)
        if (-not $entries.Count -or @($entries | Where-Object {
            $_.PSIsContainer -or $_.Name -notlike '*.resources.dll' -or
            $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)
        }).Count) { throw "语言目录包含非卫星文件，拒绝过滤：$($directory.FullName)" }
        $resolved = (Resolve-Path -LiteralPath $directory.FullName).Path
        if (-not $resolved.StartsWith($stage + '\',[StringComparison]::OrdinalIgnoreCase) -or
            $directory.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw '语言目录越界或为链接。' }
        [ordered]@{culture=$directory.Name;files=$entries.Count;bytes=($entries | Measure-Object Length -Sum).Sum}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
)
foreach ($required in @('Qingyue.exe','Qingyue.dll','Qingyue.runtimeconfig.json','coreclr.dll',
    'hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll',
    'PresentationCore.dll','WindowsBase.dll','wpfgfx_cor3.dll','zh-Hans/PresentationFramework.resources.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) { throw "自包含/WPF 必要文件缺失：$required" }
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot '操作指导/Windows使用说明.md') -Destination (Join-Path $stage 'Windows使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $windowsRoot 'src/Qingyue.Windows/Assets/Qingyue.ico') -Destination (Join-Path $stage 'Qingyue.ico') -Force
Copy-Item -LiteralPath (Join-Path $windowsRoot 'installer/UPSTREAM.md') -Destination (Join-Path $stage '安装器许可.txt') -Force
$sampleOutput = Join-Path $stage '示例'
New-Item -ItemType Directory -Path $sampleOutput -Force | Out-Null
Copy-Item -Path (Join-Path $repositoryRoot '开发空间/Samples/*') -Destination $sampleOutput -Recurse -Force
Copy-Item -Path (Join-Path $windowsRoot 'Samples/*') -Destination $sampleOutput -Recurse -Force
$archiveClock = [Diagnostics.Stopwatch]::StartNew()
$archiveName = "轻阅-$version-Windows构建$build-x64.zip"
$stagedZip = Join-Path $jobRoot $archiveName
Compress-Archive -LiteralPath $stage -DestinationPath $stagedZip -CompressionLevel Optimal
$archiveSeconds = $archiveClock.Elapsed.TotalSeconds
# Promote a completed directory, keeping the previous output for recovery.
$previous = Join-Path $jobRoot 'previous'
foreach ($path in @($stage,$output,$previous)) {
    if (-not $path.StartsWith($repositoryRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "发布路径越界：$path" }
    if ((Test-Path -LiteralPath $path) -and
        (Get-Item -LiteralPath $path).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "发布路径不能是链接：$path" }
}
$zipPath = Join-Path (Split-Path -Parent $output) $archiveName
if (Test-Path -LiteralPath $output) { Move-Item -LiteralPath $output -Destination $previous }
try { Move-Item -LiteralPath $stage -Destination $output } catch {
    if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $output }
    throw
}
Move-Item -LiteralPath $stagedZip -Destination $zipPath -Force
$files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
$result = [ordered]@{
    createdAt=(Get-Date).ToString('o'); version=$version; windowsBuild=$build
    output=$output; zip=$zipPath; zipBytes=(Get-Item -LiteralPath $zipPath).Length
    payloadFiles=$files.Count; payloadBytes=($files | Measure-Object Length -Sum).Sum
    beforeLanguageFilterFiles=$before.Count; beforeLanguageFilterBytes=($before | Measure-Object Length -Sum).Sum
    removedSatelliteCultures=$removed; retainedSatelliteCultures=@('zh-Hans','en (neutral fallback when no en directory exists)')
    runtimeAndWpfKept=$true; previousOutput=$previous
    publishSeconds=[math]::Round($publishSeconds,3); archiveSeconds=[math]::Round($archiveSeconds,3)
    totalSeconds=[math]::Round($totalClock.Elapsed.TotalSeconds,3)
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $windowsRoot 'artifacts/publish-results.json') -Encoding utf8
Get-FileHash -LiteralPath (Join-Path $output 'Qingyue.exe'), $zipPath -Algorithm SHA256 |
    Format-Table Path,Hash -AutoSize
Write-Output "便携目录：$output"
Write-Output "ZIP：$zipPath"
$result | ConvertTo-Json -Depth 6
