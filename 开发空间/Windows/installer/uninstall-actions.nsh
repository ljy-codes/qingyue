; Shared production removal actions; the test harness supplies an isolated identity.
!macro QingyueRemoveActions
  ${If} $PurgeChoice == ${BST_CHECKED}
    QingyueSafety::PurgeData /NOUNLOAD "$UserDataPath" "${DATA_LEAF}"
    Pop $Result
    ${If} $Result != ""
      MessageBox MB_OK|MB_ICONSTOP "$Result" /SD IDOK
      SetErrorLevel 4
      Abort
    ${EndIf}
  ${EndIf}
  !include "${REMOVE_FILES}"
  Delete "$SMPROGRAMS\${LINK_NAME}.lnk"
  Delete "$DESKTOP\${LINK_NAME}.lnk"
  DeleteRegKey HKCU "${UNKEY}"
  DeleteRegKey HKCU "${REGKEY}"
  DeleteRegKey /ifempty HKCU "Software\${PRODUCT_ID}"
  Delete "$INSTDIR\Qingyue.install"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  SetErrorLevel 0
!macroend
