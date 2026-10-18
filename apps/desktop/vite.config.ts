import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Vite 6 may evaluate this config as native ESM (not the historical CJS
// bundle), where __dirname is unavailable. Keep all paths anchored to the
// config file so both loaders behave identically on Windows and CI.
const configDir = resolve(fileURLToPath(new URL('.', import.meta.url)));

export default defineConfig({
  plugins: [react()],
  server: {
    allowedHosts: ['.monkeycode-ai.online'],
  },
  test: {
    // Vitest 的测试根目录与 Vite 的多入口 renderer 根目录分离，
    // 否则在 Windows 上绝对 glob 会被解析到 renderer/renderer，导致“无测试文件”。
    root: configDir,
    pool: 'forks',
    poolOptions: { forks: { singleFork: true } },
    // 渲染层使用 jsdom，主进程层使用 node。
    include: [
      'src/renderer/**/*.test.{ts,tsx}',
      'src/main/**/*.test.ts',
    ],
    environment: 'node',
    environmentMatchGlobs: [
      ['src/renderer/**/*.test.{ts,tsx}', 'jsdom'],
      ['src/main/**/*.test.ts', 'node'],
    ],
  },
  base: './',
  root: resolve(configDir, 'src/renderer'),
  build: {
    outDir: resolve(configDir, 'dist/renderer'),
    emptyOutDir: true,
    rollupOptions: {
      input: {
        library: resolve(configDir, 'src/renderer/library/index.html'),
        display: resolve(configDir, 'src/renderer/display/index.html'),
      },
    },
  },
});
