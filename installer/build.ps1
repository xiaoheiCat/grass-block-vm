$ErrorActionPreference = 'Stop'

# 生成 NSIS 消费的完整发布目录。脚本只接受显式输入路径，缺任何运行时组件都会失败，
# 不再产生“能安装但启动不了”的半成品安装包。
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageVersion = (Get-Content -Raw (Join-Path $repo 'package.json') | ConvertFrom-Json).version
$stage = Join-Path $PSScriptRoot 'stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

pnpm --dir $repo install --frozen-lockfile
pnpm --dir $repo --filter @grassvm/desktop build

$coreProject = Join-Path $repo 'apps/core/src/GrassCore/GrassCore.csproj'
$coreOut = Join-Path $stage 'resources/GrassCore'
dotnet publish $coreProject -c Release -r win-x64 --self-contained true -o $coreOut

# Electron Windows runtime 不是单文件：除了 exe 还依赖 DLL、pak、locales 以及
# resources/electron.asar/default_app.asar。先完整复制 dist，再仅重命名入口，
# 否则 NSIS 安装后会出现“exe 在但启动即退”的半成品。
$electronDist = Join-Path $repo 'apps/desktop/node_modules/electron/dist'
$electronExe = Join-Path $electronDist 'electron.exe'
if (-not (Test-Path -LiteralPath $electronExe)) { throw "找不到 Electron runtime：$electronExe" }
Copy-Item -Path (Join-Path $electronDist '*') -Destination $stage -Recurse -Force
Move-Item -LiteralPath (Join-Path $stage 'electron.exe') -Destination (Join-Path $stage 'Grass Block VM.exe') -Force

$appDir = Join-Path $stage 'resources/app'
$appDist = Join-Path $appDir 'dist'
New-Item -ItemType Directory -Path $appDist | Out-Null
# 明确把每个 bundle 的内容复制到对应子目录，避免依赖 Copy-Item 在已存在
# 目标目录上的隐式命名规则（不同 PowerShell 版本会表现成额外嵌套目录）。
foreach ($bundle in @('main', 'renderer')) {
  $source = Join-Path $repo ("apps/desktop/dist/" + $bundle)
  if (-not (Test-Path -LiteralPath $source)) { throw "缺少桌面端构建产物：$source" }
  $destination = Join-Path $appDist $bundle
  New-Item -ItemType Directory -Path $destination -Force | Out-Null
  Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'apps/desktop/package.json') -Destination $appDir

function Copy-RequiredDirectory([string]$source, [string]$destination) {
  if (-not (Test-Path -LiteralPath $source)) { throw "缺少发布组件：$source" }
  # 目标目录通常还不存在。Resolve-Path 在这种情况下返回 $null，不能直接
  # 访问 .Path，否则发布脚本会在复制第一个新目录时以 NullReferenceException
  # 退出，而不是按预期创建目录。
  $sourcePath = (Resolve-Path -LiteralPath $source).Path.TrimEnd([char]92, [char]47)
  $destinationResolved = Resolve-Path -LiteralPath $destination -ErrorAction SilentlyContinue
  if ($null -ne $destinationResolved) {
    $destinationPath = $destinationResolved.Path.TrimEnd([char]92, [char]47)
    if ([StringComparer]::OrdinalIgnoreCase.Equals($sourcePath, $destinationPath)) { return }
  }
  New-Item -ItemType Directory -Path $destination -Force | Out-Null
  Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
}

$qemuDir = if ($env:GRASSVM_QEMU_DIR) { $env:GRASSVM_QEMU_DIR } else { Join-Path $repo 'runtime/qemu/win-x64' }
$firmwareDir = if ($env:GRASSVM_FIRMWARE_DIR) { $env:GRASSVM_FIRMWARE_DIR } else { Join-Path $repo 'runtime/firmware' }
$helperDir = if ($env:GRASSVM_HELPER_DIR) { $env:GRASSVM_HELPER_DIR } else { Join-Path $repo 'runtime/GrassSpiceHelper' }
$driversDir = if ($env:GRASSVM_TAP_DIR) { $env:GRASSVM_TAP_DIR } else { Join-Path $repo 'runtime/tap' }

