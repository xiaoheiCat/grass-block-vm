[CmdletBinding()]
param(
  [string]$AdapterName = 'GrassVM-Tap',
  [string]$DriverHardwareId = 'tap0901'
)

$ErrorActionPreference = 'Stop'

# tapinstall 创建的是 PNP 设备，设备的初始友好名称由驱动决定（通常是
# "TAP-Windows Adapter V9"），并不是 tap0901。QEMU 的 ifname 使用网络
# 适配器名称，因此安装后必须把实际设备映射到 Core 约定的稳定名称。
$adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue)
$adapter = $adapters |
  Where-Object {
    $_.Name -eq $AdapterName -or
    $_.PnpDeviceId -match [regex]::Escape($DriverHardwareId) -or
    $_.InterfaceDescription -match '(?i)TAP[- ]Windows'
  } |
  Select-Object -First 1

if ($null -eq $adapter) {
  throw "找不到 TAP-Windows6 适配器（驱动标识 $DriverHardwareId）。"
}

if ($adapter.Name -ne $AdapterName) {
  $nameTaken = Get-NetAdapter -Name $AdapterName -ErrorAction SilentlyContinue
  if ($null -ne $nameTaken -and $nameTaken.InterfaceIndex -ne $adapter.InterfaceIndex) {
    throw "目标 TAP 适配器名称已被其他网络设备占用：$AdapterName"
  }
  Rename-NetAdapter -Name $adapter.Name -NewName $AdapterName -Confirm:$false
}

# 复读一次，确保后续 QEMU 启动时 ifname 一定可解析。
if ($null -eq (Get-NetAdapter -Name $AdapterName -ErrorAction SilentlyContinue)) {
  throw "TAP 适配器重命名后不可见：$AdapterName"
}
