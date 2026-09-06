/**
 * preload：contextBridge 暴露受控 API。
 * 渲染进程拿不到 Node/Electron 能力，只能调用这里列出的方法（QEMU 不可见原则）。
 */
import { contextBridge, ipcRenderer } from 'electron';

contextBridge.exposeInMainWorld('grassvm', {
  /** 调用 GrassCore JSON-RPC 方法（唯一的数据通道） */
  coreCall: (method: string, params?: unknown) => ipcRenderer.invoke('core:call', method, params),
  /** 打开独立显示器窗口（不杀 QEMU、不影响 VM 运行） */
  openDisplay: (vmName: string, spicePort: number, packagePath: string) =>
    ipcRenderer.invoke('display:open', vmName, spicePort, packagePath),
  /** 读取显示器窗口 URL 参数（vm 名 / SPICE 端口） */
  displayQuery: () =>
    typeof window !== 'undefined'
      ? Object.fromEntries(new URLSearchParams(window.location.search))
      : {},
  /** 显示器窗口原生全屏，连同标题栏/任务栏一起切换。 */
  setDisplayFullscreen: (enabled: boolean) =>
    ipcRenderer.invoke('display:set-fullscreen', enabled) as Promise<boolean>,
  /** 接收 Windows 标题栏关闭或 Alt+F4 请求，由渲染层展示统一的电源选择。 */
  onDisplayCloseRequested: (callback: () => void) => {
    const listener = () => callback();
    ipcRenderer.on('display:close-requested', listener);
    return () => ipcRenderer.removeListener('display:close-requested', listener);
  },
  /** 在渲染层完成关闭选择后，允许主进程真正关闭显示器窗口。 */
  closeDisplayWindow: () => ipcRenderer.invoke('display:close-confirmed') as Promise<boolean>,
  /** 原生文件选择（导入档案 / 安装镜像等；QEMU 不可见原则不受影响） */
  pickOpenFile: (filterName: string, extensions: string[]) =>
    ipcRenderer.invoke('dialog:pickOpen', filterName, extensions) as Promise<string | null>,
  /** 原生保存位置选择（导出档案） */
  pickSaveFile: (defaultName: string, filterName: string, extensions: string[]) =>
    ipcRenderer.invoke('dialog:pickSave', defaultName, filterName, extensions) as Promise<string | null>,
  /** 原生目录选择（首次运行设置虚拟机存档位置） */
  pickDirectory: () => ipcRenderer.invoke('dialog:pickDirectory') as Promise<string | null>,
  /** 默认存档位置（主进程解析：Documents/Grass Block VM） */
  defaultLibraryDir: () => ipcRenderer.invoke('paths:defaultLibraryDir') as Promise<string>,
});
