; Qingyue current-user installer. No elevation, network download or shell cleanup.
Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
RequestExecutionLevel user
!ifndef COMPRESSION_MODE
  !define COMPRESSION_MODE "Solid32"
!endif
!if "${COMPRESSION_MODE}" == "Zlib"
  SetCompressor zlib
!else if "${COMPRESSION_MODE}" == "Lzma8"
  SetCompressor lzma
  SetCompressorDictSize 8
!else
  SetCompressor /SOLID lzma
  !if "${COMPRESSION_MODE}" == "Solid32"
    SetCompressorDictSize 32
  !else
    SetCompressorDictSize 8
  !endif
!endif
!include LogicLib.nsh
!include nsDialogs.nsh
!include FileFunc.nsh
!include WordFunc.nsh
!include x64.nsh
!include "uninstall-actions.nsh"
!include "uninstall-pages.nsh"
!include "wizard-pages.nsh"
!insertmacro VersionCompare

!ifndef PRODUCT_ID
  !define PRODUCT_ID "Qingyue"
!endif
!ifndef DATA_LEAF
  !define DATA_LEAF "Qingyue"
!endif
!ifndef LINK_NAME
  !define LINK_NAME "轻阅"
!endif
!include "version.nsh"
!define REGKEY "Software\${PRODUCT_ID}\Windows"
!define UNKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"

!addplugindir "${BUILD_DIR}"
ChangeUI IDD_INST "${BUILD_DIR}\wizard.dll"
ChangeUI IDD_INSTFILES "${BUILD_DIR}\wizard.dll"
Icon "${ICON_FILE}"
UninstallIcon "${ICON_FILE}"
Name "轻阅"
Caption "轻阅 · 安装向导"
UninstallCaption "轻阅 · 卸载向导"
OutFile "${OUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
InstallDirRegKey HKCU "${REGKEY}" "InstallLocation"
BrandingText "轻阅 · 本机文本阅读与编辑"
SetFont "Microsoft YaHei UI" 9
InstallColors E4EDF7 0C121E
InstProgressFlags smooth
ShowInstDetails show
ShowUninstDetails show
AutoCloseWindow false
VIProductVersion "${BUILD_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "轻阅"
VIAddVersionKey /LANG=2052 "FileDescription" "轻阅 Windows 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${BUILD_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "轻阅 · Qingyue"
LoadLanguageFile "${NSISDIR}\Contrib\Language files\SimpChinese.nlf"
InstallButtonText "开始安装"
UninstallButtonText "卸载轻阅"
CompletedText "操作已完成"
DetailsButtonText "查看详细信息"

Var Dialog
Var PathInput
Var DesktopCheck
Var DesktopChoice
Var RunCheck
Var KeepRadio
Var PurgeRadio
Var PurgeChoice
Var PurgeConfirmCheck
Var PurgeConfirmed
Var OldLocation
Var OldVersion
Var Result
Var Control
Var UserDataPath

!include "install-directory.nsh"

Page custom LocationPage LocationLeave
Page instfiles InstallPre
Page custom FinishPage FinishLeave
UninstPage custom un.WelcomePage
UninstPage custom un.OptionsPage un.OptionsLeave
UninstPage custom un.ConfirmPage un.ConfirmLeave
UninstPage instfiles un.RemovePre
UninstPage custom un.FinishPage


Function .onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $DesktopChoice ${BST_CHECKED}
  StrCpy $UserDataPath "$LOCALAPPDATA\${DATA_LEAF}"
  QingyueSafety::RequestedDirectory /NOUNLOAD
  Pop $Result
  ${If} $Result != ""
    StrCpy $INSTDIR $Result
  ${Else}
    ReadRegStr $OldLocation HKCU "${REGKEY}" "InstallLocation"
    ${If} $OldLocation != ""
      StrCpy $INSTDIR $OldLocation
    ${EndIf}
  ${EndIf}
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 64 位 Windows。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd
Function .onGUIInit
  QingyueTheme::Apply /NOUNLOAD
  GetDlgItem $Control $HWNDPARENT 1035
  ShowWindow $Control ${SW_HIDE}
FunctionEnd
Function un.onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $PurgeChoice ${BST_UNCHECKED}
  StrCpy $PurgeConfirmed ${BST_UNCHECKED}
  StrCpy $UserDataPath "$LOCALAPPDATA\${DATA_LEAF}"
FunctionEnd
Function un.onGUIInit
  QingyueTheme::Apply /NOUNLOAD
  GetDlgItem $Control $HWNDPARENT 1035
  ShowWindow $Control ${SW_HIDE}
FunctionEnd

