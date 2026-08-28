/**
 * preload：contextBridge 暴露受控 API。
 * 渲染进程拿不到 Node/Electron 能力，只能调用这里列出的方法（QEMU 不可见原则）。
 */
import { contextBridge, ipcRenderer } from 'electron';

contextBridge.exposeInMainWorld('grassvm', {
  /** 调用 GrassCore JSON-RPC 方法（唯一的数据通道） */
  coreCall: (method: string, params?: unknown) => ipcRenderer.invoke('core:call', method, params),
  /** 打开独立显示器窗口（不杀 QEMU、不影响 VM 运行） */
  openDisplay: (vmName: string, spicePort: number) => ipcRenderer.invoke('display:open', vmName, spicePort),
  /** 读取显示器窗口 URL 参数（vm 名 / SPICE 端口） */
  displayQuery: () =>
    typeof window !== 'undefined'
      ? Object.fromEntries(new URLSearchParams(window.location.search))
      : {},
});
