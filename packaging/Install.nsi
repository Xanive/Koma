Unicode true
RequestExecutionLevel user
Name "Koma"
OutFile "${INSTALLER}"
InstallDir "$LOCALAPPDATA\Programs\Koma"
SetCompressor zlib
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Koma"

Function .onInit
    SetShellVarContext current
    ${IfNot} ${RunningX64}
        MessageBox MB_ICONSTOP "Koma requires Windows 11 x64."
        Abort
    ${EndIf}
    ${IfNot} ${AtLeastBuild} 22000
        MessageBox MB_ICONSTOP "Koma requires Windows 11 x64."
        Abort
    ${EndIf}
    IfFileExists "$INSTDIR\portable.marker" 0 done
        MessageBox MB_ICONSTOP "Choose an installation folder separate from the portable copy."
        Abort
    done:
FunctionEnd

Section "Koma"
    SetShellVarContext current
    SetOutPath "$INSTDIR"
    File /r "${PAYLOAD}\*"
    ${If} $VIDEOS != ""
        CreateDirectory "$VIDEOS\Anime"
    ${EndIf}
    CreateShortcut "$SMPROGRAMS\Koma.lnk" "$INSTDIR\Koma.exe"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Koma"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Koma.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetShellVarContext current
    !include "${REMOVE_MANIFEST}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    Delete "$SMPROGRAMS\Koma.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
