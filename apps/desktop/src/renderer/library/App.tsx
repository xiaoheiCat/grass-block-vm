/**
 * Library 主界面：纯卡片视图（无树、无列表、无搜索筛选——1.0 明确不做）。
 * 交互原则：正常状态安静；界面展示等于当前事实；卡片单一主操作。
 */
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import type { OsProfileDto, VmSummary } from '../../shared/contract';
import {
  osBadge,
  powerMenuItems,
  primaryAction,
  primaryActionLabel,
  stateLabel,
  vmSubtitle,
} from './vm-state';
import { CreateWizard } from './CreateWizard';
import { VmSettings } from './VmSettings';

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
  openDisplay(vmName: string, spicePort: number): Promise<boolean>;
}

const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

export function App(): React.ReactElement {
  const [vms, setVms] = useState<VmSummary[]>([]);
  const [profiles, setProfiles] = useState<OsProfileDto[]>([]);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [settingsVm, setSettingsVm] = useState<VmSummary | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!api) return;
    try {
      const lib = await api.coreCall<{ vms: VmSummary[] }>('scanLibrary');
      setVms(lib.vms);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    (async () => {
      if (!api) return;
      try {
        setProfiles(await api.coreCall<OsProfileDto[]>('getProfiles'));
        await refresh();
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      }
    })();
    const t = setInterval(refresh, 3000);
    return () => clearInterval(t);
  }, [refresh]);

  const onStart = useCallback(
    async (vm: VmSummary) => {
      if (!api) return;
      await api.coreCall('startVm', { packagePath: vm.path });
      await refresh();
    },
    [refresh],
  );

  const onPower = useCallback(
    async (vm: VmSummary, action: 'shutdown' | 'suspend' | 'forceOff') => {
      if (!api) return;
      await api.coreCall('powerAction', { packagePath: vm.path, action });
      await refresh();
    },
    [refresh],
  );

  const onOpenDisplay = useCallback(async (vm: VmSummary) => {
    if (!api) return;
    // SPICE 端口由 Core 经 QMP query-spice 提供；这里走显示器打开协议
    await api.openDisplay(vm.name, 0);
  }, []);

  const memoCards = useMemo(
    () =>
      vms.map((vm) => (
        <VmCard
          key={vm.path}
          vm={vm}
          onStart={onStart}
          onPower={onPower}
          onOpenDisplay={onOpenDisplay}
          onSettings={setSettingsVm}
        />
      )),
    [vms, onStart, onPower, onOpenDisplay],
  );

  if (!api) {
    return (
      <div className="empty-state">
        <p>此页面需要在 Grass Block VM 桌面应用中运行（未检测到 preload 桥）。</p>
      </div>
    );
  }

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark">🟩</span>
          <h1>我的虚拟机</h1>
        </div>
        <button className="btn-primary" onClick={() => setWizardOpen(true)} disabled={profiles.length === 0}>
          ＋ 新建虚拟机
        </button>
      </header>

      {error && <div className="banner-error">{error}</div>}

      <main className="library">
        {vms.length === 0 && !error && (
          <div className="empty-state">
            <div className="empty-art">🌱</div>
            <h2>还没有虚拟机</h2>
            <p>点击右上角"新建虚拟机"，几分钟就能得到一台新的虚拟电脑。</p>
          </div>
        )}
        <div className="card-grid">{memoCards}</div>
      </main>

      {wizardOpen && (
        <CreateWizard
          profiles={profiles}
          existingNames={vms.map((v) => v.name)}
          onClose={() => setWizardOpen(false)}
          onCreated={refresh}
        />
      )}
      {settingsVm && <VmSettings vm={settingsVm} onClose={() => setSettingsVm(null)} />}
    </div>
  );
}

function VmCard(props: {
  vm: VmSummary;
  onStart(vm: VmSummary): void;
  onPower(vm: VmSummary, action: 'shutdown' | 'suspend' | 'forceOff'): void;
  onOpenDisplay(vm: VmSummary): void;
  onSettings(vm: VmSummary): void;
}): React.ReactElement {
  const { vm } = props;
  const action = primaryAction(vm.state);
  const badge = osBadge(vm.osProfileId);
  return (
    <article className={`vm-card state-${vm.state}`}>
      <div className="vm-cover">
        <span className="vm-os-emoji">{badge.emoji}</span>
        {vm.state === 'running' && <span className="vm-running-dot" title={stateLabel(vm.state)} />}
      </div>
      <h3 className="vm-name">{vm.name}</h3>
      <p className="vm-subtitle">{vmSubtitle(vm)}</p>
      <p className="vm-state-label">{stateLabel(vm.state)}</p>
      <div className="vm-card-actions">
        {action && (
          <button
            className="btn-primary"
            onClick={() =>
              action === 'start'
                ? props.onStart(vm)
                : action === 'resume'
                  ? props.onStart(vm)
                  : props.onOpenDisplay(vm)
            }
          >
            {primaryActionLabel(action)}
          </button>
        )}
        {(vm.state === 'running' || vm.state === 'suspending') && (
          <details className="power-menu">
            <summary>电源</summary>
            <div className="power-menu-items">
              {powerMenuItems().map((item) => (
                <button
                  key={item}
                  className={item === 'forceOff' ? 'menu-item-danger' : 'menu-item'}
                  onClick={() => props.onPower(vm, item)}
                >
                  {item === 'shutdown' ? '关机' : item === 'suspend' ? '挂起' : '强制关机…'}
                </button>
              ))}
            </div>
          </details>
        )}
        <button className="btn-ghost" onClick={() => props.onSettings(vm)}>
          设置
        </button>
      </div>
    </article>
  );
}
