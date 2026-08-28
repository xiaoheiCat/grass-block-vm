import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

export default defineConfig({
  plugins: [react()],
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
