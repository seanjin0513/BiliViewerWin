; =============================================================================
;  BiliViewer Windows 安装程序（NSIS 3 + MUI2 + 简体中文）
;
;  编译（在 installer 目录下执行，需要先跑过 build.ps1 生成 ..\dist\BiliViewer）：
;     makensis /INPUTCHARSET UTF8 BiliViewer.nsi               ; 出货包：per-user，免管理员（默认）
;     makensis /INPUTCHARSET UTF8 -DPERMACHINE BiliViewer.nsi  ; 可选：per-machine，需要管理员
;
;  per-user（默认，出货用）：
;     RequestExecutionLevel user → 全程不弹 UAC
;     卸载项写 HKCU、快捷方式只放当前用户、回退目录 %LOCALAPPDATA%\Programs\BiliViewer
;  per-machine（-DPERMACHINE，可选）：
;     RequestExecutionLevel admin → 会弹 UAC
;     卸载项写 HKLM、快捷方式放所有用户、回退目录 %ProgramFiles%\BiliViewer
;
;  命令行：
;     BiliViewer-Setup-1.0.0.exe /S /D=C:\BiliViewer    ; 静默安装到指定目录
;     Uninstall.exe /S                                  ; 静默卸载
;
;  用户数据（登录 Cookie / 窗口设置）在 %LOCALAPPDATA%\BiliViewer，卸载时保留。
; =============================================================================

Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

; ----------------------------------------------------------------------------
; 基本信息
; ----------------------------------------------------------------------------
!define PRODUCT_NAME      "哔哩哔哩 BiliViewer"
!define PRODUCT_VERSION   "1.0.0"
!define PRODUCT_PUBLISHER "BiliViewer"
!define PRODUCT_EXE       "BiliViewer.exe"
!define APP_KEY           "BiliViewer"
!define APP_DATA_DIR      "$LOCALAPPDATA\BiliViewer"
!define ROOTDIR           ".."
; 界面位图所在目录（相对 installer\，由 tools\MakeIcon --installer-assets 生成）
!define ART_DIR           "assets"

; 默认安装目录：优先 D:\Program Files\BiliViewer，装前用探针文件确认当下真的可写；
; 不可写才回退到 ${FALLBACK_INSTDIR}（per-user 是 %LOCALAPPDATA%\Programs，per-machine 是 %ProgramFiles%）
!define DEFAULT_INSTDIR   "D:\Program Files\BiliViewer"

; ----------------------------------------------------------------------------
; 安装范围：默认 per-user（免管理员），-DPERMACHINE 切到 per-machine（需管理员）
; ----------------------------------------------------------------------------
!ifdef PERMACHINE
  !define REQLEVEL       "admin"
  !define REGROOT        "HKLM"
  !define UNINST_ROOT    "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
  !define SHELL_CTX      "all"
  !define FALLBACK_INSTDIR "$PROGRAMFILES64\BiliViewer"
  !define FALLBACK_TEXT  "%ProgramFiles%\BiliViewer"
  !define SCOPE_HINT     "这是「所有用户」安装包（per-machine），安装过程中会弹出 UAC 要求管理员权限。"
  !define SCOPE_SHORT    "所有用户安装（per-machine），安装时会弹出 UAC 要求管理员权限。"
  !define OUTFILE        "${ROOTDIR}\dist\BiliViewer-Setup-1.0.0-permachine.exe"
!else
  !define REQLEVEL       "user"
  !define REGROOT        "HKCU"
  !define UNINST_ROOT    "Software\Microsoft\Windows\CurrentVersion\Uninstall"
  !define SHELL_CTX      "current"
  !define FALLBACK_INSTDIR "$LOCALAPPDATA\Programs\BiliViewer"
  !define FALLBACK_TEXT  "%LOCALAPPDATA%\Programs\BiliViewer"
  !define SCOPE_HINT     "这是「仅当前用户」安装包（per-user），全程不需要管理员权限，不会弹出 UAC。"
  !define SCOPE_SHORT    "免管理员安装（仅当前用户），全程不需要管理员权限，不用装 .NET 运行时。"
  !define OUTFILE        "${ROOTDIR}\dist\BiliViewer-Setup-1.0.0.exe"
