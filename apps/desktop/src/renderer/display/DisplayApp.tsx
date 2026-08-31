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
import { helperTooltip, shouldShowHelperWarning } from './helper-state';

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
  displayQuery(): Record<string, string>;
  pickOpenFile(filterName: string, extensions: string[]): Promise<string | null>;
}
const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

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
  const helperConnected = false;
  const [closeDialog, setCloseDialog] = useState(false);
  const [helperWarning, setHelperWarning] = useState<string | null>(null);
  const [cds, setCds] = useState<CdDrive[]>([]);
  const [cdMenu, setCdMenu] = useState(false);

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

  const showHelperWarning = shouldShowHelperWarning(helperConnected);

  return (
    <div className={`display-app ${fullscreen ? 'fullscreen' : ''}`}>
      <div className="screen-area">
        {/* spice-client 适配层接入点：连接 ws://127.0.0.1:{port}/{token}
            （token 是桥的一次性通行证——SPICE 禁票运行，路径不符的连接会被桥拒绝） */}
        <canvas
          id="spice-canvas"
          width={1024}
          height={768}
          data-spice-port={query.port}
          data-spice-token={query.token}
        />
        {showHelperWarning && (
          <div className="helper-hint" title={helperTooltip(helperConnected) ?? undefined}>
            <span className="helper-warn">⚠</span> 客户机帮助程序未安装
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
        <button className="tool" title="全屏" onClick={() => setFullscreen((f) => !f)}>
          ⛶
        </button>
        <button className="tool" title="发送 Ctrl+Alt+Del（暂未接入）" disabled>
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
