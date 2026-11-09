#requires -Version 5.1
<#
.SYNOPSIS
Compile the theme/resources and check layout, sibling occlusion and native bar
painting on a private desktop. Does not run NSIS, switch desktops or install.
All build outputs go to a new TEMP/QingyueInstallerTest folder.
#>
[CmdletBinding()]
param(
    [string]$VcVarsPath = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars32.bat'
)
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer'
if (-not (Test-Path -LiteralPath $VcVarsPath -PathType Leaf)) { throw "MSVC x86 environment not found: $VcVarsPath" }
$build = Join-Path $env:TEMP ('QingyueInstallerTest\theme-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $build)
$saved = @{}
Get-ChildItem Env: | ForEach-Object { $saved[$_.Name] = $_.Value }
try {
    $lines = & $env:ComSpec /d /c "`"$VcVarsPath`" >nul && set"
    if ($LASTEXITCODE) { throw 'Cannot initialize MSVC x86 environment.' }
    foreach ($line in $lines) {
        if ($line -match '^([^=]+)=(.*)$') {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
        }
    }
    $theme = Join-Path $build 'QingyueTheme.dll'
    $wizard = Join-Path $build 'wizard.dll'
    & cl.exe /nologo /utf-8 /std:c++17 /LD /O2 /MT /W4 /WX /EHsc (Join-Path $source 'theme.cpp') `
        "/Fo$build\theme.obj" /link "/OUT:$theme" "/IMPLIB:$build\theme.lib" `
        user32.lib gdi32.lib comctl32.lib uxtheme.lib dwmapi.lib
    if ($LASTEXITCODE) { throw 'Theme compile failed.' }
    & rc.exe /nologo /c65001 "/fo$build\wizard.res" (Join-Path $source 'wizard.rc')
    if ($LASTEXITCODE) { throw 'Resource compile failed.' }
    & link.exe /nologo /dll /noentry /machine:X86 "/OUT:$wizard" (Join-Path $build 'wizard.res')
    if ($LASTEXITCODE) { throw 'Resource link failed.' }
    $testExe = Join-Path $build 'theme-visibility.exe'
    & cl.exe /nologo /utf-8 /std:c++17 /O2 /MT /W4 /WX /EHsc (Join-Path $source 'tests\theme-visibility.cpp') `
        "/Fo$build\test.obj" "/Fe$testExe" /link user32.lib gdi32.lib comctl32.lib
    if ($LASTEXITCODE) { throw 'Visibility test compile failed.' }
    $output = @(& $testExe $wizard $theme)
    $testExitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $build 'visibility.log') -Encoding UTF8
    $output | Write-Output
    [ordered]@{
        verifiedAt = (Get-Date).ToString('o')
        passed = ($testExitCode -eq 0)
        exitCode = $testExitCode
        outputDirectory = $build
        resourceSha256 = (Get-FileHash -LiteralPath (Join-Path $source 'wizard.rc') -Algorithm SHA256).Hash
        themeSha256 = (Get-FileHash -LiteralPath (Join-Path $source 'theme.cpp') -Algorithm SHA256).Hash
        scope = 'Private desktop only; no SwitchDesktop, installer, uninstall or delivery build.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $build 'results.json') -Encoding UTF8
    Write-Output "Evidence: $build"
    if ($testExitCode -ne 0) { throw "Theme visibility checks failed: $testExitCode" }
} finally {
    Get-ChildItem Env: | Where-Object { -not $saved.ContainsKey($_.Name) } |
        ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process') }
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
}