!endif

; 编译前自检：必须已经在 installer 目录下、且发布产物存在
!if /FILEEXISTS "${ROOTDIR}\dist\BiliViewer\BiliViewer.exe"
!else
  !error "找不到 ${ROOTDIR}\dist\BiliViewer\BiliViewer.exe —— 请先运行 build.ps1 生成发布产物，并在 installer 目录下执行 makensis。"
!endif
!if /FILEEXISTS "${ROOTDIR}\Assets\AppIcon.ico"
!else
  !error "找不到 ${ROOTDIR}\Assets\AppIcon.ico —— 请先运行 build.ps1 生成图标。"
!endif
!if /FILEEXISTS "${ART_DIR}\header.bmp"
!else
  !error "找不到 ${ART_DIR}\header.bmp —— 请先运行 build.ps1（tools\MakeIcon --installer-assets 生成界面位图）。"
!endif
!if /FILEEXISTS "${ART_DIR}\welcome-finish.bmp"
!else
  !error "找不到 ${ART_DIR}\welcome-finish.bmp —— 请先运行 build.ps1（tools\MakeIcon --installer-assets 生成界面位图）。"
!endif

Name "${PRODUCT_NAME}"
Caption "${PRODUCT_NAME} 安装程序"
BrandingText "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTFILE}"
RequestExecutionLevel ${REQLEVEL}
InstallDir "${DEFAULT_INSTDIR}"
InstallDirRegKey ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "InstallLocation"

SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on
CRCCheck on
XPStyle on
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "1.0.0.0"
VIAddVersionKey /LANG=2052 "ProductName"      "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "ProductVersion"   "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "FileVersion"      "1.0.0.0"
VIAddVersionKey /LANG=2052 "CompanyName"      "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription"  "${PRODUCT_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "LegalCopyright"   "Copyright (C) 2026 ${PRODUCT_PUBLISHER}"

; ----------------------------------------------------------------------------
; 界面（MUI2）
; ----------------------------------------------------------------------------
!define MUI_ICON   "${ROOTDIR}\Assets\AppIcon.ico"
!define MUI_UNICON "${ROOTDIR}\Assets\AppIcon.ico"
!define MUI_ABORTWARNING
; 页眉图：150x57 24 位 BMP（左侧白色电视机 + 横向粉蓝渐变），显示在右侧，不挤标题区
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP   "${ART_DIR}\header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "${ART_DIR}\header.bmp"

; 完成页 / 卸载完成页左侧竖幅：164x314 24 位 BMP，替换 MUI 默认的蓝色地球图
!define MUI_WELCOMEFINISHPAGE_BITMAP   "${ART_DIR}\welcome-finish.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${ART_DIR}\welcome-finish.bmp"

; 组件页顶部说明：原欢迎页的两条信息搬到这里。
; 控件实测 563x48（125% 缩放约 3 行），且 MUI 静态文本不给长单词折行 —— 所以这里只放两行短句，
; 具体回退路径放到目录页（那边控件实测 563x113，空间充裕）。
!define MUI_COMPONENTSPAGE_TEXT_TOP "${SCOPE_SHORT}$\r$\n默认装到 D:\Program Files\BiliViewer；该位置不可写时会自动改到用户目录。"

; 目录页顶部说明：长路径放在行首单独一行，避免不折行导致溢出
!define MUI_DIRECTORYPAGE_TEXT_TOP "请选择 ${PRODUCT_NAME} 的安装目录。安装包是绿色目录形式，卸载时整目录删除，不会动你的登录数据（%LOCALAPPDATA%\BiliViewer）。$\r$\n如果 D:\Program Files\BiliViewer 不可写，会自动改到：$\r$\n${FALLBACK_TEXT}"

