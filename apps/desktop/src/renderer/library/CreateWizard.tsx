/**
 * 创建向导（Parallels 风格）：四步。默认值来自 Profile 推荐；"创建后立即启动"默认开。
 * 磁盘大小可调（限制：既有磁盘在关机后只能扩大）由 Core 校验。
 */
import React, { useMemo, useState } from 'react';
import type { OsProfileDto } from '../../shared/contract';
import { validateWizard } from './vm-state';
import {
  advance,
  canAdvance,
  defaultVmName,
  goBack,
  initialWizardState,
  selectProfile,
  sortProfilesForDisplay,
  toCreateRequest,
  WIZARD_STEPS,
  type WizardState,
} from './wizard-state';

const STEP_TITLES: Record<WizardState['step'], string> = {
  os: '选择操作系统',
  iso: '选择安装镜像',
  config: '确认配置',
  confirm: '完成',
};

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
  pickOpenFile(filterName: string, extensions: string[]): Promise<string | null>;
}
const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

export function CreateWizard(props: {
  profiles: OsProfileDto[];
  existingNames: ReadonlyArray<string>;
  onClose(): void;
  onCreated(): void;
}): React.ReactElement {
  const [state, setState] = useState<WizardState>(() => initialWizardState(props.profiles));
  const [name, setName] = useState('');
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // ISO 路径归属向导状态（canAdvance/toCreateRequest 都读它），不再用脱钩的本地 state
  const isoPath = state.isoPath;
  const setIsoPath = (v: string | null): void =>
    setState((s) => ({ ...s, isoPath: v && v.trim() ? v.trim() : null }));

  const sorted = useMemo(() => sortProfilesForDisplay(props.profiles), [props.profiles]);
  const selected = state.profiles.find((p) => p.id === state.selectedProfileId) ?? null;
  const stepIndex = WIZARD_STEPS.indexOf(state.step);
  const fallbackName = selected ? defaultVmName(props.existingNames, selected.name) : '';
  const effectiveName = name.trim() || fallbackName;
  const validation = validateWizard(effectiveName, isoPath);

  const onNext = (): void => {
    // Profile 推荐值在选择时已应用（selectProfile），这里只推进步骤
    setState((s) => advance(s));
  };

  const onCreate = async (): Promise<void> => {
    const req = toCreateRequest({ ...state, vmName: name }, fallbackName);
    if (!req || !api) return;
    setCreating(true);
    setError(null);
    try {
      // "创建后立即启动"由 Core 在 createVm 内一并完成（启动失败不回滚创建，错误原样提示）
      await api.coreCall('createVm', req);
      props.onCreated();
      props.onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setCreating(false);
    }
  };

  return (
    <div className="modal-backdrop">
      <div className="wizard">
        <header className="wizard-header">
          <h2>新建虚拟机</h2>
          <div className="wizard-steps">
            {WIZARD_STEPS.map((s, i) => (
              <span key={s} className={i === stepIndex ? 'step active' : i < stepIndex ? 'step done' : 'step'}>
                {i + 1}. {STEP_TITLES[s]}
              </span>
            ))}
          </div>
        </header>

        <div className="wizard-body">
          {state.step === 'os' && (
            <div className="os-grid">
              {sorted.map((p) => (
                <button
                  key={p.id}
                  className={`os-option ${state.selectedProfileId === p.id ? 'selected' : ''}`}
                  onClick={() => setState((s) => selectProfile(s, p.id))}
                >
                  <span className="os-emoji">
                    {p.family === 'windows' ? '🪟' : p.family === 'linux' ? '🐧' : '📦'}
                  </span>
                  <span className="os-name">{p.name}</span>
                  <span className="os-verified">{p.verified ? '推荐' : '可安装'}</span>
                </button>
              ))}
            </div>
          )}

          {state.step === 'iso' && (
            <div className="iso-picker">
              <p>选择安装光盘镜像（ISO 文件）。安装完成后可以随时更换或取出。</p>
              <label className="file-row">
                <span>安装镜像</span>
                <input
                  type="text"
                  value={isoPath ?? ''}
                  placeholder="例如 C:\ISO\ubuntu-24.04-desktop-amd64.iso"
                  onChange={(e) => setIsoPath(e.target.value)}
                />
                <button
                  className="btn-ghost"
                  onClick={async () => {
                    if (!api) return;
                    const picked = await api.pickOpenFile('安装镜像（ISO）', ['iso']);
                    if (picked) setIsoPath(picked);
                  }}
                >
                  浏览…
                </button>
              </label>
            </div>
          )}

          {state.step === 'config' && (
            <div className="config-form">
              <label className="file-row">
                <span>虚拟机名称</span>
                <input
                  type="text"
                  value={name}
                  placeholder={fallbackName}
                  onChange={(e) => setName(e.target.value)}
                />
              </label>
              <label className="file-row">
                <span>处理器</span>
                <input
                  type="number"
                  min={1}
                  max={16}
                  value={state.cpuCores}
                  onChange={(e) => setState((s) => ({ ...s, cpuCores: Number(e.target.value) || 1 }))}
                />
              </label>
              <label className="file-row">
                <span>内存（GB）</span>
                <input
                  type="number"
                  min={0.5}
                  step={0.5}
                  value={state.memoryMiB / 1024}
                  onChange={(e) =>
                    setState((s) => ({ ...s, memoryMiB: Math.round((Number(e.target.value) || 1) * 1024) }))
                  }
                />
              </label>
              <label className="file-row">
                <span>硬盘（GB）</span>
                <input
                  type="number"
                  min={8}
                  step={8}
                  value={state.diskGiB}
                  onChange={(e) => setState((s) => ({ ...s, diskGiB: Number(e.target.value) || 8 }))}
                />
              </label>
            </div>
          )}

          {state.step === 'confirm' && (
            <div className="confirm-summary">
              <p>
                即将创建 <strong>{effectiveName}</strong>
                {selected ? `（${selected.name}）` : ''}。
              </p>
              <ul>
                <li>
                  {state.cpuCores} 核 CPU · {(state.memoryMiB / 1024).toFixed(1)} GB 内存 · {state.diskGiB} GB 硬盘
                </li>
                <li>网络默认 NAT（开箱即用，之后可在设置中改桥接/Host-only）</li>
                <li>创建后立即启动（{state.startAfterCreate ? '开' : '关'}）</li>
              </ul>
              <label className="check-row">
                <input
                  type="checkbox"
                  checked={state.startAfterCreate}
                  onChange={(e) => setState((s) => ({ ...s, startAfterCreate: e.target.checked }))}
                />
                创建后立即启动
              </label>
            </div>
          )}

          {error && <div className="banner-error">{error}</div>}
        </div>

        <footer className="wizard-footer">
          <button className="btn-ghost" onClick={props.onClose} disabled={creating}>
            取消
          </button>
          <div className="spacer" />
          {stepIndex > 0 && (
            <button className="btn-ghost" onClick={() => setState((s) => goBack(s))} disabled={creating}>
              上一步
            </button>
          )}
          {state.step !== 'confirm' ? (
            <button className="btn-primary" disabled={!canAdvance(state)} onClick={onNext}>
              下一步
            </button>
          ) : (
            <button className="btn-primary" disabled={creating || validation != null} onClick={() => void onCreate()}>
              {creating ? '正在创建…' : '创建'}
            </button>
          )}
        </footer>
      </div>
    </div>
  );
}
