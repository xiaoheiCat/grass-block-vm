import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

export default defineConfig({
  plugins: [react()],
  server: {
    allowedHosts: ['.monkeycode-ai.online'],
  },
  test: {
    // 渲染层使用 jsdom，主进程层使用 node。
    include: [
      resolve(__dirname, 'src/renderer/**/*.test.{ts,tsx}'),
      resolve(__dirname, 'src/main/**/*.test.ts'),
    ],
    environment: 'node',
    environmentMatchGlobs: [
      ['src/renderer/**/*.test.{ts,tsx}', 'jsdom'],
      ['src/main/**/*.test.ts', 'node'],
    ],
  },
  base: './',
  root: resolve(__dirname, 'src/renderer'),
  build: {
    outDir: resolve(__dirname, 'dist/renderer'),
    emptyOutDir: true,
    rollupOptions: {
      input: {
        library: resolve(__dirname, 'src/renderer/library/index.html'),
        display: resolve(__dirname, 'src/renderer/display/index.html'),
      },
    },
  },
});
