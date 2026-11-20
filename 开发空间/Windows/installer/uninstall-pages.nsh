; Actual uninstall pages, also rendered by the side-effect-free design preview.
!macro QingyueUninstallPages PREFIX
Function ${PREFIX}OptionsPage
  !insertmacro Header "QINGYUE  /  卸载轻阅" "卸载，也尊重你的选择。" "默认保留用户信息；完成后窗口会自动关闭。"
  !insertmacro NewPage
  !insertmacro Label 3 20 "感谢这一段，和文字相处的时光。"
  !insertmacro Label 25 20 "请选择卸载方式。其他位置保存的文档不会被删除。"
  ${NSD_CreateRadioButton} 0 52u 320u 22u "保留用户信息（推荐）"
  Pop $KeepRadio
  ${NSD_OnClick} $KeepRadio ${PREFIX}SelectionChanged
  !insertmacro Label 74 18 "保留设置、最近记录与恢复草稿，重新安装后可继续使用。"
  ${NSD_CreateRadioButton} 0 96u 320u 22u "彻底卸载并清理用户信息"
  Pop $PurgeRadio
  ${NSD_OnClick} $PurgeRadio ${PREFIX}SelectionChanged
  !insertmacro Label 118 27 "删除用户信息和安装目录内的全部内容，不可恢复。用户信息：$UserDataPath"
  ${If} $PurgeChoice == ${BST_CHECKED}
    ${NSD_SetState} $PurgeRadio ${BST_CHECKED}
  ${Else}
    ${NSD_SetState} $KeepRadio ${BST_CHECKED}
  ${EndIf}
  Call ${PREFIX}UpdateSelectionText
  QingyueTheme::Apply /NOUNLOAD
  nsDialogs::Show
FunctionEnd
Function ${PREFIX}OptionsLeave
  ${NSD_GetState} $PurgeRadio $PurgeChoice
  Call ${PREFIX}CheckRemove
FunctionEnd
Function ${PREFIX}SelectionChanged
  Pop $0
  Call ${PREFIX}UpdateSelectionText
FunctionEnd
Function ${PREFIX}UpdateSelectionText
  !insertmacro NextText "卸载轻阅"
FunctionEnd
Function ${PREFIX}RemovePre
  !insertmacro Header "QINGYUE  /  正在卸载" "正在完成最后的整理" "请稍候，卸载完成后窗口会自动关闭。"
  QingyueTheme::Progress /NOUNLOAD "0" "正在准备卸载"
FunctionEnd
!macroend
