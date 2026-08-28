/**
 * Electron 主进程入口。
 * - Library 主窗口：卡片式虚拟机存档
 * - 显示器窗口：每台运行中的 VM 一个独立窗口（关闭 ≠ 关机）
 * - SPICE 桥：Chromium(WebSocket) ↔ Electron Main(Node TCP) ↔ QEMU SPICE(127.0.0.1)
 */
import { app, BrowserWindow, ipcMain } from 'electron';
import path from 'node:path';
import net from 'node:net';
import http from 'node:http';
import { WebSocketServer } from './ws-lite';
import { CoreBridge } from './core-bridge';

let bridge: CoreBridge | null = null;

async function createBridge(): Promise<CoreBridge> {
  if (bridge) return bridge;
  const isDevPackaged = app.isPackaged
    ? path.join(process.resourcesPath, 'GrassCore', 'GrassCore.exe')
    : path.join(__dirname, '..', '..', 'core-rundir', 'GrassCore.exe');
  bridge = new CoreBridge(isDevPackaged);
  await bridge.ensureRunning();
  return bridge;
}

function createLibraryWindow(): void {
  const win = new BrowserWindow({
    width: 1180,
    height: 760,
    minWidth: 880,
    minHeight: 560,
    backgroundColor: '#f4f9ef',
    title: 'Grass Block VM',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  win.setMenuBarVisibility(false);
  win.loadFile(path.join(__dirname, '..', 'renderer', 'library', 'index.html'));
}

/** 显示器窗口：UI 崩溃窗口可消失，但 Guest 继续运行；重开 UI 后"打开显示器"重新接入。 */
function createDisplayWindow(vmName: string, spicePort: number): BrowserWindow {
  const win = new BrowserWindow({
    width: 1024,
    height: 768,
    title: vmName,
    backgroundColor: '#1d211a',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  win.setMenuBarVisibility(false);
  // 显示器页面通过 URL query 拿到本机 SPICE 端口；实际画面由 spice-client 经 WS 桥接入
  win.loadFile(path.join(__dirname, '..', 'renderer', 'display', 'index.html'), {
    query: { vm: vmName, port: String(spicePort) },
  });
  return win;
}

/**
 * SPICE WebSocket↔TCP 桥（参考 electerm 的做法，不修改 spice-client 源码）：
 * WebSocket 收到的字节直接写 TCP；TCP 数据原样回推 WebSocket。无协议转换。
 */
function startSpiceBridge(spicePort: number): Promise<number> {
  const server = http.createServer();
  const wss = new WebSocketServer(server);
  wss.on('connection', (ws) => {
    const tcp = net.connect({ host: '127.0.0.1', port: spicePort });
    ws.on('message', (data: Buffer) => tcp.write(data));
    tcp.on('data', (chunk: Buffer) => ws.send(chunk));
    const close = () => tcp.destroy();
    ws.on('close', close);
    tcp.on('close', () => ws.close());
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => {
    const addr = server.address();
    resolve(typeof addr === 'object' && addr ? addr.port : 0);
  }));
}

// ---- IPC 表面（preload contextBridge 暴露给渲染进程） ----

ipcMain.handle('core:call', async (_e, method: string, params?: unknown) => {
  const b = await createBridge();
  return b.call(method, params);
});

ipcMain.handle('display:open', async (_e, vmName: string, spicePort: number) => {
  await startSpiceBridge(spicePort);
  createDisplayWindow(vmName, spicePort);
  return true;
});

// 退出策略：UI 退出不杀 GrassCore；Core 在"最后一台 VM 结束且 UI 已退出"时自动退出。
app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.whenReady().then(async () => {
  createLibraryWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createLibraryWindow();
  });
});