Function LocationPage
  !insertmacro Header "QINGYUE  /  安装设置" "选择位置，开始轻阅。" "${VERSION} · Windows 构建 ${WINDOWS_BUILD} · 无需管理员权限"
  !insertmacro NewPage
  !insertmacro Label 0 15 "安装文件夹"
  ${NSD_CreateText} 0 21u 250u 23u "$INSTDIR"
  Pop $PathInput
  ${NSD_CreateBrowseButton} 258u 21u 62u 23u "浏览…"
  Pop $Control
  ${NSD_OnClick} $Control BrowseDirectory
  !insertmacro Label 46 14 "浏览时自动补全 Qingyue 子文件夹，可直接编辑完整路径。"
  ${NSD_CreateCheckbox} 0 62u 320u 20u "创建桌面快捷方式"
  Pop $DesktopCheck
  ${NSD_SetState} $DesktopCheck $DesktopChoice
  !insertmacro Label 90 20 "开始菜单快捷方式与系统卸载入口将自动创建。"
  !insertmacro Label 118 27 "程序约需 ${ESTIMATED_MB} MB 空间。升级保留设置与恢复草稿。"
  !insertmacro NextText "开始安装"
  QingyueTheme::Apply /NOUNLOAD
  nsDialogs::Show
FunctionEnd
Function BrowseDirectory
  Pop $0
  ${NSD_GetText} $PathInput $0
  nsDialogs::SelectFolderDialog "选择安装位置（自动添加 Qingyue 子文件夹）" "$0"
  Pop $0
  ${If} $0 != error
    Push $0
    Call ResolveBrowseDirectory
    Pop $0
    ${NSD_SetText} $PathInput "$0"
  ${EndIf}
FunctionEnd
Function LocationLeave
  ${NSD_GetText} $PathInput $INSTDIR
  ${NSD_GetState} $DesktopCheck $DesktopChoice
  Call CheckInstall
FunctionEnd
Function CheckInstall
  QingyueSafety::ValidateRoot /NOUNLOAD "$INSTDIR" "$UserDataPath"
  Pop $Result
  ${If} $Result != ""
    MessageBox MB_OK|MB_ICONSTOP "$Result" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  QingyueSafety::NormalizePath /NOUNLOAD "$INSTDIR"
  Pop $INSTDIR
  ReadRegStr $OldLocation HKCU "${REGKEY}" "InstallLocation"
  ${If} $OldLocation != ""
    QingyueSafety::NormalizePath /NOUNLOAD "$OldLocation"
    Pop $OldLocation
    ${If} $OldLocation != $INSTDIR
      MessageBox MB_OK|MB_ICONSTOP "轻阅已安装在：$\r$\n$OldLocation$\r$\n$\r$\n覆盖更新请使用该位置。迁移位置请先卸载，并保留用户信息。" /SD IDOK
      SetErrorLevel 2
      Abort
    ${EndIf}
  ${EndIf}
  ReadRegStr $OldVersion HKCU "${REGKEY}" "BuildVersion"
  ${If} $OldVersion != ""
    ${VersionCompare} "$OldVersion" "${BUILD_VERSION}" $Result
    ${If} $Result == 1
      MessageBox MB_OK|MB_ICONSTOP "本机已安装更高版本，已阻止降级。" /SD IDOK
      SetErrorLevel 2
      Abort
    ${EndIf}
  ${EndIf}
  QingyueSafety::HasFiles /NOUNLOAD "$INSTDIR"
  Pop $Result
  StrCmp $Result 0 checkBusy
  IfFileExists "$INSTDIR\Qingyue.exe" 0 notDedicated
  IfFileExists "$INSTDIR\Qingyue.runtimeconfig.json" checkBusy notDedicated
  notDedicated:
    MessageBox MB_OK|MB_ICONSTOP "所选目录已有其他文件，不能直接安装。$\r$\n$\r$\n请点击“浏览”选择父目录，安装器会自动添加 Qingyue 子文件夹；也可手动输入空文件夹或已有轻阅安装文件夹的完整路径。$\r$\n$\r$\n当前目录：$INSTDIR" /SD IDOK
    SetErrorLevel 2
    Abort
  checkBusy:
  QingyueSafety::IsBusy /NOUNLOAD
  Pop $Result
  ${If} $Result == 1
    MessageBox MB_OK|MB_ICONSTOP "请先关闭所有轻阅窗口，保存编辑后再继续安装。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  !include "${PREFLIGHT_FILES}"
FunctionEnd
Function InstallPre
  !insertmacro Header "QINGYUE  /  正在安装" "正在准备你的阅读空间" "进度按实际写入文件计算，完成后即可使用。"
  QingyueTheme::Progress /NOUNLOAD "0" "正在校验安装目录与文件占用"
