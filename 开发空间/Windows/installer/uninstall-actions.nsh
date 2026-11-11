; Shared production removal actions; the test harness supplies an isolated identity.
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
  DeleteRegKey HKCU "${UNKEY}"
  DeleteRegKey HKCU "${REGKEY}"
  DeleteRegKey /ifempty HKCU "Software\${PRODUCT_ID}"
  QingyueTheme::Progress /NOUNLOAD "100" "卸载完成，即将关闭"
  SetErrorLevel 0
  Goto removeDone
  purgeCleanupFailed:
    MessageBox MB_OK|MB_ICONSTOP "安装目录仍有文件被占用，卸载未完成。请关闭占用程序后重试。" /SD IDOK
    SetErrorLevel 4
    Abort
  removeDone:
!macroend
