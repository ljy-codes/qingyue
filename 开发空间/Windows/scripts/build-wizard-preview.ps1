# Renders the actual uninstall pages without any file/registry/deletion actions.
$ErrorActionPreference='Stop'
$windowsRoot=Split-Path -Parent $PSScriptRoot
$build=Join-Path $windowsRoot 'artifacts/installer/test'
$source=Join-Path $windowsRoot 'installer'
$icon=Join-Path $windowsRoot 'src/Qingyue.Windows/Assets/Qingyue.ico'
if(-not (Test-Path -LiteralPath (Join-Path $build 'wizard.dll'))) {throw '请先构建 TestBuild。'}
$template=@'
Unicode true
RequestExecutionLevel user
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
!include LogicLib.nsh
!include nsDialogs.nsh
!include "@SOURCE@\wizard-pages.nsh"
!include "@SOURCE@\uninstall-pages.nsh"
!addplugindir "@BUILD@"
!include "@SOURCE@\version.nsh"
ChangeUI IDD_INST "@BUILD@\wizard.dll"
Icon "@ICON@"
Name "轻阅界面预览"
Caption "轻阅 · 卸载界面预览（不执行卸载）"
OutFile "@BUILD@\Qingyue-WizardPreview.exe"
SetFont "Microsoft YaHei UI" 9
LoadLanguageFile "${NSISDIR}\Contrib\Language files\SimpChinese.nlf"
Var Dialog
Var Control
Var PurgeChoice
Var UserDataPath
Var KeepRadio
Var PurgeRadio
Page custom OptionsPage OptionsLeave
Page instfiles RemovePre
!insertmacro QingyueUninstallPages ""
Function .onInit
StrCpy $PurgeChoice ${BST_UNCHECKED}
StrCpy $UserDataPath "$LOCALAPPDATA\Qingyue"
FunctionEnd
Function .onGUIInit
QingyueTheme::Apply /NOUNLOAD
GetDlgItem $Control $HWNDPARENT 1035
ShowWindow $Control ${SW_HIDE}
FunctionEnd
Function CheckRemove
; Deliberately empty. This executable only previews the shared UI.
FunctionEnd
Section "Preview only"
; No filesystem, registry, shortcut, payload, or uninstall operations.
SectionEnd
'@
$script=Join-Path $build 'wizard-preview.nsi'
Set-Content -LiteralPath $script -Value $template.Replace('@SOURCE@',$source).Replace('@BUILD@',$build).Replace('@ICON@',$icon) -Encoding utf8
& "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe" /INPUTCHARSET UTF8 /V2 $script
if($LASTEXITCODE) {throw '界面预览程序构建失败。'}
Write-Output (Join-Path $build 'Qingyue-WizardPreview.exe')