FunctionEnd
Function FinishPage
  !insertmacro Header "QINGYUE  /  已就绪" "安装完成，开始轻阅。" "轻阅 ${VERSION} · Windows 构建 ${WINDOWS_BUILD}"
  !insertmacro NewPage
  !insertmacro Label 4 24 "轻阅已安装至："
  !insertmacro Label 29 38 "$INSTDIR"
  ${NSD_CreateCheckbox} 0 87u 320u 22u "立即启动轻阅"
  Pop $RunCheck
  ${NSD_SetState} $RunCheck ${BST_CHECKED}
  !insertmacro Label 121 24 "可从 Windows 设置中的应用列表卸载。"
  !insertmacro NextText "完成"
  GetDlgItem $Control $HWNDPARENT 3
  ShowWindow $Control ${SW_HIDE}
  QingyueTheme::Apply /NOUNLOAD
  nsDialogs::Show
FunctionEnd
Function FinishLeave
  ${NSD_GetState} $RunCheck $Result
  ${If} $Result == ${BST_CHECKED}
    Exec '"$INSTDIR\Qingyue.exe"'
  ${EndIf}
FunctionEnd

Section "轻阅" SEC_MAIN
  QingyueTheme::Progress /NOUNLOAD "0" "正在校验安装目录与文件占用"
  Call CheckInstall
  SetOverwrite on
  QingyueTheme::Progress /NOUNLOAD "0" "正在解压并写入程序文件"
  !include "${INSTALL_FILES}"
  QingyueTheme::Progress /NOUNLOAD "96" "正在写入卸载入口"
  ClearErrors
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  FileOpen $0 "$INSTDIR\Qingyue.install" w
  IfErrors installFailed
  FileWrite $0 "${PRODUCT_ID}$\r$\n"
  FileClose $0
  IfErrors installFailed
  QingyueTheme::Progress /NOUNLOAD "98" "正在登记应用与创建快捷方式"
  ClearErrors
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "BuildVersion" "${BUILD_VERSION}"
  WriteRegStr HKCU "${UNKEY}" "DisplayName" "轻阅 · Qingyue"
  WriteRegStr HKCU "${UNKEY}" "DisplayVersion" "${VERSION} (Windows 构建 ${WINDOWS_BUILD})"
  WriteRegStr HKCU "${UNKEY}" "Publisher" "轻阅"
  WriteRegStr HKCU "${UNKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNKEY}" "DisplayIcon" "$INSTDIR\Qingyue.ico,0"
  WriteRegStr HKCU "${UNKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNKEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNKEY}" "EstimatedSize" ${ESTIMATED_KB}
  CreateShortcut "$SMPROGRAMS\${LINK_NAME}.lnk" "$INSTDIR\Qingyue.exe" "" "$INSTDIR\Qingyue.ico" 0
  ${If} $DesktopChoice == ${BST_CHECKED}
    CreateShortcut "$DESKTOP\${LINK_NAME}.lnk" "$INSTDIR\Qingyue.exe" "" "$INSTDIR\Qingyue.ico" 0
  ${EndIf}
  IfErrors installFailed
  QingyueTheme::Progress /NOUNLOAD "100" "安装完成"
  SetErrorLevel 0
  Goto installDone
  installFailed:
    QingyueTheme::Progress /NOUNLOAD "-1" "安装未完成，请处理错误后重新运行安装器"
    MessageBox MB_OK|MB_ICONSTOP "安装未完成，可能没有写入权限或文件正在被占用。请处理后重新运行安装器；升级时原有用户信息保持不变。" /SD IDOK
    SetErrorLevel 5
    Abort
  installDone:
SectionEnd

!insertmacro QingyueUninstallPages "un."

Function un.CheckRemove
  QingyueSafety::ValidateRoot /NOUNLOAD "$INSTDIR" "$UserDataPath"
  Pop $Result
  ${If} $Result != ""
    MessageBox MB_OK|MB_ICONSTOP "$Result" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ClearErrors
  FileOpen $0 "$INSTDIR\Qingyue.install" r
  IfErrors un.invalidMarker
  FileRead $0 $1
  FileClose $0
  StrCmp $1 "${PRODUCT_ID}$\r$\n" un.validMarker
  un.invalidMarker:
    MessageBox MB_OK|MB_ICONSTOP "无法确认轻阅安装目录，已停止卸载以保护其他文件。" /SD IDOK
    SetErrorLevel 2
    Abort
  un.validMarker:
  QingyueSafety::IsBusy /NOUNLOAD
  Pop $Result
  ${If} $Result == 1
    MessageBox MB_OK|MB_ICONSTOP "轻阅正在运行。请先保存并关闭所有轻阅窗口，再执行卸载。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  !include "${PREFLIGHT_FILES}"
FunctionEnd

Section "Uninstall"
  Call un.CheckRemove
  ; Silent uninstall ALWAYS keeps user information. There is no purge switch.
  ${If} ${Silent}
    StrCpy $PurgeChoice ${BST_UNCHECKED}
  ${EndIf}
  !insertmacro QingyueRemoveActions
SectionEnd
