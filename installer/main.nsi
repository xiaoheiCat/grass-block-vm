; Grass Block VM — NSIS 安装器（由 installer/build.ps1 先生成完整 stage）
; 逻辑要点（与 installer/README.md 的冻结决策一致）：
;   1. RequestExecutionLevel admin —— 一开始就整体提权，一次装齐驱动与组件
;   2. 升级前检测运行中的 VM（存在 GrassCore 进程即中止并提示）
;   3. 卸载清理 TAP 驱动；.grassvm 数据默认保留

!define APPNAME "Grass Block VM"
!ifndef VERSION
  !define VERSION "0.1.0"
!endif
!define COMPANY "Grass Block VM Project"
; QemuCommandBuilder 与安装器共享这一稳定 TAP 适配器名称。tap0901 是
; TAP-Windows6 的 PNP 硬件 ID，不是 Windows 网络连接中的友好名称。
!define TAP_ADAPTER_NAME "GrassVM-Tap"
!define TAP_DRIVER_HARDWARE_ID "tap0901"
!include "LogicLib.nsh"
!include "StrFunc.nsh"

Name "${APPNAME}"
OutFile "GrassBlockVM-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APPNAME}"
RequestExecutionLevel admin

Page directory
Page instfiles

Section "Core Components"
  SetOutPath $INSTDIR
  ; installer/build.ps1 先生成可运行的 stage 目录（Electron + resources/app + Core/QEMU/固件/Helper）。
  ; NSIS 只消费经过校验的发布目录，避免把开发 dist 当成安装产物。
  File /r "stage\*.*"
  File "remove-tap-adapter.ps1"

  ; TAP-Windows6：Grass Block VM Virtual Ethernet Adapter（桥接 / Host-only）
  ; tapinstall.exe 的第三个参数是 PNP 硬件 ID；安装后由 PowerShell 脚本
  ; 找到实际设备并重命名为 QemuCommandBuilder 使用的稳定 ifname。
  ExecWait '"$INSTDIR\drivers\tapinstall.exe" install "$INSTDIR\drivers\OemVista.inf" ${TAP_DRIVER_HARDWARE_ID}' $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "虚拟网卡驱动安装失败（错误码 $0）。安装已中止。"
    Abort
  ${EndIf}

  ; Get-NetAdapter/Rename-NetAdapter 需要管理员权限；安装器本身已整体提权。
  ; 脚本放在插件临时目录，安装结束后由 NSIS 自动清理，不进入产品目录。
  File "/oname=$PLUGINSDIR\ensure-tap-adapter.ps1" "ensure-tap-adapter.ps1"
  ExecWait 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\ensure-tap-adapter.ps1" -AdapterName "${TAP_ADAPTER_NAME}" -DriverHardwareId "${TAP_DRIVER_HARDWARE_ID}"' $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "虚拟网卡已安装，但无法映射为 ${TAP_ADAPTER_NAME}。安装已中止。"
    Abort
  ${EndIf}

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\Grass Block VM.exe"
  ; 安装器整体提权，HKCU 可能属于提供管理员凭据的另一个用户；使用 HKLM
  ; 确保首次安装后已有 HostDb 自启动清单可以在登录时被执行。Electron 的
  ; --autostart 模式无界面运行清单后退出，用户是否启用 VM 仍由 Core 决定。
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "${APPNAME}" '"$INSTDIR\Grass Block VM.exe" --autostart'
SectionEnd

Function .onInit
  ; GrassCore 是控制台进程，且 Core 崩溃时 QEMU 仍可能存活；两者都必须检查。
  Push "GrassCore.exe"
  Call CheckRunningProcess
  Push "qemu-system-x86_64.exe"
  Call CheckRunningProcess
  Push "qemu-img.exe"
  Call CheckRunningProcess
  Push "GrassSpiceHelper.exe"
  Call CheckRunningProcess
FunctionEnd

Function un.onInit
  ; 卸载同样不能在 VM/Helper 仍运行时移除程序目录，否则会留下半卸载状态，
  ; 更严重的是可能删掉仍被 QEMU 使用的运行库。复用安装前的进程检查。
  Push "GrassCore.exe"
  Call CheckRunningProcess
  Push "qemu-system-x86_64.exe"
  Call CheckRunningProcess
  Push "qemu-img.exe"
  Call CheckRunningProcess
  Push "GrassSpiceHelper.exe"
  Call CheckRunningProcess
FunctionEnd

Function CheckRunningProcess
  Exch $0
  ; tasklist 的 IMAGENAME 过滤器负责缩小结果；TABLE 输出再用进程名后的空格
  ; 做字段边界匹配，不能用裸子串，否则 NotGrassCore.exe 会误命中 GrassCore.exe。
  nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq $0" /FO TABLE /NH'
  Pop $1
  Pop $2
  ${If} $1 == 0
    ${StrStr} $3 $2 "$0 "
    ${If} $3 != ""
      MessageBox MB_OK|MB_ICONSTOP "检测到 $0 正在运行。请先关闭或挂起所有虚拟机，然后再运行 Grass Block VM 安装程序。"
      Abort
    ${EndIf}
  ${EndIf}
  Pop $0
FunctionEnd

Section "Uninstall"
  ; 仅移除安装器映射的 GrassVM-Tap 适配器；不能按通用 hardware-id
  ; 删除同机 OpenVPN 等软件的全部 TAP 设备。
  IfFileExists "$INSTDIR\remove-tap-adapter.ps1" 0 +2
    ExecWait 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\remove-tap-adapter.ps1" -AdapterName "${TAP_ADAPTER_NAME}"'
  Delete "$INSTDIR\remove-tap-adapter.ps1"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  ; 兼容早期版本可能写入的 HKLM/HKCU Run 项；新版本安装器不再创建它们。
  DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "${APPNAME}"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APPNAME}"
  RMDir /r "$INSTDIR"
  ; %USERPROFILE%\Documents\Grass Block VM 下的 .grassvm 默认保留
SectionEnd
