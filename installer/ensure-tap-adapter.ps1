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
$named = @($adapters | Where-Object { $_.Name -eq $AdapterName })
if ($named.Count -gt 1) { throw "检测到多个同名 TAP 适配器：$AdapterName" }
$adapter = $named | Select-Object -First 1
if ($null -eq $adapter) {
  $candidates = @(
    foreach ($candidate in $adapters) {
      if ($candidate.InterfaceDescription -notmatch '(?i)TAP[- ]Windows') { continue }
      $candidatePnp = Get-PnpDevice -InstanceId $candidate.PnpDeviceId -ErrorAction SilentlyContinue
      if ($null -ne $candidatePnp -and $candidatePnp.HardwareId -match [regex]::Escape($DriverHardwareId)) {
        $candidate
      }
    }
  )
  if ($candidates.Count -ne 1) {
    throw "无法唯一确定本次安装的 TAP-Windows6 适配器（找到 $($candidates.Count) 个候选）。请移除重复 TAP 设备后重试。"
  }
  $adapter = $candidates[0]
}

if ($null -eq $adapter) {
  throw "找不到 TAP-Windows6 适配器（驱动标识 $DriverHardwareId）。"
}

# 名称是可被用户或其他安装器修改的显示属性，不能单独证明设备归属。
# 即使已有同名 GrassVM-Tap，也必须核对 PNP 硬件 ID 和驱动描述，避免误把
# 普通网卡/其他虚拟网卡当成本项目适配器继续使用。
$pnp = Get-PnpDevice -InstanceId $adapter.PnpDeviceId -ErrorAction SilentlyContinue
if (($null -eq $pnp) -or ($pnp.HardwareId -notmatch [regex]::Escape($DriverHardwareId)) -or ($adapter.InterfaceDescription -notmatch '(?i)TAP[- ]Windows')) {
  throw "名称为 $AdapterName 的适配器不是 Grass Block VM TAP-Windows6 设备。"
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
