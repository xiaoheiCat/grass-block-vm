/**
 * 开发启动脚本（Node，无额外依赖）：
 * 1. 编译 Electron 主进程（tsc → dist/main）
 * 2. 构建渲染层（vite build → dist/renderer）
 * 3. 发布一个 framework-dependent GrassCore 到 core-rundir，再启动 Electron。
 *    这样 `pnpm dev` 不会在首次 RPC 时才发现 Core 可执行文件不存在。
 *
 * 说明：热更新模式（pnpm run dev:web，vite dev server）用于纯渲染层迭代；
 * 主进程/Preload 改动需要重启本脚本。产品目标平台是 Windows；macOS 上仅做逻辑联调。
 */
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const coreProject = resolve(repo, 'apps', 'core', 'src', 'GrassCore', 'GrassCore.csproj');
const coreRundir = resolve(here, 'core-rundir');
const electronBin = process.platform === 'darwin'
  ? resolve(here, 'node_modules', 'electron', 'dist', 'Electron.app', 'Contents', 'MacOS', 'Electron')
  : process.platform === 'win32'
    ? resolve(here, 'node_modules', 'electron', 'dist', 'electron.exe')
    : resolve(here, 'node_modules', 'electron', 'dist', 'electron');
const tscBin = resolve(here, 'node_modules', 'typescript', 'bin', 'tsc');
const viteBin = resolve(here, 'node_modules', 'vite', 'bin', 'vite.js');

function run(cmd, args, opts = {}) {
  console.log(`> ${cmd} ${args.join(' ')}`);
  const r = spawnSync(cmd, args, {
    stdio: 'inherit',
    shell: false,
    env: process.env,
    ...opts,
  });
  if (r.status !== 0) {
    console.error(`${cmd} 失败（退出码 ${r.status}）`);
    process.exit(r.status ?? 1);
  }
}

function resolveDotnet() {
  const candidates = [];
  if (process.env.DOTNET_ROOT) {
    candidates.push(resolve(process.env.DOTNET_ROOT, process.platform === 'win32' ? 'dotnet.exe' : 'dotnet'));
  }
  if (process.platform === 'win32' && process.env.LOCALAPPDATA) {
    // dnvm installs the active SDK outside PATH. Apphost executables also use
    // DOTNET_ROOT, so keep this runtime visible to both publish and Electron.
    candidates.push(resolve(process.env.LOCALAPPDATA, 'dnvm', 'dn', 'dotnet.exe'));
  }
  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate;
  }
  return 'dotnet';
}

const dotnet = resolveDotnet();
if (dotnet !== 'dotnet') {
  process.env.DOTNET_ROOT = dirname(dotnet);
  if (process.platform === 'win32') process.env.DOTNET_ROOT_X64 = dirname(dotnet);
}
// CI/受限桌面账户可能无法读取用户 NuGet.Config；仓库已带有离线缓存，
// 强制 NuGet 使用它，避免开发启动在发布 Core 前因权限错误失败。
process.env.NUGET_PACKAGES ??= resolve(repo, '.nuget');
// APPDATA is used by NuGet to locate the per-user config. Isolate it only in
// the dotnet child process; changing it in this launcher breaks Node tooling
// (Vite/esbuild resolve paths through the normal user environment).
const dotnetEnv = {
  ...process.env,
  ...(process.platform === 'win32' ? { APPDATA: resolve(repo, '.nuget-appdata') } : {}),
};
const restoreConfig = resolve(repo, '.nuget-appdata', 'NuGet', 'NuGet.Config');

// 开发版使用 framework-dependent apphost：Windows 生成 GrassCore.exe，
// 其他平台生成 GrassCore；CoreBridge 可分别通过 Named Pipe/stdio 接入。
// 目标项目仍以 .NET 10 为准，缺少 SDK 时在这里尽早给出明确错误，而不是
// 等 Electron 启动后才显示“无法连接 GrassCore”。
const publishArgs = [
  'publish', coreProject,
  '-c', 'Debug',
  '-o', coreRundir,
];
// 本地受限环境可提供隔离 NuGet.Config，但它是开发机生成物，不应成为
// 干净检出后的硬依赖；没有该文件时让 dotnet 使用正常的用户配置/源。
if (existsSync(restoreConfig)) publishArgs.push('--configfile', restoreConfig);
run(dotnet, publishArgs, { cwd: repo, env: dotnetEnv });
run(process.execPath, [tscBin, '-p', 'tsconfig.main.json'], { cwd: here });
// Vite 6's bundled config loader can hit esbuild path restrictions on some
// Windows sandboxed profiles; runner mode evaluates the ESM config directly.
run(process.execPath, [viteBin, 'build', '--configLoader', 'runner'], { cwd: here });

if (!existsSync(electronBin)) {
  console.error(
    '未找到 Electron 运行时（sandbox 环境无法下载二进制）。' +
      '渲染层与主进程已构建完成，可在 dist/ 查看产物。',
  );
  process.exit(0);
}
run(electronBin, ['.'], { cwd: here });
