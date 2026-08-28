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
import { ForceOffConfirm, UnlockConfirm, VmSettings } from './VmSettings';

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
  openDisplay(vmName: string, spicePort: number, packagePath: string): Promise<boolean>;
  pickOpenFile(filterName: string, extensions: string[]): Promise<string | null>;
  pickSaveFile(defaultName: string, filterName: string, extensions: string[]): Promise<string | null>;
  pickDirectory(): Promise<string | null>;
  defaultLibraryDir(): Promise<string>;
}

const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

export function App(): React.ReactElement {
  const [vms, setVms] = useState<VmSummary[]>([]);
  const [profiles, setProfiles] = useState<OsProfileDto[]>([]);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [settingsVm, setSettingsVm] = useState<VmSummary | null>(null);
  const [forceOffVm, setForceOffVm] = useState<VmSummary | null>(null);
  const [unlockVm, setUnlockVm] = useState<VmSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [needsSetup, setNeedsSetup] = useState(false);
  const [libraryDir, setLibraryDir] = useState<string>('');
  // 默认存档位置由主进程解析（渲染层沙箱读不到 USERPROFILE）
  useEffect(() => {
    void api?.defaultLibraryDir().then(setLibraryDir);
  }, []);
  const [savingSetup, setSavingSetup] = useState(false);

  const refresh = useCallback(async () => {
    if (!api) return;
    try {
      const lib = await api.coreCall<{ vms: VmSummary[] }>('scanLibrary');
      setVms(lib.vms);
      setError(null);
      setNeedsSetup(false);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      if (msg.includes('尚未设置')) {
        setNeedsSetup(true); // 首次运行：引导选择存档位置（不是错误横幅）
        setError(null);
      } else {
        setError(msg);
      }
    }
  }, []);

  const confirmLibraryDir = useCallback(async () => {
    if (!api || savingSetup) return;
    setSavingSetup(true);
    try {
      await api.coreCall('setLibraryRoot', { libraryRoot: libraryDir });
      setNeedsSetup(false);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSavingSetup(false);
    }
  }, [api, libraryDir, savingSetup, refresh]);

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

  const fail = useCallback((e: unknown) => {
    setError(e instanceof Error ? e.message : String(e));
  }, []);

  const onStart = useCallback(
    async (vm: VmSummary) => {
      if (!api) return;
      try {
        await api.coreCall('startVm', { packagePath: vm.path });
      } catch (e) {
        fail(e);
        return;
      }
      await refresh();
    },
    [refresh, fail],
  );

  // 挂起恢复：必须走 resumeVm（startVm 会拒绝已挂起的 VM——保存的状态只能用 -incoming 回到）
  const onResume = useCallback(
    async (vm: VmSummary) => {
      if (!api) return;
      try {
        await api.coreCall('resumeVm', { packagePath: vm.path });
      } catch (e) {
        fail(e);
        return;
      }
      await refresh();
    },
    [refresh, fail],
  );

  const onPower = useCallback(
    async (vm: VmSummary, action: 'shutdown' | 'suspend' | 'forceOff') => {
      if (!api) return;
      try {
        await api.coreCall('powerAction', { packagePath: vm.path, action });
      } catch (e) {
        fail(e);
        return;
      }
      await refresh();
    },
    [refresh, fail],
  );

  const onOpenDisplay = useCallback(async (vm: VmSummary) => {
    if (!api) return;
    try {
      // -spice port=0 自动分配：真实端口由 Core 经 QMP query-spice 查询
      const info = await api.coreCall<{ spicePort: number }>('getDisplayInfo', { packagePath: vm.path });
      await api.openDisplay(vm.name, info.spicePort, vm.path);
    } catch (e) {
      fail(e);
    }
  }, [fail]);

  // 完整克隆：独立副本（无快照历史、无本机痕迹）
  const onFullClone = useCallback(
    async (vm: VmSummary) => {
      if (!api) return;
      const name = window.prompt('新虚拟机的名称（完整克隆：独立副本，不含快照历史）', `${vm.name} 副本`);
      if (!name?.trim()) return;
      try {
        await api.coreCall('fullClone', { packagePath: vm.path, newName: name.trim() });
        await refresh();
      } catch (e) {
        fail(e);
      }
    },
    [refresh, fail],
  );

  // 导出完整档案（必须已关机；Core 校验后导出）
  const onExportZip = useCallback(
    async (vm: VmSummary) => {
      if (!api) return;
      const target = await api.pickSaveFile(`${vm.name}.grassvm.zip`, 'Grass Block VM 档案', ['zip']);
      if (!target) return;
      try {
        await api.coreCall('exportZip', { packagePath: vm.path, zipPath: target });
        await refresh();
      } catch (e) {
        fail(e);
      }
    },
    [refresh, fail],
  );

  // 导入：.grassvm.zip 完整档案 或 .ova/.ovf（计划→[仍然导入]→执行）
  const onImport = useCallback(async () => {
    if (!api) return;
    const src = await api.pickOpenFile('虚拟机档案', ['zip', 'ova', 'ovf']);
    if (!src) return;
    try {
      if (src.toLowerCase().endsWith('.zip')) {
        await api.coreCall('importZip', { zipPath: src });
      } else {
        const plan = await api.coreCall<{
          vmName: string;
          requiredGiB: number;
          warnings: string[];
          unsupported: string[];
          blocksImport: boolean;
        }>('planImportOvf', { path: src });
        if (plan.blocksImport && !window.confirm(
          `这份档案包含 ${plan.unsupported.length} 个无法支持的设备（${plan.unsupported.join('、')}）。\n` +
          '仍要导入吗？这些设备会保留为占位（标记不可用），其余设备正常工作。',
        )) {
          return;
        }
        if (plan.warnings.length > 0 && !window.confirm(plan.warnings.join('\n'))) return;
        const name = window.prompt('虚拟机名称', plan.vmName);
        if (!name?.trim()) return;
        await api.coreCall('executeImportOvf', {
          path: src,
          vmName: name.trim(),
          allowUnsupported: plan.blocksImport,
        });
      }
      await refresh();
    } catch (e) {
      fail(e);
    }
  }, [refresh, fail]);

  const memoCards = useMemo(
    () =>
      vms.map((vm) => (
        <VmCard
          key={vm.path}
          vm={vm}
          onStart={onStart}
          onResume={onResume}
          onPower={onPower}
          onRequestForceOff={setForceOffVm}
          onUnlock={setUnlockVm}
          onOpenDisplay={onOpenDisplay}
          onSettings={setSettingsVm}
          onFullClone={onFullClone}
          onExportZip={onExportZip}
        />
      )),
    [vms, onStart, onResume, onPower, onOpenDisplay, onFullClone, onExportZip],
  );

  if (!api) {
    return (
      <div className="empty-state">
        <p>此页面需要在 Grass Block VM 桌面应用中运行（未检测到 preload 桥）。</p>
      </div>
    );
  }

  if (needsSetup) {
    return (
      <div className="app-shell">
        <header className="topbar">
          <div className="brand">
            <span className="brand-mark">🟩</span>
            <h1>欢迎使用 Grass Block VM</h1>
          </div>
        </header>
        <main className="setup-card">
          <h2>选择虚拟机存档位置</h2>
          <p className="hint">
            你的虚拟机会以文件夹形式保存在这里（每台一个 .grassvm 文件夹）。建议放在本地磁盘；
            不要放在网络盘或同步盘里（虚拟机磁盘不适合同步）。
          </p>
          <label className="file-row">
            <span>存档位置</span>
            <input
              type="text"
              value={libraryDir}
              onChange={(e) => setLibraryDir(e.target.value)}
            />
            <button
              className="btn-ghost"
              onClick={async () => {
                const picked = await api?.pickDirectory();
                if (picked) setLibraryDir(picked);
              }}
            >
              浏览…
            </button>
          </label>
          <div className="setup-actions">
            <button className="btn-primary" onClick={confirmLibraryDir} disabled={savingSetup}>
              {savingSetup ? '正在初始化…' : '使用此位置'}
            </button>
          </div>
          {error && <div className="banner-error">{error}</div>}
        </main>
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
        <div className="topbar-actions">
          <button className="btn-ghost" onClick={onImport}>
            导入…
          </button>
          <button className="btn-primary" onClick={() => setWizardOpen(true)} disabled={profiles.length === 0}>
            ＋ 新建虚拟机
          </button>
        </div>
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
      {forceOffVm && (
        <ForceOffConfirm
          vmName={forceOffVm.name}
          onCancel={() => setForceOffVm(null)}
          onConfirm={() => {
            const vm = forceOffVm;
            setForceOffVm(null);
            void onPower(vm, 'forceOff');
          }}
        />
      )}
      {unlockVm && (
        <UnlockConfirm
          vmName={unlockVm.name}
          onCancel={() => setUnlockVm(null)}
          onConfirm={() => {
            const vm = unlockVm;
            setUnlockVm(null);
            if (!api) return;
            // 解锁后立即刷新（卡片状态从"已锁定"恢复；失败也刷新让用户看到真实状态）
            void api
              .coreCall('unlockVm', { packagePath: vm.path })
              .then(refresh, (e: unknown) => {
                fail(e);
                void refresh();
              });
          }}
        />
      )}
    </div>
  );
}

function VmCard(props: {
  vm: VmSummary;
  onStart(vm: VmSummary): void;
  onResume(vm: VmSummary): void;
  onPower(vm: VmSummary, action: 'shutdown' | 'suspend' | 'forceOff'): void;
  onRequestForceOff(vm: VmSummary): void;
  onUnlock(vm: VmSummary): void;
  onOpenDisplay(vm: VmSummary): void;
  onSettings(vm: VmSummary): void;
  onFullClone(vm: VmSummary): void;
  onExportZip(vm: VmSummary): void;
}): React.ReactElement {
  const { vm } = props;
  const action = primaryAction(vm.state);
  const badge = osBadge(vm.osProfileId);
  const busy = vm.state === 'running' || vm.state === 'suspending' || vm.state === 'starting';
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
                  ? props.onResume(vm)
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
                  onClick={() =>
                    item === 'forceOff' ? props.onRequestForceOff(vm) : props.onPower(vm, item)
                  }
                >
                  {item === 'shutdown' ? '关机' : item === 'suspend' ? '挂起' : '强制关机…'}
                </button>
              ))}
            </div>
          </details>
        )}
        <details className="power-menu">
          <summary>更多</summary>
          <div className="power-menu-items">
            {vm.locked && (
              <button
                className="menu-item-danger"
                disabled={busy}
                title="只在确认它没有在其他实例或其他电脑上运行时才解除；运行中强制解锁会导致磁盘损坏"
                onClick={() => props.onUnlock(vm)}
              >
                解除锁定…
              </button>
            )}
            <button
              className="menu-item"
              disabled={busy || vm.state === 'suspended'}
              title={vm.state === 'suspended' ? '已挂起的虚拟机需要先恢复并正常关机' : undefined}
              onClick={() => props.onFullClone(vm)}
            >
              克隆副本
            </button>
            <button
              className="menu-item"
              disabled={busy || vm.state === 'suspended'}
              title={vm.state === 'suspended' ? '已挂起的虚拟机需要先恢复并正常关机' : undefined}
              onClick={() => props.onExportZip(vm)}
            >
              导出档案…
            </button>
          </div>
        </details>
        <button className="btn-ghost" onClick={() => props.onSettings(vm)}>
          设置
        </button>
      </div>
    </article>
  );
}
