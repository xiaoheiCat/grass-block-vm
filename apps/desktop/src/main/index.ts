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

// UI 也收敛为单实例：Core 端虽然有跨进程 mutex，但第二个 Electron 若继续
// 留下会产生重复 Library/Display 窗口和重复轮询。第二次启动只把已有窗口置前。
const isPrimaryInstance = app.requestSingleInstanceLock();
if (!isPrimaryInstance) app.quit();
else app.on('second-instance', () => {
  const win = BrowserWindow.getAllWindows().find((candidate) => !candidate.isDestroyed());
  win?.show();
  win?.focus();
});

let bridge: CoreBridge | null = null;
// 单飞：并发首调（启动风暴里 statusList + 每个 VM 卡片的并行 RPC）共享同一次
// 连接尝试——否则会孵出多个 GrassCore 进程
let bridgeConnecting: Promise<CoreBridge> | null = null;

async function createBridge(): Promise<CoreBridge> {
  if (bridge) return bridge;
  if (bridgeConnecting) return bridgeConnecting;
  bridgeConnecting = (async () => {
    const isDevPackaged = app.isPackaged
      ? path.join(process.resourcesPath, 'GrassCore', 'GrassCore.exe')
      : (process.env.GRASSCORE_DEV_EXE
        ?? path.join(__dirname, '..', '..', 'core-rundir', process.platform === 'win32' ? 'GrassCore.exe' : 'GrassCore'));
    const b = new CoreBridge(isDevPackaged);
    await b.ensureRunning();
    return b;
  })();
  try {
    bridge = await bridgeConnecting;
    return bridge;
  } finally {
    bridgeConnecting = null;
  }
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
function createDisplayWindow(
  vmName: string,
  spicePort: number,
  spicePassword: string,
  bridgePort: number,
  packagePath: string,
  token: string,
): BrowserWindow {
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
  // 桥端口（WS 接入点）与 SPICE 端口（127.0.0.1 直连、不暴露给页面）分开传：
  // 前者给 spice-client 连接用，后者仅作展示/诊断
  win.loadFile(path.join(__dirname, '..', 'renderer', 'display', 'index.html'), {
    // 电源动作需要包路径（Core 按包路径寻址）；名称仅用于展示
    query: { vm: vmName, port: String(bridgePort), spicePort: String(spicePort), password: spicePassword, path: packagePath, token },
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
  const result = await b.call(method, params);
  // 安装器以管理员权限运行，不能在安装阶段写 HKCU（可能属于提供凭据的
  // 另一个用户）。只有实际登录用户在 Electron 中修改 VM 自启动时，才同步
  // 该用户自己的登录项。
  if (method === 'setAutostart' || method === 'removeAutostart') {
    try {
      const status = await b.call<{ enabled?: boolean }>('hasAutostart', {});
      app.setLoginItemSettings({ openAtLogin: status.enabled === true, args: ['--autostart'] });
    } catch (error) {
      console.error('[autostart] login item sync failed:', error);
    }
  }
  return result;
});

// 每个显示器窗口一个 SPICE 桥；窗口关闭时拆除（否则每次开窗泄漏一个 HTTP 服务器）
const displayBridges = new Map<number, { server: http.Server; wss: WebSocketServer }>();
/** 已打开的显示器窗口（包路径 → 窗口）：一台 VM 只允许一个显示器 */
const openDisplays = new Map<number, string>();
/** 已经由渲染层完成电源选择的关闭请求，绕过下一次 close 事件。 */
const displayCloseAllowed = new Set<number>();

// 默认存档位置：用户 Documents 下 Grass Block VM（渲染层沙箱拿不到 USERPROFILE，
// 只能由主进程解析后交给首运行引导卡片）
ipcMain.handle('paths:defaultLibraryDir', () =>
  path.join(app.getPath('documents'), 'Grass Block VM'),
);

ipcMain.handle('display:set-fullscreen', (event, enabled: boolean) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win || win.isDestroyed()) throw new Error('显示器窗口已关闭。');
  win.setFullScreen(Boolean(enabled));
  return win.isFullScreen();
});

ipcMain.handle('display:close-confirmed', (event) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win || win.isDestroyed()) return false;
  displayCloseAllowed.add(win.id);
  win.close();
  return true;
});

/** 正在打开中的显示器（包路径集合）：双击"打开显示器"会并发进来两次 invoke，
 *  异步桥启动让两个都通过"没有已开窗口"的检查——各开一扇窗+各起一座桥。
 *  同步占位把这个竞态关掉。 */
const openingDisplays = new Set<string>();

ipcMain.handle('display:open', async (_e, vmName: string, _spicePort: number, packagePath: string) => {
  // 一台 VM 同时只允许一个显示器窗口：已开则聚焦。只按包路径匹配——按标题匹配
  // 会撞上重名 VM 或库主窗口（标题也是 VM 名时聚焦错窗口，显示器"打不开"）
  const existing = [...BrowserWindow.getAllWindows()].find(
    (w) => openDisplays.get(w.id) === packagePath && !w.isDestroyed(),
  );
  if (existing) {
    existing.focus();
    return true;
  }
  if (openingDisplays.has(packagePath)) return true; // 并发的第二次点击：等第一路开完
  openingDisplays.add(packagePath);
  try {
    const b = await createBridge();
    const display = await b.call<{ spicePort: number; spicePassword: string }>('getDisplayInfo', { packagePath });
    const spicePort = display.spicePort;
    if (!Number.isInteger(spicePort) || spicePort < 1 || spicePort > 65535)
      throw new Error('GrassCore 返回了无效的 SPICE 端口。');
    const bridge = await startSpiceBridge(spicePort);
    const bridgePort = (bridge.server.address() as net.AddressInfo).port;
    const win = createDisplayWindow(vmName, spicePort, display.spicePassword, bridgePort, packagePath, bridge.wss.token);
    openDisplays.set(win.id, packagePath);
    displayBridges.set(win.id, bridge);
    win.on('close', (event) => {
      if (displayCloseAllowed.delete(win.id)) return;
      // 标题栏 X / Alt+F4 必须与工具栏关闭按钮走同一套四选一流程，
      // 否则用户会在没有提示的情况下把显示器关成后台运行。
      event.preventDefault();
      win.webContents.send('display:close-requested');
    });
    win.on('closed', () => {
      openDisplays.delete(win.id);
      displayCloseAllowed.delete(win.id);
      const b = displayBridges.get(win.id);
      if (b) {
        displayBridges.delete(win.id);
        for (const client of b.wss.clients) client.terminate();
        b.server.close();
      }
    });
    return true;
  } finally {
    openingDisplays.delete(packagePath);
  }
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
  if (!isPrimaryInstance) return;
  createLibraryWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createLibraryWindow();
  });
  // 自启动只在【开机登录触发的启动】跑（实际用户的登录项带 --autostart）。
  // 用户手动打开应用不拉清单——否则每次打开库都把所有 autostart VM 开一遍
  if (!process.argv.includes('--autostart')) return;
  try {
    const b = await createBridge();
    await b.call('runAutostart', {});
    app.quit();
  } catch (e) {
    console.error('[autostart] bridge unavailable:', e);
    app.quit();
  }
});
