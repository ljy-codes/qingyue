; Shared production removal actions; the test harness supplies an isolated identity.
!ifndef REG_ROOT
  !define REG_ROOT HKCU
!endif
!ifndef ASSOCIATION_ROOT
  !define ASSOCIATION_ROOT "Software\Classes"
!endif
!ifndef ASSOCIATION_PROGID
  !define ASSOCIATION_PROGID "Qingyue.Document"
!endif
!macro QingyueRemoveActions
  ${If} $PurgeChoice == ${BST_CHECKED}
    QingyueSafety::PurgeInstall /NOUNLOAD "$INSTDIR" "$UserDataPath" "${PRODUCT_ID}"
    Pop $Result
    ${If} $Result != ""
      MessageBox MB_OK|MB_ICONSTOP "$Result" /SD IDOK
      SetErrorLevel 4
      Abort
    ${EndIf}
    QingyueSafety::PurgeData /NOUNLOAD "$UserDataPath" "${DATA_LEAF}"
    Pop $Result
    ${If} $Result != ""
      MessageBox MB_OK|MB_ICONSTOP "$Result" /SD IDOK
      SetErrorLevel 4
      Abort
    ${EndIf}
  ${Else}
    !include "${REMOVE_FILES}"
  ${EndIf}
  Delete "$SMPROGRAMS\${LINK_NAME}.lnk"
  Delete "$DESKTOP\${LINK_NAME}.lnk"
  ${If} $PurgeChoice == ${BST_CHECKED}
    ClearErrors
    Delete "$INSTDIR\Qingyue.install"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    IfErrors purgeCleanupFailed
  ${Else}
    Delete "$INSTDIR\Qingyue.install"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
  ${EndIf}
  DeleteRegKey ${REG_ROOT} "${UNKEY}"
  DeleteRegKey ${REG_ROOT} "${REGKEY}"
  DeleteRegKey /ifempty ${REG_ROOT} "Software\${PRODUCT_ID}"
  !insertmacro QingyueUnregisterAssociation txt
  !insertmacro QingyueUnregisterAssociation log
  !insertmacro QingyueUnregisterAssociation md
  !insertmacro QingyueUnregisterAssociation markdown
  !insertmacro QingyueUnregisterAssociation mdown
  !insertmacro QingyueUnregisterAssociation mkd
  !insertmacro QingyueUnregisterAssociation json
  !insertmacro QingyueUnregisterAssociation jsonc
  !insertmacro QingyueUnregisterAssociation yaml
  !insertmacro QingyueUnregisterAssociation yml
  !insertmacro QingyueUnregisterAssociation ini
  !insertmacro QingyueUnregisterAssociation cfg
  !insertmacro QingyueUnregisterAssociation conf
  !insertmacro QingyueUnregisterAssociation properties
  !insertmacro QingyueUnregisterAssociation env
  !insertmacro QingyueUnregisterAssociation xml
  !insertmacro QingyueUnregisterAssociation xaml
  !insertmacro QingyueUnregisterAssociation svg
  !insertmacro QingyueUnregisterAssociation plist
  !insertmacro QingyueUnregisterAssociation csproj
  !insertmacro QingyueUnregisterAssociation csv
  !insertmacro QingyueUnregisterAssociation tsv
  !ifdef TEST_ASSOCIATION_EXTENSION
    !insertmacro QingyueUnregisterAssociation ${TEST_ASSOCIATION_EXTENSION}
  !endif
  DeleteRegKey ${REG_ROOT} "${ASSOCIATION_ROOT}\${ASSOCIATION_PROGID}"
  ; Remove the previous per-user registration so a system install cannot
  ; leave a stale entry behind in the current user's app list.
  DeleteRegKey HKCU "${UNKEY}"
  DeleteRegKey HKCU "${REGKEY}"
  DeleteRegKey /ifempty HKCU "Software\${PRODUCT_ID}"
  !if "${REG_ROOT}" != "HKCU"
    DeleteRegKey HKCU "${ASSOCIATION_ROOT}\${ASSOCIATION_PROGID}"
  !endif
  QingyueTheme::Progress /NOUNLOAD "100" "卸载完成，即将关闭"
  SetErrorLevel 0
  Goto removeDone
  purgeCleanupFailed:
    MessageBox MB_OK|MB_ICONSTOP "安装目录仍有文件被占用，卸载未完成。请关闭占用程序后重试。" /SD IDOK
    SetErrorLevel 4
    Abort
  removeDone:
!macroend
