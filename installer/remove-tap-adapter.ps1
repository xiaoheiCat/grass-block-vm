[CmdletBinding()]
param([string]$AdapterName = 'GrassVM-Tap')

$ErrorActionPreference = 'Stop'
$adapter = Get-NetAdapter -Name $AdapterName -ErrorAction SilentlyContinue
if ($null -eq $adapter) { exit 0 }
$pnp = Get-PnpDevice -InstanceId $adapter.PnpDeviceId -ErrorAction SilentlyContinue
if ($null -eq $pnp -or $pnp.HardwareId -notmatch '(?i)tap0901') {
  throw "拒绝移除名称为 $AdapterName 但并非 Grass Block VM TAP-Windows6 的适配器。"
}
Remove-NetAdapter -Name $AdapterName -Confirm:$false
