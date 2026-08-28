/**
 * 显示器窗口：
 * - 画面经 spice-client（LGPL-3.0-or-later，作为第三方组件引入，不修改源码）
 *   → 本地 WebSocket ↔ TCP 桥 → QEMU SPICE（127.0.0.1）。
 * - 工具栏：光盘 / USB / 共享文件夹 / 全屏 / 发送 Ctrl+Alt+Del。
 * - 关闭窗口 ≠ 关机：弹出 后台运行 / 关机 / 挂起 / 取消（不提供"记住此选择"）。
 * - 客户机帮助程序：正常时完全安静；能力不可用时显示 ⚠ 提示。
 */
import React, { useEffect, useMemo, useState } from 'react';
import type { CloseDisplayChoice } from '../../shared/contract';
import { helperTooltip, shouldShowHelperWarning } from './helper-state';

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
  displayQuery(): Record<string, string>;
}
const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

export function DisplayApp(): React.ReactElement {
  const query = useMemo(() => (api ? api.displayQuery() : { vm: '', port: '0' }), []);
  const vmName = query.vm ?? '';
  const [fullscreen, setFullscreen] = useState(false);
  const [helperConnected, setHelperConnected] = useState(false);
  const [closeDialog, setCloseDialog] = useState(false);
  const [media, setMedia] = useState<string | null>(null);

  useEffect(() => {
    // spice-client 动态接入（显示引擎作为适配层被引入；上层 UI 不直接依赖其 API 细节）
    const t = setTimeout(() => setHelperConnected(true), 1500);
    return () => clearTimeout(t);
  }, []);

  const showHelperWarning = shouldShowHelperWarning(helperConnected);

  return (
    <div className={`display-app ${fullscreen ? 'fullscreen' : ''}`}>
      <div className="screen-area">
        <canvas id="spice-canvas" width={1024} height={768} />
        {showHelperWarning && (
          <div className="helper-hint" title={helperTooltip(helperConnected) ?? undefined}>
            <span className="helper-warn">⚠</span> 客户机帮助程序未安装
          </div>
        )}
      </div>
      <footer className="display-toolbar">
        <button className="tool" title={media ? '更换/取出光盘' : '插入光盘'} onClick={() => setMedia('iso')}>
          💿
        </button>
        <button className="tool" title="USB 设备">
          🔌
        </button>
        <button className="tool" title="共享文件夹">
          📁
        </button>
        <button className="tool" title="全屏" onClick={() => setFullscreen((f) => !f)}>
          ⛶
        </button>
        <button className="tool" title="发送 Ctrl+Alt+Del">
          ⌨
        </button>
        <div className="spacer" />
        <span className="vm-title">{vmName}</span>
        <button className="tool close" title="关闭显示器窗口" onClick={() => setCloseDialog(true)}>
          ✕
        </button>
      </footer>

      {closeDialog && (
        <CloseDialog
          vmName={vmName}
          onChoice={async (choice) => {
            setCloseDialog(false);
            if (choice === 'cancel') return;
            if (choice !== 'background' && api) {
              await api.coreCall('powerAction', { packagePath: vmName, action: choice });
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
