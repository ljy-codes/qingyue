param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $windowsRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build (Join-Path $windowsRoot 'Qingyue.Windows.slnx') -c $Configuration --nologo --disable-build-servers `
    -p:UseSharedCompilation=false -p:MSBuildEnableWorkloadResolver=false -p:ImportWorkloads=false -m:1
if ($LASTEXITCODE -ne 0) { throw "构建失败：$LASTEXITCODE" }
