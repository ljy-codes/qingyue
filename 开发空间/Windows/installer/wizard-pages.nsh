!macro Header Code Title Subtitle
  GetDlgItem $Control $HWNDPARENT 1101
  SendMessage $Control ${WM_SETTEXT} 0 "STR:${Code}"
  GetDlgItem $Control $HWNDPARENT 1037
  SendMessage $Control ${WM_SETTEXT} 0 "STR:${Title}"
  GetDlgItem $Control $HWNDPARENT 1038
  SendMessage $Control ${WM_SETTEXT} 0 "STR:${Subtitle}"
!macroend
!macro NewPage
  nsDialogs::Create 1018
  Pop $Dialog
  ${If} $Dialog == error
    Abort
  ${EndIf}
!macroend
!macro Label Y H Text
  ${NSD_CreateLabel} 0 ${Y}u 100% ${H}u "${Text}"
  Pop $Control
!macroend
!macro NextText Text
  GetDlgItem $Control $HWNDPARENT 1
  SendMessage $Control ${WM_SETTEXT} 0 "STR:${Text}"
!macroend
