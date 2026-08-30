/**
 * 设置页（设备化）：处理器与内存 / 硬盘 / CD/DVD / 网络 / 显示器 / 声音 / USB /
 * 共享文件夹 / 摄像头 / 麦克风 / 安全芯片。
 * 原则：界面展示 = 当前事实（getConfig 实时读取）。运行中不可修改的设备直接锁定；
 * 不存在"待应用"。设备按添加时间稳定排序显示（磁盘 #1 / 网络 #2…编号不漂移）。
 */
import React, { useCallback, useEffect, useState } from 'react';
import type { VmSummary } from '../../shared/contract';

const DEVICE_ORDER = [
  'cpu-memory',
  'disk',
  'cdrom',
  'network',
  'display',
  'audio',
  'usb',
  'sharedFolder',
  'camera',
  'microphone',
  'tpm',
  'autostart',
] as const;

/** getConfig 返回的配置里 UI 关心的字段（C# VmConfiguration 的子集） */
interface VmConfigView {
  name: string;
  osProfileId: string;
  cpuCores: number;
  memoryMiB: number;
  devices: Array<{
    deviceId: string;
    deviceType: string;
    path?: string;
    sizeBytes?: number;
    isoPath?: string | null;
    mode?: string;
    enabled?: boolean;
  }>;
}

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
}

const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

