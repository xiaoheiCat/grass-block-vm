/**
 * 显示器窗口：
 * - 画面经 spice-client（LGPL-3.0-or-later，作为第三方组件引入，不修改源码）
 *   → 本地 WebSocket ↔ TCP 桥 → QEMU SPICE（127.0.0.1）。
 * - 工具栏：光盘 / USB / 共享文件夹 / 全屏 / 发送 Ctrl+Alt+Del。
 * - 关闭窗口 ≠ 关机：弹出 后台运行 / 关机 / 挂起 / 取消（不提供"记住此选择"）。
 * - 客户机帮助程序：正常时完全安静；能力不可用时显示 ⚠ 提示。
 */
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import type { CloseDisplayChoice } from '../../shared/contract';
import { helperTooltip, shouldShowHelperWarning, type HelperState } from './helper-state';

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
  displayQuery(): Record<string, string>;
  pickOpenFile(filterName: string, extensions: string[]): Promise<string | null>;
}
const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

type SpiceConnection = {
  stop(): void;
  agent_connected?: boolean;
  file_xfer_start?: (file: File) => void;
};

declare global {
  interface Window {
    SpiceMainConn?: new (options: {
      uri: string;
      password?: string;
      screen_id?: string;
      message_id?: string;
      onerror?: (error: Error) => void;
      onagent?: (connection: unknown) => void;
    }) => SpiceConnection;
    /** spice-html5 的 resize/file-transfer 适配层使用的全局连接与事件函数。 */
    spice_connection?: SpiceConnection;
    handle_resize?: (event: Event) => void;
    handle_file_dragover?: (event: DragEvent) => void;
    handle_file_drop?: (event: DragEvent) => void;
  }
}

const SPICE_SCRIPTS = [
  'spicearraybuffer.js', 'enums.js', 'atKeynames.js', 'utils.js', 'png.js', 'lz.js', 'quic.js',
  'bitmap.js', 'spicedataview.js', 'spicetype.js', 'spicemsg.js', 'wire.js', 'spiceconn.js',
  'display.js', 'main.js', 'inputs.js', 'webm.js', 'playback.js', 'simulatecursor.js', 'cursor.js',
  'thirdparty/jsbn.js', 'thirdparty/rsa.js', 'thirdparty/prng4.js', 'thirdparty/rng.js',
  'thirdparty/sha1.js', 'ticket.js', 'resize.js', 'filexfer.js',
] as const;
let spiceClientPromise: Promise<void> | null = null;

function loadSpiceClient(): Promise<void> {
  if (window.SpiceMainConn) return Promise.resolve();
  if (spiceClientPromise) return spiceClientPromise;
  // spice-html5 自带的 spice.css 面向独立示例页，包含全局 `* { margin: 0 }`
  // 和 body 背景/字体规则；直接注入会覆盖 Grass Block VM 的工具栏与主题。
  // 协议脚本本身不依赖这些样式，显示器所需样式由应用 styles.css 提供。
  spiceClientPromise = SPICE_SCRIPTS.reduce((chain, file) => chain.then(() => new Promise<void>((resolve, reject) => {
    const script = document.createElement('script');
    script.src = new URL(`../spice-html5/${file}`, document.baseURI).href;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`无法加载 SPICE 客户端资源：${file}`));
    document.head.appendChild(script);
  })), Promise.resolve());
  return spiceClientPromise;
}

interface ConfigView {
  devices: Array<{ deviceId: string; deviceType: string; isoPath?: string | null }>;
}

interface CdDrive {
  deviceId: string;
  isoPath: string | null;
}

