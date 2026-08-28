; Grass Block VM — NSIS 安装器骨架（需要 Windows 构建机上完成实机验证后启用）
; 逻辑要点（与 installer/README.md 的冻结决策一致）：
;   1. RequestExecutionLevel admin —— 一开始就整体提权，一次装齐驱动与组件
;   2. 升级前检测运行中的 VM（存在 GrassCore 进程即中止并提示）
;   3. 卸载清理 TAP 驱动；.grassvm 数据默认保留

!define APPNAME "Grass Block VM"
!define VERSION "0.1.0"
!define COMPANY "Grass Block VM Project"

Name "${APPNAME}"
OutFile "GrassBlockVM-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APPNAME}"
RequestExecutionLevel admin

Page directory
Page instfiles

Section "Core Components"
  SetOutPath $INSTDIR
  File /r "..\apps\desktop\dist\*.*"
  File /r "..\apps\core\rundir\*.*"

  ; TAP-Windows6：Grass Block VM Virtual Ethernet Adapter（桥接 / Host-only）
  ; tapinstall.exe 由驱动包提供；设备名与 QemuCommandBuilder 的 TapName 保持一致
  ; ExecWait '"$INSTDIR\drivers\tapinstall.exe" install OemVista.inf TAP0901dev'

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\Grass Block VM.exe"
SectionEnd

Function .onInit
  ; 有任何 VM 运行就禁止安装更新
  FindWindow $0 "" "GrassCoreWindow"
  IfErrors 0 +3
    MessageBox MB_OK|MB_ICONSTOP "请先关闭或挂起所有正在运行的虚拟机，然后再运行 Grass Block VM 安装程序。"
    Abort
FunctionEnd

Section "Uninstall"
  ; 移除虚拟网卡驱动（组件与主程序同生共死，但用户数据不动）
  ; ExecWait '"$INSTDIR\drivers\tapinstall.exe" remove TAP0901dev'
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  RMDir /r "$INSTDIR"
  ; %USERPROFILE%\Documents\Grass Block VM 下的 .grassvm 默认保留
SectionEnd