$tapInstaller = Join-Path $driversDir 'tapinstall.exe'
$tapInf = Join-Path $driversDir 'OemVista.inf'
if (-not (Test-Path -LiteralPath $tapInstaller) -or -not (Test-Path -LiteralPath $tapInf)) {
  throw "TAP 驱动目录必须包含 tapinstall.exe 与 OemVista.inf：$driversDir"
}
$helperExe = Join-Path $helperDir 'GrassSpiceHelper.exe'
if (-not (Test-Path -LiteralPath $helperExe)) {
  # 开发仓库没有提交二进制 Helper 时，从同一源码构建发布件；正式构建机
  # 使用 .NET 10 SDK，与 GrassCore 的 self-contained 发布保持一致。
  $helperProject = Join-Path $repo 'apps/helper/GrassSpiceHelper.csproj'
  if (-not (Test-Path -LiteralPath $helperProject)) {
    throw "Helper 发布目录必须包含 GrassSpiceHelper.exe：$helperDir"
  }
  $helperDir = Join-Path $stage 'resources/GrassSpiceHelper'
  dotnet publish $helperProject -c Release -r win-x64 --self-contained true -o $helperDir
  $helperExe = Join-Path $helperDir 'GrassSpiceHelper.exe'
  if (-not (Test-Path -LiteralPath $helperExe)) { throw "Helper 构建未生成 GrassSpiceHelper.exe。" }
}
foreach ($firmwareName in @('OVMF_CODE.fd', 'OVMF_CODE.secboot.fd', 'OVMF_VARS.fd')) {
  if (-not (Test-Path -LiteralPath (Join-Path $firmwareDir $firmwareName))) {
    throw "固件发布目录缺少 $firmwareName：$firmwareDir"
  }
}
Copy-RequiredDirectory $qemuDir (Join-Path $stage 'resources/QEMU')
Copy-RequiredDirectory $firmwareDir (Join-Path $stage 'resources/firmware')
Copy-RequiredDirectory $helperDir (Join-Path $stage 'resources/GrassSpiceHelper')
Copy-RequiredDirectory $driversDir (Join-Path $stage 'drivers')

# 升级保护以 QEMU major 为边界，marker 必须来自实际随包发布的二进制，不能写
# 固定的 "bundled"。同时在打包期验证 qemu-system/qemu-img 都确实可执行。
$stagedQemu = Join-Path $stage 'resources/QEMU/qemu-system-x86_64.exe'
$stagedQemuImg = Join-Path $stage 'resources/QEMU/qemu-img.exe'
if (-not (Test-Path -LiteralPath $stagedQemu) -or -not (Test-Path -LiteralPath $stagedQemuImg)) {
  throw "QEMU 发布目录缺少 qemu-system-x86_64.exe 或 qemu-img.exe：$qemuDir"
}
$qemuVersionText = (& $stagedQemu --version 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $qemuVersionText -notmatch '(?im)QEMU emulator version\s+(\d+)') {
  throw "无法读取随包 QEMU 的主版本号。"
}
$markerPath = Join-Path $stage 'resources/QEMU/qemu-major.txt'
# Windows 构建机可能仍使用 Windows PowerShell 5.1（不支持 utf8NoBOM 参数），
# 用 .NET API 明确写 UTF-8 无 BOM，兼容 5.1 与 pwsh 7。
$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
[System.IO.File]::WriteAllText($markerPath, $Matches[1], $utf8NoBom)

Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $stage

$makensis = Get-Command makensis -ErrorAction SilentlyContinue
if (-not $makensis) {
  throw "找不到 makensis。发布目录已生成，但为避免误把 stage 当成可安装产品，安装器编译已中止。请安装 NSIS 3.x 后重试。"
}

# main.nsi 使用 stage\... 相对路径；固定在 installer 目录编译，且让 OutFile
# 落在 installer/ 下，避免从任意当前目录运行脚本时找不到文件或把安装器
# 写到不可预期的位置。
Push-Location $PSScriptRoot
try {
  & $makensis.Source /V2 "/DVERSION=$packageVersion" 'main.nsi'
  if ($LASTEXITCODE -ne 0) { throw "makensis 编译失败（退出码 $LASTEXITCODE）。" }
}
finally {
  Pop-Location
}

Write-Host "安装器已生成：$(Join-Path $PSScriptRoot ('GrassBlockVM-Setup-' + $packageVersion + '.exe'))"