export function VmSettings(props: { vm: VmSummary; onClose(): void }): React.ReactElement {
  const [selected, setSelected] = useState<(typeof DEVICE_ORDER)[number]>('cpu-memory');
  const [busy, setBusy] = useState(false);
  const suspendedNow = props.vm.state === 'suspended';
  const lockedNow =
    props.vm.state === 'running' ||
    props.vm.state === 'suspending' ||
    props.vm.state === 'starting' ||
    suspendedNow;
  const [config, setConfig] = useState<VmConfigView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [cpu, setCpu] = useState(0);
  const [mem, setMem] = useState(0);
  const [hostInfo, setHostInfo] = useState<{ cpuCores: number; memoryMiB: number } | null>(null);

  useEffect(() => {
    (async () => {
      if (!api) return;
      try {
        const c = await api.coreCall<VmConfigView>('getConfig', { packagePath: props.vm.path });
        setConfig(c);
        setCpu(c.cpuCores);
        setMem(c.memoryMiB);
        setHostInfo(await api.coreCall<{ cpuCores: number; memoryMiB: number }>('getHostInfo'));
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      }
    })();
  }, [props.vm.path]);

  // Core 侧改写配置（硬盘扩容等）后重拉：设置页持有的配置快照必须始终与
  // config.json 一致，否则下一次“保存”会把旧值（旧 sizeBytes）原样写回
  const reloadConfig = useCallback(async () => {
    if (!api) return;
    try {
      const c = await api.coreCall<VmConfigView>('getConfig', { packagePath: props.vm.path });
      setConfig(c);
    } catch {
      /* 刷新失败不打断扩容结果展示 */
    }
  }, [api, props.vm.path]);

  const onSave = useCallback(async () => {
    if (!api || !config) return;
    try {
      const next = { ...config, cpuCores: cpu, memoryMiB: mem };
      await api.coreCall('updateConfig', {
        packagePath: props.vm.path,
        configJson: JSON.stringify(next),
      });
      setDirty(false);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [api, config, cpu, mem, props.vm.path]);

  // 上限来自 Core（宿主真实内存；拿不到时退回保守的每核 2GB 估算）
  const hostCores = hostInfo?.cpuCores ?? navigator.hardwareConcurrency ?? 8;
  const hostMemMiB = hostInfo?.memoryMiB ?? hostCores * 2048;
  const edit = (setter: (v: number) => void) => (v: number) => {
    setter(v);
    setDirty(true);
  };

  return (
    <div className="modal-backdrop">
      <div className="settings">
        <header className="settings-header">
          <h2>{props.vm.name} 的设置</h2>
          <button className="btn-ghost" onClick={props.onClose}>
            完成
          </button>
        </header>
        {lockedNow && !suspendedNow && (
          <div className="banner-quiet">
            这台虚拟机正在运行。运行期间无法修改的设置已锁定，正常关机后即可更改。
          </div>
        )}
        {suspendedNow && (
          <div className="banner-quiet">
            这台虚拟机已挂起。保存的运行状态绑定挂起时的硬件配置，请先恢复并正常关机后再修改设置。
            <button
              className="btn-ghost"
              disabled={busy}
              title="丢弃挂起瞬间的内存状态（磁盘数据完好，虚拟机从硬盘冷启动）。恢复失败或已换电脑时使用。"
              onClick={async () => {
                if (!window.confirm(
                  `放弃“${props.vm.name}”保存的挂起状态？\n\n挂起瞬间之后的内存里未保存的工作会丢失；磁盘上的文件完好，虚拟机将从硬盘重新启动。`,
                )) return;
                setBusy(true);
                try {
                  await api?.coreCall('discardSuspendState', { packagePath: props.vm.path });
                  props.onClose();
                } catch (e) {
                  setError(e instanceof Error ? e.message : String(e));
                } finally {
                  setBusy(false);
                }
              }}
            >
              放弃保存的状态…
            </button>
          </div>
        )}
        {error && <div className="banner-error">{error}</div>}
        <div className="settings-body">
          <nav className="settings-nav">
            {DEVICE_ORDER.map((d) => (
              <button
                key={d}
                className={selected === d ? 'nav-item active' : 'nav-item'}
                onClick={() => setSelected(d)}
              >
                {navLabel(d)}
              </button>
            ))}
          </nav>
          <section className="settings-pane">
            {renderPane(selected, lockedNow, {
              config,
              cpu,
              mem,
              hostCores,
              hostMemMiB,
              dirty,
              locked: lockedNow,
              vmPath: props.vm.path,
              autostart: props.vm.hasAutostart ?? false,
              setCpu: edit(setCpu),
              setMem: edit(setMem),
              onSave,
              onResized: reloadConfig,
            })}
          </section>
        </div>
      </div>
    </div>
  );
}

interface PaneProps {
  config: VmConfigView | null;
  cpu: number;
  mem: number;
  hostCores: number;
  hostMemMiB: number;
  dirty: boolean;
  locked: boolean;
  vmPath: string;
  autostart: boolean;
  setCpu(v: number): void;
  setMem(v: number): void;
  onSave(): void;
  /** 扩容等 Core 侧改写配置的操作完成后调用：重新拉取整份配置，避免后续保存把
   *  对话框里的旧快照（旧 sizeBytes）写回去 */
  onResized(): void;
}

function navLabel(d: (typeof DEVICE_ORDER)[number]): string {
  switch (d) {
    case 'cpu-memory':
      return '处理器与内存';
    case 'disk':
      return '硬盘';
    case 'cdrom':
      return 'CD/DVD';
    case 'network':
      return '网络';
    case 'display':
      return '显示器';
    case 'audio':
      return '声音';
    case 'usb':
      return 'USB';
    case 'sharedFolder':
      return '共享文件夹';
    case 'camera':
      return '摄像头';
    case 'microphone':
      return '麦克风';
    case 'tpm':
      return '安全芯片';
    case 'autostart':
      return '自动启动';
  }
}

function renderPane(d: (typeof DEVICE_ORDER)[number], locked: boolean, p: PaneProps): React.ReactNode {
  const lock = locked ? <p className="lock-note">🔒 运行中无法修改，正常关机后可调整。</p> : null;
  switch (d) {
    case 'cpu-memory':
      return (
        <>
          {lock}
          <label className="field">
            处理器核心数：{p.cpu} 核（宿主共 {p.hostCores} 核）
            <input
              type="range"
              min={1}
              max={p.hostCores}
              step={1}
              value={p.cpu}
              disabled={locked}
              onChange={(e) => p.setCpu(Number(e.target.value))}
            />
          </label>
          <label className="field">
            内存：{(p.mem / 1024).toFixed(p.mem % 1024 === 0 ? 0 : 1)} GB
            <input
              type="range"
              min={512}
              max={Math.max(512, Math.floor(p.hostMemMiB / 2 / 512) * 512)}
              step={512}
              value={p.mem}
              disabled={locked}
              onChange={(e) => p.setMem(Number(e.target.value))}
            />
          </label>
          <p className="hint">调整后立即生效，无需重装系统。</p>
          <button className="btn-primary" disabled={locked || !p.dirty} onClick={p.onSave}>
            保存
          </button>
        </>
      );
    case 'disk':
      return (
        <>
          {lock}
          {(p.config?.devices ?? [])
            .filter((dev) => dev.deviceType === 'disk')
            .map((dev, i) => (
              <DiskResizeRow
                key={dev.deviceId}
                vmPath={p.vmPath}
                deviceId={dev.deviceId}
                index={i}
                currentGiB={Math.round((dev.sizeBytes ?? 0) / 1024 / 1024 / 1024)}
                locked={p.locked}
                onResized={p.onResized}
              />
            ))}
          <p>硬盘容量只能扩大，不能缩小。扩大后需要在客户机内自行扩展分区。</p>
        </>
      );
    case 'cdrom':
      return (
        <>
          {(p.config?.devices ?? [])
            .filter((dev) => dev.deviceType === 'cdrom')
            .map((dev, i) => (
              <p key={dev.deviceId}>
                CD/DVD #{i + 1}：{dev.isoPath ? dev.isoPath.split(/[\\/]/).pop() : '空（无介质）'}
              </p>
            ))}
          <p>CD/DVD 可以在运行中更换或取出镜像（即插即用）。</p>
        </>
      );
    case 'network':
      return (
        <>
          {lock}
          {(p.config?.devices ?? [])
            .filter((dev) => dev.deviceType === 'network')
            .map((dev, i) => (
              <p key={dev.deviceId}>
                网卡 #{i + 1}：{networkModeLabel(dev.mode)}
              </p>
            ))}
          <p>每张网卡可独立选择：NAT（默认，开箱即用）/ 桥接 / Host-only / 断开。</p>
        </>
      );
    case 'display':
      return <p>共享剪贴板与文件拖放默认开启；需要客户机帮助程序支持，缺失时自动降级。</p>;
    case 'audio': {
      // 如实反映配置：设备可能被关闭，也可能本就不存在（导出往返用"缺席"
      // 表达关闭——缺席时绝不能显示"已开启"）
      const audioDev = (p.config?.devices ?? []).find((dev) => dev.deviceType === 'audio');
      const enabled = audioDev?.enabled === true;
      return (
        <p>
          {enabled
            ? '声音已开启，底层输出自动选择。'
            : '声音未启用：这台虚拟机没有启用中的声卡设备（导入/导出会如实保留关闭状态）。'}
        </p>
      );
    }
    case 'usb':
      return <p>宿主 USB 设备从显示器窗口的设备栏“连接到此虚拟机”，运行中即可操作。</p>;
    case 'sharedFolder':
      return (
        <>
          {lock}
          <p>共享文件夹默认可读写，可单独勾选只读。需要客户机帮助程序。</p>
        </>
      );
    case 'camera':
      return <p>摄像头默认关闭；启用后由这台虚拟机独占使用。</p>;
    case 'microphone':
      return <p>麦克风默认关闭；启用后由这台虚拟机独占使用。</p>;
    case 'tpm':
      return <p>安全芯片（TPM 2.0）：Windows 11 虚拟机默认开启。</p>;
    case 'autostart':
      return <AutostartPane vmPath={p.vmPath} initial={p.autostart} />;
  }
}

/** 硬盘扩容行：resizeDisk 走 Core（QCOW2 resize，只扩大） */
function DiskResizeRow(props: {
  vmPath: string;
  deviceId: string;
  index: number;
  currentGiB: number;
  locked: boolean;
  /** 扩容成功后由父组件刷新整份配置：设置页后续“保存”才会带上新容量，否则
   *  旧的 sizeBytes 会被原样写回 config.json（物理盘 128GB、配置却写着 64GB） */
  onResized(): void;
}): React.ReactElement {
  const [gib, setGib] = useState(props.currentGiB);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [doneGiB, setDoneGiB] = useState(props.currentGiB);
  return (
    <div className="field">
      {err && <div className="banner-error">{err}</div>}
      <label className="file-row">
        <span>硬盘 #{props.index + 1}</span>
        <input
          type="number"
          min={props.currentGiB}
          max={2048}
          value={gib}
          disabled={props.locked || busy}
          onChange={(e) => setGib(Number(e.target.value))}
        />
        <span>GB（当前 {doneGiB} GB）</span>
        <button
          className="btn-ghost"
          disabled={props.locked || busy || gib <= props.currentGiB}
          onClick={async () => {
            setBusy(true);
            try {
              await api?.coreCall('resizeDisk', {
                packagePath: props.vmPath,
                deviceId: props.deviceId,
                newGiB: gib,
              });
              setDoneGiB(gib);
              setErr(null);
              props.onResized();
            } catch (e) {
              setErr(e instanceof Error ? e.message : String(e));
            } finally {
              setBusy(false);
            }
          }}
        >
          扩大到 {gib} GB
        </button>
      </label>
    </div>
  );
}

/** 宿主级属性（不随 .grassvm 迁移）：开机自动逐台启动，串行间隔默认 10 秒 */
function AutostartPane(props: { vmPath: string; initial: boolean }): React.ReactElement {
  const [enabled, setEnabled] = useState(props.initial);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  return (
    <>
      {err && <div className="banner-error">{err}</div>}
      <label className="field">
        <input
          type="checkbox"
          checked={enabled}
          disabled={busy}
          onChange={async (e) => {
            const next = e.target.checked;
            setBusy(true);
            try {
              await api?.coreCall('setAutostart', { packagePath: props.vmPath, enabled: next });
              setEnabled(next);
              setErr(null);
            } catch (ex) {
              setErr(ex instanceof Error ? ex.message : String(ex));
            } finally {
              setBusy(false);
            }
          }}
        />
        打开电脑登录后自动启动这台虚拟机
      </label>
      <p className="hint">多台自动启动的虚拟机按列表顺序串行启动，间隔默认 10 秒。</p>
    </>
  );
}

/** 与 C# NetworkMode 枚举的 camelCase 线值一一对应 */
export function networkModeLabel(mode?: string): string {
  switch (mode) {
    case 'nat':
      return 'NAT（默认）';
    case 'bridged':
      return '桥接';
    case 'hostOnly':
      return 'Host-only';
    case 'disconnected':
      return '断开';
    default:
      return mode ?? '未知';
  }
}

/** 供 App 在 forceOff 菜单里弹的二次确认（文案由 vm-state.forceOffConfirmText 提供）。 */
export function ForceOffConfirm(props: { vmName: string; onConfirm(): void; onCancel(): void }): React.ReactElement {
  return (
    <div className="modal-backdrop">
      <div className="dialog">
        <h2>强制关机“{props.vmName}”？</h2>
        <p className="danger-text">{forceOffText()}</p>
        <div className="dialog-actions">
          <button className="btn-ghost" autoFocus onClick={props.onCancel}>
            取消
          </button>
          <button className="btn-danger" onClick={props.onConfirm}>
            仍要强制关机
          </button>
        </div>
      </div>
    </div>
  );
}

function forceOffText(): string {
  return (
    '强制关机会立即停止虚拟机，这相当于给一台正在运行的电脑直接拔掉电源线，' +
    '可能导致未保存的数据丢失、文件系统损坏，甚至造成不可逆的问题。仅在虚拟机无响应时使用。'
  );
}

export function UnlockConfirm(props: { vmName: string; onConfirm(): void; onCancel(): void }): React.ReactElement {
  return (
    <div className="modal-backdrop">
      <div className="dialog">
        <h2>解除“{props.vmName}”的锁定？</h2>
        <p className="danger-text">
          锁定表示这台虚拟机可能正在另一台电脑或另一个实例上运行。只有在确认它没有在运行时才解除，
          否则两个系统同时写入同一块磁盘会导致数据损坏。如果不确定，请先到另一台机器上正常关机。
        </p>
        <div className="dialog-actions">
          <button className="btn-ghost" autoFocus onClick={props.onCancel}>
            取消
          </button>
          <button className="btn-danger" onClick={props.onConfirm}>
            确认没有在运行，解除锁定
          </button>
        </div>
      </div>
    </div>
  );
}
