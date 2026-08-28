import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

export default defineConfig({
  plugins: [react()],
  test: {
    // 渲染层（jsdom 语义）与主进程层（node：ws-lite 帧协议等）都纳入
    include: [
      resolve(__dirname, 'src/renderer/**/*.test.{ts,tsx}'),
      resolve(__dirname, 'src/main/**/*.test.ts'),
    ],
    environment: 'node',
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
