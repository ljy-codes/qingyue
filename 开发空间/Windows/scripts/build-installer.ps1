param(
    [switch]$SkipPublish,
    [switch]$TestBuild,
    [switch]$FullPayload,
    [ValidateSet('Solid32','Solid8','Lzma8','Zlib')][string]$CompressionMode = 'Solid32',
    [string]$NsisPath = "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe"
)
$ErrorActionPreference = 'Stop'
$totalClock = [Diagnostics.Stopwatch]::StartNew()
$windowsRoot = Split-Path -Parent $PSScriptRoot
$repo = (Resolve-Path (Join-Path $windowsRoot '../..')).Path
$source = Join-Path $windowsRoot 'installer'
$versionSource = Get-Content -LiteralPath (Join-Path $source 'version.nsh') -Raw
$productVersion = [regex]::Match($versionSource,'!define VERSION "([^"]+)"').Groups[1].Value
$buildVersion = [regex]::Match($versionSource,'!define BUILD_VERSION "([^"]+)"').Groups[1].Value
$windowsBuild = [regex]::Match($versionSource,'!define WINDOWS_BUILD "([^"]+)"').Groups[1].Value
if (-not $productVersion -or -not $buildVersion -or -not $windowsBuild) { throw '安装器版本配置不完整。' }
if ($FullPayload -and -not $TestBuild) { throw 'FullPayload 仅用于隔离 TestBuild。' }
$build = Join-Path $windowsRoot 'artifacts/installer'
if ($TestBuild) { $build = Join-Path $build 'test' }
New-Item -ItemType Directory -Path $build -Force | Out-Null
if (-not (Test-Path -LiteralPath $NsisPath)) { throw "未找到 NSIS 3：$NsisPath" }
if (-not $SkipPublish -and -not $TestBuild) { & (Join-Path $PSScriptRoot 'publish.ps1') }
$payload = Join-Path $repo '交付产品/轻阅-Windows-x64'
$icon = Join-Path $windowsRoot 'src/Qingyue.Windows/Assets/Qingyue.ico'
if (-not (Test-Path -LiteralPath $icon)) { & (Join-Path $PSScriptRoot 'build-brand-assets.ps1') }
if ($TestBuild -and -not $FullPayload) {
    $payload = Join-Path $build 'payload'
    New-Item -ItemType Directory -Path (Join-Path $payload '示例/子目录') -Force | Out-Null
    # Small isolated fixture; it exercises the exact same installer/deletion logic.
    foreach ($file in @('Qingyue.exe','Qingyue.dll','Qingyue.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $repo "交付产品/轻阅-Windows-x64/$file") -Destination $payload -Force
    }
    Copy-Item -LiteralPath $icon -Destination (Join-Path $payload 'Qingyue.ico') -Force
    Set-Content -LiteralPath (Join-Path $payload '示例/子目录/中文样例.txt') -Value '轻阅安装器集成验收'
}
if (-not (Test-Path -LiteralPath (Join-Path $payload 'Qingyue.exe'))) { throw '请先发布 Windows 自包含目录。' }
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars32.bat'
if (-not (Test-Path -LiteralPath $vcvars)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vcvars = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC/Auxiliary/Build/vcvars32.bat')[0]
}
if (-not $vcvars) { throw '需要 MSVC x86 C++ Build Tools 和 Windows SDK。' }
$saved = @{}
Get-ChildItem Env: | ForEach-Object { $saved[$_.Name] = $_.Value }
try {
    $lines = & $env:ComSpec /d /c "`"$vcvars`" >nul && set"
    if ($LASTEXITCODE) { throw '无法初始化 MSVC x86 环境。' }
    foreach ($line in $lines) {
        if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1],$Matches[2],'Process') }
    }
    foreach ($unit in @('theme','safety')) {
        $name = if ($unit -eq 'theme') { 'QingyueTheme' } else { 'QingyueSafety' }
        & cl.exe /nologo /utf-8 /std:c++17 /LD /O2 /MT /W4 /WX /EHsc (Join-Path $source "$unit.cpp") `
            "/Fo$build/$unit.obj" /link "/OUT:$build/$name.dll" "/IMPLIB:$build/$name.lib" `
            user32.lib gdi32.lib comctl32.lib uxtheme.lib dwmapi.lib shell32.lib ole32.lib
        if ($LASTEXITCODE) { throw "$unit 编译失败。" }
    }
    & rc.exe /nologo /c65001 "/fo$build/wizard.res" (Join-Path $source 'wizard.rc')
    if ($LASTEXITCODE) { throw '向导资源编译失败。' }
    & link.exe /nologo /dll /noentry /machine:X86 "/OUT:$build/wizard.dll" (Join-Path $build 'wizard.res')
    if ($LASTEXITCODE) { throw '向导资源链接失败。' }
} finally {
    Get-ChildItem Env: | Where-Object { -not $saved.ContainsKey($_.Name) } |
        ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name,$null,'Process') }
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key],'Process') }
}
# Escape NSIS metacharacters in source and destination filenames.
function ConvertTo-NsisLiteral([string]$value) {
    return $value.Replace('$','$$').Replace('"','$\"').Replace("`r",'').Replace("`n",'')
}
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)
$payloadBytes = [long]($files | Measure-Object Length -Sum).Sum
if ($payloadBytes -le 0) { throw '发布目录为空。' }
$install = [Collections.Generic.List[string]]::new()
$remove = [Collections.Generic.List[string]]::new()
$preflight = [Collections.Generic.List[string]]::new()
$lastDirectory = $null
$writtenBytes = 0L
$previousPercent = -1
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($payload,$file.FullName).Replace('/','\')
    if ($relative.StartsWith('..') -or $relative -eq 'Uninstall.exe') { throw "无效发布文件：$relative" }
    $directory = [IO.Path]::GetDirectoryName($relative)
    if ($directory -ne $lastDirectory) {
        $install.Add('SetOutPath "$INSTDIR' + $(if($directory) {'\' + (ConvertTo-NsisLiteral $directory)} else {''}) + '"')
        $lastDirectory = $directory
    }
    $install.Add('ClearErrors')
    $install.Add('File "' + (ConvertTo-NsisLiteral $file.FullName) + '"')
    $install.Add('IfErrors installFailed')
    $writtenBytes += $file.Length
    $percent = [int][math]::Floor(95.0 * $writtenBytes / $payloadBytes)
    if ($percent -ne $previousPercent) {
        $install.Add('QingyueTheme::Progress /NOUNLOAD "' + $percent + '" "正在解压并写入程序文件"')
        $previousPercent = $percent
    }
    $target = '$INSTDIR\' + (ConvertTo-NsisLiteral $relative)
    $preflight.Add('QingyueSafety::CanReplace /NOUNLOAD "' + $target + '"')
    $preflight.Add('Pop $Result')
    $preflight.Add('${If} $Result != ""')
    $preflight.Add('MessageBox MB_OK|MB_ICONSTOP "$Result$\r$\n' + $target + '" /SD IDOK')
    $preflight.Add('SetErrorLevel 5')
    $preflight.Add('Abort')
    $preflight.Add('${EndIf}')
    $remove.Add('IfFileExists "' + $target + '" 0 +6')
    $remove.Add('ClearErrors')
    $remove.Add('Delete "' + $target + '"')
    $remove.Add('IfErrors 0 +3')
    $remove.Add('SetErrorLevel 5')
    $remove.Add('Abort "无法删除程序文件，请关闭占用程序后重试。"')
}
$directories = @(Get-ChildItem -LiteralPath $payload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending)
foreach ($directory in $directories) {
    $relative = [IO.Path]::GetRelativePath($payload,$directory.FullName).Replace('/','\')
    $remove.Add('RMDir "$INSTDIR\' + (ConvertTo-NsisLiteral $relative) + '"')
}
$install.Add('SetOutPath "$INSTDIR"')
$installFile = Join-Path $build 'install-files.nsh'
$removeFile = Join-Path $build 'remove-files.nsh'
$preflightFile = Join-Path $build 'preflight-files.nsh'
Set-Content -LiteralPath $installFile -Value $install -Encoding utf8
Set-Content -LiteralPath $removeFile -Value $remove -Encoding utf8
Set-Content -LiteralPath $preflightFile -Value $preflight -Encoding utf8
$outfile = if ($TestBuild) { Join-Path $build 'Qingyue-installer-test.exe' } else {
    Join-Path $repo "交付产品/轻阅-$productVersion-Windows构建$windowsBuild-x64-setup.exe"
}
$arguments = @('/INPUTCHARSET','UTF8','/V3',"/DBUILD_DIR=$build","/DICON_FILE=$icon",
    "/DINSTALL_FILES=$installFile","/DREMOVE_FILES=$removeFile","/DPREFLIGHT_FILES=$preflightFile","/DOUT_FILE=$outfile",
    "/DESTIMATED_KB=$([math]::Ceiling($payloadBytes / 1024))",
    "/DESTIMATED_MB=$([math]::Ceiling($payloadBytes / 1MB))","/DCOMPRESSION_MODE=$CompressionMode")
if ($TestBuild) { $arguments += @('/DPRODUCT_ID=QingyueInstallerTest','/DDATA_LEAF=QingyueInstallerTest','/DLINK_NAME=轻阅安装器测试') }
$arguments += Join-Path $source 'qingyue.nsi'
$compileClock = [Diagnostics.Stopwatch]::StartNew()
& $NsisPath @arguments
if ($LASTEXITCODE) { throw "NSIS 编译失败：$LASTEXITCODE" }
$result = [ordered]@{
    createdAt = (Get-Date).ToString('o'); testBuild = [bool]$TestBuild; fullPayload = [bool]$FullPayload
    productVersion=$productVersion; fileVersion=$buildVersion; windowsBuild=$windowsBuild
    compressionMode=$CompressionMode
    installer = $outfile; length = (Get-Item -LiteralPath $outfile).Length
    sha256 = (Get-FileHash -LiteralPath $outfile -Algorithm SHA256).Hash
    payloadFiles = $files.Count; payloadBytes = $payloadBytes
    nsisSeconds=[math]::Round($compileClock.Elapsed.TotalSeconds,3)
    totalSeconds=[math]::Round($totalClock.Elapsed.TotalSeconds,3)
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $build 'build-results.json') -Encoding utf8
$result | ConvertTo-Json
