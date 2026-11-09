; Shared by the folder picker and a read-only directory-selection regression.
; Input and output are the selected path on the NSIS stack.
Function ResolveBrowseDirectory
  Exch $0
  Push $1
  ${If} $0 == ""
    Goto browseDirectoryDone
  ${EndIf}
  ; The picker returns a base folder. Trim trailing separators, including a
  ; drive root, before adding the product folder; validation still runs later.
  browseDirectoryTrim:
    StrCpy $1 $0 1 -1
    ${If} $1 == "\"
      StrCpy $0 $0 -1
      Goto browseDirectoryTrim
    ${EndIf}
  ; Updating an existing install may use a custom folder name.
  IfFileExists "$0\Qingyue.exe" 0 browseDirectoryName
  IfFileExists "$0\Qingyue.runtimeconfig.json" browseDirectoryDone browseDirectoryName
  browseDirectoryName:
    ${GetFileName} "$0" $1
    ${If} $1 == "${PRODUCT_ID}"
    ${OrIf} $1 == "轻阅"
      Goto browseDirectoryDone
    ${EndIf}
    StrCpy $0 "$0\${PRODUCT_ID}"
  browseDirectoryDone:
  Pop $1
  Exch $0
FunctionEnd
