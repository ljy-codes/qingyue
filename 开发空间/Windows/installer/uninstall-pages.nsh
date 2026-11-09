; Actual uninstall pages, also rendered by the side-effect-free design preview.
!macro QingyueUninstallPages PREFIX
Function ${PREFIX}WelcomePage
  !insertmacro Header "QINGYUE  /  01 · 卸载" "卸载，也尊重你的选择。" "你可以保留阅读状态，或清理轻阅的用户信息。"
  !insertmacro NewPage
  !insertmacro Label 6 29 "感谢这一段，和文字相处的时光。"
  !insertmacro Label 48 35 "下一步选择是否保留设置、最近记录与恢复草稿。"
  !insertmacro Label 102 40 "你在其他位置保存的文档不会被删除。$\r$\n请先关闭轻阅，并保存尚未保存的编辑。"
  !insertmacro NextText "继续"
  QingyueTheme::Apply /NOUNLOAD
  nsDialogs::Show
FunctionEnd
Function ${PREFIX}OptionsPage
  !insertmacro Header "QINGYUE  /  02 · 用户信息" "用户信息，由你决定。" "默认保留。清理恢复草稿前，会再次征求你的确认。"
  !insertmacro NewPage
  ${NSD_CreateRadioButton} 0 0 320u 22u "保留用户信息（推荐）"
  Pop $KeepRadio
  ${NSD_OnClick} $KeepRadio ${PREFIX}SelectionChanged
  !insertmacro Label 25 23 "保留设置、最近记录与恢复草稿，重新安装后可继续使用。"
  ${NSD_CreateRadioButton} 0 57u 320u 22u "彻底卸载并清理用户信息"
  Pop $PurgeRadio
  ${NSD_OnClick} $PurgeRadio ${PREFIX}SelectionChanged
  !insertmacro Label 82 27 "同时删除上述信息和未保存草稿，此操作不可恢复。"
  !insertmacro Label 119 26 "用户信息位置：$UserDataPath"
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
  ${NSD_GetState} $PurgeRadio $PurgeChoice
  ${If} $PurgeChoice == ${BST_CHECKED}
    !insertmacro NextText "继续确认"
  ${Else}
    !insertmacro NextText "卸载轻阅"
  ${EndIf}
FunctionEnd
Function ${PREFIX}ConfirmPage
  ${If} $PurgeChoice != ${BST_CHECKED}
    Abort
  ${EndIf}
  !insertmacro Header "QINGYUE  /  03 · 清理确认" "最后确认，草稿将被清理。" "未保存内容删除后无法恢复，请认真核对。"
  !insertmacro NewPage
  !insertmacro Label 0 31 "将永久删除轻阅的设置、最近记录、会话和恢复草稿。"
  !insertmacro Label 38 38 "$UserDataPath"
  !insertmacro Label 83 24 "其他位置保存的文档不受影响。"
  ${NSD_CreateCheckbox} 0 117u 320u 24u "我确认删除这些用户信息与恢复草稿"
  Pop $PurgeConfirmCheck
  StrCpy $PurgeConfirmed ${BST_UNCHECKED}
  ${NSD_OnClick} $PurgeConfirmCheck ${PREFIX}ConfirmChanged
  !insertmacro NextText "清理并卸载"
  GetDlgItem $Control $HWNDPARENT 1
  EnableWindow $Control 0
  QingyueTheme::Apply /NOUNLOAD
  nsDialogs::Show
FunctionEnd
Function ${PREFIX}ConfirmChanged
  Pop $0
  ${NSD_GetState} $PurgeConfirmCheck $PurgeConfirmed
  GetDlgItem $Control $HWNDPARENT 1
  EnableWindow $Control $PurgeConfirmed
FunctionEnd
Function ${PREFIX}ConfirmLeave
  ${If} $PurgeConfirmed != ${BST_CHECKED}
    Abort
  ${EndIf}
  Call ${PREFIX}CheckRemove
FunctionEnd
Function ${PREFIX}RemovePre
  !insertmacro Header "QINGYUE  /  03 · 正在卸载" "正在完成最后的整理" "按你的选择处理用户信息，请稍候。"
FunctionEnd
Function ${PREFIX}FinishPage
  !insertmacro Header "QINGYUE  /  04 · 卸载完成" "整理完毕，后会有期。" "轻阅程序和系统卸载入口已移除。"
  !insertmacro NewPage
  ${If} $PurgeChoice == ${BST_CHECKED}
    !insertmacro Label 8 40 "设置、最近记录与恢复草稿已清理。"
  ${Else}
    !insertmacro Label 8 40 "你的设置、最近记录与恢复草稿已保留。"
    !insertmacro Label 50 42 "$UserDataPath"
  ${EndIf}
  !insertmacro Label 106 39 "其他位置的文档，以及安装目录中自行添加的文件，均予以保留。"
  !insertmacro NextText "完成"
  GetDlgItem $Control $HWNDPARENT 3
  ShowWindow $Control ${SW_HIDE}
  QingyueTheme::Apply /NOUNLOAD
  nsDialogs::Show
FunctionEnd
!macroend