; 完成页：可勾选「立即运行 BiliViewer」（默认勾选）
!define MUI_FINISHPAGE_RUN        "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT   "立即运行 BiliViewer"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_LINK       "访问哔哩哔哩"
!define MUI_FINISHPAGE_LINK_LOCATION "https://www.bilibili.com"

; 卸载完成页：提示用户数据被保留
!define MUI_UNFINISHPAGE_TEXT "${PRODUCT_NAME} 已从你的电脑上移除。$\r$\n$\r$\n登录数据与窗口设置已保留在 %LOCALAPPDATA%\BiliViewer"

; 不用 MUI_PAGE_WELCOME：MUI 欢迎页的正文控件只有 300px 宽、长英文路径不折行会糊成一坨，
; 欢迎信息已挪到组件页顶部（MUI_COMPONENTSPAGE_TEXT_TOP）。
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; /LANG=2052 为唯一语言，UI 默认就是简体中文
!insertmacro MUI_LANGUAGE "SimpChinese"

; ----------------------------------------------------------------------------
; .onInit：默认安装目录探测 + 运行中检测
; ----------------------------------------------------------------------------
Function .onInit
  ; ---- 1) 默认安装目录：命令行给了 /D= 就不探测 ----
  ;      （NSIS 会先把 /D= 的值写进 $INSTDIR，所以拿它和编译期默认值比对即可）
  StrCmp $INSTDIR "${DEFAULT_INSTDIR}" 0 bv_dir_done

  ; D: 盘存在吗？（IfFileExists 对目录也生效）
  IfFileExists "D:\*.*" 0 bv_no_d_drive

  ; 试着建 D:\Program Files\BiliViewer + 写一个探针文件，确认当前账户真的可写
  ; （非管理员账户对 D: 盘可能只读；写不进去就回退，避免装到一半失败）
  ClearErrors
  CreateDirectory "D:\Program Files\BiliViewer"
  ClearErrors
  FileOpen $9 "D:\Program Files\BiliViewer\.bv_write_test" w
  ${If} ${Errors}
    Goto bv_dir_fallback
  ${EndIf}
  FileClose $9
  Delete "D:\Program Files\BiliViewer\.bv_write_test"
  ; 探针用完把只为探测而建的目录清掉（非空则删除失败，无副作用）
  RMDir "D:\Program Files\BiliViewer"
  StrCpy $INSTDIR "${DEFAULT_INSTDIR}"
  DetailPrint "默认安装目录：${DEFAULT_INSTDIR}"
  Goto bv_dir_done

  bv_no_d_drive:
  DetailPrint "没有可用的 D: 盘，回退到 ${FALLBACK_TEXT}"
  Goto bv_dir_fallback

  bv_dir_fallback:
  StrCpy $INSTDIR "${FALLBACK_INSTDIR}"
  DetailPrint "D: 盘不可写，回退到 ${FALLBACK_TEXT}"

  bv_dir_done:

  ; ---- 2) 如果 BiliViewer.exe 正在运行，提示关闭并中止 ----
  ;      tasklist 过滤 + find 判断，find 的退出码与系统语言无关
  nsExec::ExecToStack 'cmd.exe /C ""$SYSDIR\tasklist.exe" /FI "IMAGENAME eq ${PRODUCT_EXE}" /NH | "$SYSDIR\find.exe" /I "${PRODUCT_EXE}" >nul"'
  Pop $0
  Pop $1
  ${If} $0 == "0"
    ${If} ${Silent}
      SetErrorLevel 2
      Quit
    ${Else}
      MessageBox MB_ICONSTOP|MB_OK "${PRODUCT_NAME} 正在运行，请先关闭它（包括托盘里的小图标）再安装。"
      Quit
    ${EndIf}
  ${EndIf}
FunctionEnd

