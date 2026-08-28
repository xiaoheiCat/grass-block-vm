/**
 * Electron 主进程入口。
 * - Library 主窗口：卡片式虚拟机存档
 * - 显示器窗口：每台运行中的 VM 一个独立窗口（关闭 ≠ 关机）
 * - SPICE 桥：Chromium(WebSocket) ↔ Electron Main(Node TCP) ↔ QEMU SPICE(127.0.0.1)
 */
import { app, BrowserWindow, dialog, ipcMain } from 'electron';
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
function createDisplayWindow(vmName: string, spicePort: number, packagePath: string, token: string): BrowserWindow {
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
    // 电源动作需要包路径（Core 按包路径寻址）；名称仅用于展示
    query: { vm: vmName, port: String(spicePort), path: packagePath, token },
  });
  return win;
}

/**
 * SPICE WebSocket↔TCP 桥（参考 electerm 的做法，不修改 spice-client 源码）：
 * WebSocket 收到的字节直接写 TCP；TCP 数据原样回推 WebSocket。无协议转换。
 */
function startSpiceBridge(
  spicePort: number,
): Promise<{ server: http.Server; wss: WebSocketServer }> {
  const server = http.createServer();
  const wss = new WebSocketServer(server);
  wss.on('connection', (ws) => {
    const tcp = net.connect({ host: '127.0.0.1', port: spicePort });
    ws.on('message', (data: Buffer) => tcp.write(data));
    tcp.on('data', (chunk: Buffer) => ws.send(chunk));
    // 连不上 SPICE 端口：断开 WS 让前端提示，而不是让主进程抛未处理错误
    tcp.on('error', () => ws.close());
    const close = () => tcp.destroy();
    ws.on('close', close);
    tcp.on('close', () => ws.close());
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => {
    resolve({ server, wss });
  }));
}

// ---- IPC 表面（preload contextBridge 暴露给渲染进程） ----

ipcMain.handle('core:call', async (_e, method: string, params?: unknown) => {
  const b = await createBridge();
  return b.call(method, params);
});

// 每个显示器窗口一个 SPICE 桥；窗口关闭时拆除（否则每次开窗泄漏一个 HTTP 服务器）
const displayBridges = new Map<number, { server: http.Server; wss: WebSocketServer }>();
/** 已打开的显示器窗口（包路径 → 窗口）：一台 VM 只允许一个显示器 */
const openDisplays = new Map<number, string>();

// 默认存档位置：用户 Documents 下 Grass Block VM（渲染层沙箱拿不到 USERPROFILE，
// 只能由主进程解析后交给首运行引导卡片）
ipcMain.handle('paths:defaultLibraryDir', () =>
  path.join(app.getPath('documents'), 'Grass Block VM'),
);

ipcMain.handle('display:open', async (_e, vmName: string, spicePort: number, packagePath: string) => {
  // 一台 VM 同时只允许一个显示器窗口：已开则聚焦（重复桥也会堆叠资源）
  const existing = [...BrowserWindow.getAllWindows()].find(
    (w) => (w.getTitle() === vmName || openDisplays.get(w.id) === packagePath) && !w.isDestroyed(),
  );
  if (existing) {
    existing.focus();
    return true;
  }
  const bridge = await startSpiceBridge(spicePort);
  const win = createDisplayWindow(vmName, spicePort, packagePath, bridge.wss.token);
  openDisplays.set(win.id, packagePath);
  displayBridges.set(win.id, bridge);
  win.on('closed', () => {
    openDisplays.delete(win.id);
    const b = displayBridges.get(win.id);
    if (b) {
      displayBridges.delete(win.id);
      for (const client of b.wss.clients) client.terminate();
      b.server.close();
    }
  });
  return true;
});

// 文件选择对话框：导入（.zip/.ova/.ovf）、导出保存位置、安装镜像选择共用。
ipcMain.handle('dialog:pickOpen', async (_e, filterName: string, extensions: string[]) => {
  const r = await dialog.showOpenDialog({ properties: ['openFile'], filters: [{ name: filterName, extensions }] });
  return r.canceled ? null : r.filePaths[0];
});

ipcMain.handle('dialog:pickDirectory', async () => {
  const r = await dialog.showOpenDialog({ properties: ['openDirectory', 'createDirectory'] });
  return r.canceled ? null : r.filePaths[0];
});
ipcMain.handle('dialog:pickSave', async (_e, defaultName: string, filterName: string, extensions: string[]) => {
  const r = await dialog.showSaveDialog({ defaultPath: defaultName, filters: [{ name: filterName, extensions }] });
  return r.canceled ? null : r.filePath;
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
  // 自启动：应用启动时按宿主级清单串行拉起（间隔 10s 由 Core 钳制），失败项由 Core 通知
  try {
    const b = await createBridge();
    b.call('runAutostart', {}).catch((e: unknown) => console.error('[autostart]', e));
  } catch (e) {
    console.error('[autostart] bridge unavailable:', e);
  }
});