export function DisplayApp(): React.ReactElement {
  const query = useMemo(() => (api ? api.displayQuery() : { vm: '', port: '0' }), []);
  const vmName = query.vm ?? '';
  const [fullscreen, setFullscreen] = useState(false);
  const [helperState, setHelperState] = useState<HelperState>('detecting');
  const [guestAgentConnected, setGuestAgentConnected] = useState(false);
  const [spiceError, setSpiceError] = useState<string | null>(null);
  const [closeDialog, setCloseDialog] = useState(false);
  const [helperWarning, setHelperWarning] = useState<string | null>(null);
  const [cds, setCds] = useState<CdDrive[]>([]);
  const [cdMenu, setCdMenu] = useState(false);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setFullscreen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  // 光驱实况（💿 按钮）：OVF 导入可能带来多个光驱——按设备逐个管理，不静默只动第一个
  const packagePath = query.path ?? '';
  const reloadCds = useCallback(async () => {
    if (!api || !packagePath) return;
    try {
      const c = await api.coreCall<ConfigView>('getConfig', { packagePath });
      setCds(c.devices.filter((d) => d.deviceType === 'cdrom')
        .map((d) => ({ deviceId: d.deviceId, isoPath: d.isoPath ?? null })));
    } catch {
      /* 状态未知不阻塞窗口 */
    }
  }, [api, packagePath]);

  useEffect(() => {
    void reloadCds();
  }, [reloadCds]);

  useEffect(() => {
    let cancelled = false;
    const check = async () => {
      if (!api || !packagePath) { setHelperState('stopped'); return; }
      try {
        const status = await api.coreCall<{ state?: HelperState; connected?: boolean }>('getHelperStatus', { packagePath });
        if (!cancelled)
          setHelperState(status.connected ? 'connected' : (status.state ?? 'disconnected'));
      } catch {
        if (!cancelled) setHelperState('disconnected');
      }
    };
    void check();
    const timer = window.setInterval(check, 3000);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, [api, packagePath]);

  useEffect(() => {
    let disposed = false;
    let conn: SpiceConnection | undefined;
    const spiceGlobals = window;
    const screen = document.getElementById('spice-screen');
    const onResize = (event: Event) => {
      if (spiceGlobals.spice_connection && spiceGlobals.handle_resize)
        spiceGlobals.handle_resize(event);
    };
    const onDragOver = (event: DragEvent) => {
      spiceGlobals.handle_file_dragover?.(event);
    };
    const onDrop = (event: DragEvent) => {
      spiceGlobals.handle_file_drop?.(event);
    };
    window.addEventListener('resize', onResize);
    screen?.addEventListener('dragover', onDragOver);
    screen?.addEventListener('drop', onDrop);
    void loadSpiceClient().then(() => {
      const Client = window.SpiceMainConn;
      if (disposed || !query.port || !Client) return;
      const uri = `ws://127.0.0.1:${query.port}${query.token ? `/${encodeURIComponent(query.token)}` : ''}`;
      try {
        conn = new Client({
          uri,
          password: query.password ?? '',
          screen_id: 'spice-screen',
          message_id: 'spice-message',
          onerror: (error) => setSpiceError(error.message),
          onagent: () => {
            setGuestAgentConnected(true);
          },
        });
        // spice-html5 的 resize.js/filexfer.js 通过这个全局引用找到连接；
        // 不设置它时画面虽然能连上，但动态分辨率和拖放传文件会静默失效。
        spiceGlobals.spice_connection = conn;
        setSpiceError(null);
      } catch (error) {
        setSpiceError(error instanceof Error ? error.message : String(error));
      }
    }).catch((error: unknown) => {
      if (!disposed) setSpiceError(error instanceof Error ? error.message : String(error));
    });
    const agentPoll = window.setInterval(() => {
      if (!conn || typeof conn.agent_connected !== 'boolean') return;
      if (conn.agent_connected) {
        setGuestAgentConnected(true);
      } else {
        setGuestAgentConnected(false);
      }
    }, 1000);
    return () => {
      disposed = true;
      window.clearInterval(agentPoll);
      window.removeEventListener('resize', onResize);
      screen?.removeEventListener('dragover', onDragOver);
      screen?.removeEventListener('drop', onDrop);
      if (spiceGlobals.spice_connection === conn) delete spiceGlobals.spice_connection;
      setGuestAgentConnected(false);
      try { conn?.stop(); } catch { /* 客户端关闭幂等 */ }
    };
  }, [query.port, query.token]);

  /** action = 'insert'（弹文件选择）或 null（取出介质） */
  const changeMedium = useCallback(async (deviceId: string, action: 'insert' | null) => {
    if (!api) return;
    try {
      let iso: string | null = null;
      if (action === 'insert') {
        iso = await api.pickOpenFile('光盘镜像（ISO）', ['iso']);
        if (!iso) return; // 用户取消了选择
      }
      await api.coreCall('changeMedium', { packagePath, deviceId, isoPath: iso });
      await reloadCds();
      setHelperWarning(null);
    } catch (e) {
      setHelperWarning(e instanceof Error ? e.message : String(e));
    }
  }, [api, packagePath, reloadCds]);

  const showHelperWarning = shouldShowHelperWarning(helperState, guestAgentConnected);

  return (
    <div className={`display-app ${fullscreen ? 'fullscreen' : ''}`}>
      <div className="screen-area">
        {/* spice-client 适配层接入点：连接 ws://127.0.0.1:{port}/{token}
            （token 是桥的一次性通行证——SPICE 禁票运行，路径不符的连接会被桥拒绝） */}
        <div id="spice-screen" data-spice-port={query.port} data-spice-token={query.token} />
        <div id="spice-message" className="spice-message" />
        <div id="spice-xfer-area" className="spice-xfer-area" />
        {spiceError && <div className="helper-hint spice-error">{spiceError}</div>}
        {showHelperWarning && (
          <div className="helper-hint" title={helperTooltip(helperState, guestAgentConnected) ?? undefined}>
            <span className="helper-warn">⚠</span> {helperState === 'detecting' || (helperState === 'connected' && !guestAgentConnected) ? '正在检测客户机帮助程序…' : '客户机帮助程序不可用'}
          </div>
        )}
      </div>
      <footer className="display-toolbar">
        <button
          className="tool"
          title="插入 / 更换 / 取出光盘"
          onClick={() => {
            if (cds.length === 1 && !cds[0].isoPath) void changeMedium(cds[0].deviceId, 'insert');
            else if (cds.length === 0) setHelperWarning('这台虚拟机没有光驱设备。');
            else setCdMenu(true);
          }}        >
          💿
        </button>
        {cdMenu && (
          <div className="cd-menu">
            {cds.map((cd, i) => (
              <div className="cd-drive" key={cd.deviceId}>
                <span className="hint">
                  光驱 #{i + 1}：{cd.isoPath ? cd.isoPath.split(/[\\/]/).pop() : '空'}
                </span>
                <button
                  className="btn-ghost"
                  onClick={() => { setCdMenu(false); void changeMedium(cd.deviceId, 'insert'); }}
                >
                  {cd.isoPath ? '更换光盘…' : '插入光盘…'}
                </button>
                {cd.isoPath && (
                  <button
                    className="btn-ghost"
                    onClick={() => { setCdMenu(false); void changeMedium(cd.deviceId, null); }}
                  >
                    取出
                  </button>
                )}
              </div>
            ))}
            <button className="btn-ghost" onClick={() => setCdMenu(false)}>
              取消
            </button>
          </div>
        )}
        <button className="tool" title="USB 设备（暂未接入）" disabled>
          🔌
        </button>
        <button className="tool" title="共享文件夹（暂未接入）" disabled>
          📁
        </button>
        <button className="tool" title={fullscreen ? '退出全屏（Esc）' : '全屏'} onClick={() => setFullscreen((f) => !f)}>
          ⛶
        </button>
        <button
          className="tool"
          title="发送 Ctrl+Alt+Del"
          onClick={async () => {
            if (!api || !packagePath) return;
            try { await api.coreCall('sendCtrlAltDel', { packagePath }); }
            catch (e) { setHelperWarning(e instanceof Error ? e.message : String(e)); }
          }}
        >
          ⌨
        </button>
        <div className="spacer" />
        <span className="vm-title">{vmName}</span>
        <button className="tool close" title="关闭显示器窗口" onClick={() => setCloseDialog(true)}>
          ✕
        </button>
      </footer>
      {helperWarning && <div className="helper-hint">{helperWarning}</div>}

      {closeDialog && (
        <CloseDialog
          vmName={vmName}
          onChoice={async (choice) => {
            setCloseDialog(false);
            if (choice === 'cancel') return;
            if (choice !== 'background' && api) {
              // 电源动作按包路径寻址（名称仅展示用）；失败要提示而不是静默挂掉窗口
              try {
                await api.coreCall('powerAction', { packagePath: query.path ?? vmName, action: choice });
              } catch (e) {
                setHelperWarning(e instanceof Error ? e.message : String(e));
                return;
              }
            }
            window.close();
          }}
        />
      )}
    </div>
  );
}

function CloseDialog(props: {
  vmName: string;
  onChoice(choice: CloseDisplayChoice): void;
}): React.ReactElement {
  return (
    <div className="modal-backdrop">
      <div className="dialog">
        <h2>要怎么处理“{props.vmName}”？</h2>
        <p>关闭这个窗口不会影响虚拟机本身。</p>
        <div className="dialog-actions vertical">
          <button className="btn-primary" onClick={() => props.onChoice('background')}>
            在后台运行
          </button>
          <button className="btn-ghost" onClick={() => props.onChoice('shutdown')}>
            关机
          </button>
          <button className="btn-ghost" onClick={() => props.onChoice('suspend')}>
            挂起
          </button>
          <button className="btn-ghost" onClick={() => props.onChoice('cancel')}>
            取消
          </button>
        </div>
      </div>
    </div>
  );
}