; ----------------------------------------------------------------------------
; 安装：主程序
; ----------------------------------------------------------------------------
Section "哔哩哔哩 BiliViewer（必需）" SEC_MAIN
  SectionIn RO

  SetShellVarContext ${SHELL_CTX}

  ; 绿色目录整体复制（含 Assets、runtimes 等子目录）
  SetOutPath "$INSTDIR"
  File /r "${ROOTDIR}\dist\BiliViewer\*.*"

  ; 卸载程序
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 开始菜单：哔哩哔哩\BiliViewer + 哔哩哔哩\卸载 BiliViewer
  CreateDirectory "$SMPROGRAMS\哔哩哔哩"
  CreateShortCut "$SMPROGRAMS\哔哩哔哩\BiliViewer.lnk" "$INSTDIR\${PRODUCT_EXE}" "" "$INSTDIR\${PRODUCT_EXE}" 0
  CreateShortCut "$SMPROGRAMS\哔哩哔哩\卸载 BiliViewer.lnk" "$INSTDIR\Uninstall.exe"

  ; 卸载登记（Windows 设置「应用」里能看到；per-user 写 HKCU，per-machine 写 HKLM）
  WriteRegStr   ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "DisplayName"     "${PRODUCT_NAME}"
  WriteRegStr   ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "DisplayVersion"  "${PRODUCT_VERSION}"
  WriteRegStr   ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "Publisher"       "${PRODUCT_PUBLISHER}"
  WriteRegStr   ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "DisplayIcon"     "$INSTDIR\${PRODUCT_EXE},0"
  WriteRegStr   ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "NoModify" 1
  WriteRegDWORD ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "NoRepair" 1

  ; 安装体积（KB）
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "EstimatedSize" $0

  DetailPrint "已登记卸载信息：${REGROOT}\${UNINST_ROOT}\${APP_KEY}"
SectionEnd

; ----------------------------------------------------------------------------
; 安装：桌面快捷方式（可取消勾选，默认勾选）
; ----------------------------------------------------------------------------
Section "创建桌面快捷方式" SEC_DESKTOP
  SetShellVarContext ${SHELL_CTX}
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${PRODUCT_EXE}" "" "$INSTDIR\${PRODUCT_EXE}" 0
SectionEnd

; 组件页说明（必须在 section 定义之后，才能引用 ${SEC_MAIN} / ${SEC_DESKTOP}）
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN}    "哔哩哔哩 BiliViewer 主程序和运行库（必需）。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "在桌面创建一个「${PRODUCT_NAME}」快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ----------------------------------------------------------------------------
; 卸载
;   注意：%LOCALAPPDATA%\BiliViewer（登录 Cookie / 窗口设置）不删除
; ----------------------------------------------------------------------------
Function un.onInit
  SetShellVarContext ${SHELL_CTX}
  ; 真实安装目录从注册表读（$INSTDIR 在卸载器里的初始值只是编译期默认值）
  ReadRegStr $0 ${REGROOT} "${UNINST_ROOT}\${APP_KEY}" "InstallLocation"
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
FunctionEnd

Section "Uninstall"
  SetShellVarContext ${SHELL_CTX}

  ; 快捷方式
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\哔哩哔哩\BiliViewer.lnk"
  Delete "$SMPROGRAMS\哔哩哔哩\卸载 BiliViewer.lnk"
  RMDir  "$SMPROGRAMS\哔哩哔哩"

  ; 卸载登记
  DeleteRegKey ${REGROOT} "${UNINST_ROOT}\${APP_KEY}"

  ; 安装目录（整目录删掉；%LOCALAPPDATA%\BiliViewer 不在这个目录里，安全）
  ${If} $INSTDIR != ""
    ${If} ${FileExists} "$INSTDIR\${PRODUCT_EXE}"
      RMDir /r "$INSTDIR"
    ${Else}
      ${If} ${FileExists} "$INSTDIR\Uninstall.exe"
        RMDir /r "$INSTDIR"
      ${EndIf}
    ${EndIf}
  ${EndIf}

  DetailPrint "登录数据与窗口设置已保留在 ${APP_DATA_DIR}"
SectionEnd
