/**
 * 开发启动脚本（Node，无额外依赖）：
 * 1. 编译 Electron 主进程（tsc → dist/main）
 * 2. 构建渲染层（vite build → dist/renderer）
 * 3. 启动 Electron（GrassCore 路径指向 core-rundip；开发时可放一个本地构建产物）
 *
 * 说明：热更新模式（pnpm run dev:web，vite dev server）用于纯渲染层迭代；
 * 主进程/Preload 改动需要重启本脚本。产品目标平台是 Windows；macOS 上仅做逻辑联调。
 */
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const electronBin = process.platform === 'darwin'
  ? resolve(here, 'node_modules', 'electron', 'dist', 'Electron.app', 'Contents', 'MacOS', 'Electron')
  : process.platform === 'win32'
    ? resolve(here, 'node_modules', 'electron', 'dist', 'electron.exe')
    : resolve(here, 'node_modules', 'electron', 'dist', 'electron');

function run(cmd, args, opts = {}) {
  console.log(`> ${cmd} ${args.join(' ')}`);
  const r = spawnSync(cmd, args, { stdio: 'inherit', shell: process.platform === 'win32', ...opts });
  if (r.status !== 0) {
    console.error(`${cmd} 失败（退出码 ${r.status}）`);
    process.exit(r.status ?? 1);
  }
}

run('pnpm', ['exec', 'tsc', '-p', 'tsconfig.main.json'], { cwd: here });
run('pnpm', ['exec', 'vite', 'build'], { cwd: here });

if (!existsSync(electronBin)) {
  console.error(
    '未找到 Electron 运行时（sandbox 环境无法下载二进制）。' +
      '渲染层与主进程已构建完成，可在 dist/ 查看产物。',
  );
  process.exit(0);
}
run(electronBin, ['.'], { cwd: here });
