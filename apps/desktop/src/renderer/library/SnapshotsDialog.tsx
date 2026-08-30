/**
 * 快照面板：列表 / 创建 / 恢复 / 删除 / 从快照建链接克隆。
 * 快照只在【关机】状态下可动（Core 校验；运行中这里直接禁用并说明）。
 * 恢复 = 丢弃快照点之后的全部工作状态（确认话术里明说）；
 * 删除 = 可能触发向后合并（计划预览告知受影响的链接克隆）。
 */
import React, { useCallback, useEffect, useState } from 'react';
import type { VmSummary } from '../../shared/contract';

interface SnapshotEntry {
  uuid: string;
  parent: string | null;
  name: string;
  description: string | null;
  createdAt: string;
  hasMemory: boolean;
  hidden: boolean;
}

interface DeletePlan {
  isChainRoot: boolean;
  requiresMerge: boolean;
  keepsBase: boolean;
  affectedLinkedClones: Array<{ childVmName: string; childVmPath: string }>;
  rebindings: unknown;
}

interface GrassApi {
  coreCall<T = unknown>(method: string, params?: unknown): Promise<T>;
}

const api: GrassApi | undefined = (window as unknown as { grassvm?: GrassApi }).grassvm;

export function SnapshotsDialog(props: { vm: VmSummary; onClose(): void }): React.ReactElement {
  const [items, setItems] = useState<SnapshotEntry[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const locked = props.vm.state !== 'stopped';

  const reload = useCallback(async () => {
    if (!api) return;
    try {
      const all = await api.coreCall<SnapshotEntry[]>('listSnapshots', { packagePath: props.vm.path });
      setItems(all.filter((s) => !s.hidden)); // 升级保护快照不是用户的
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [props.vm.path]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const run = useCallback(
    async (label: string, fn: () => Promise<unknown>) => {
      setBusy(true);
      try {
        await fn();
        await reload();
        setError(null);
      } catch (e) {
        setError(`${label}失败：${e instanceof Error ? e.message : String(e)}`);
      } finally {
        setBusy(false);
      }
    },
    [reload],
  );

  const create = () => {
    const name = window.prompt('新快照的名称（保存当前全部磁盘状态）', `快照 ${new Date().toLocaleString()}`);
    if (!name?.trim() || !api) return;
    void run('创建快照', () =>
      api.coreCall('createSnapshot', { packagePath: props.vm.path, name: name.trim() }),
    );
  };

  const restore = async (s: SnapshotEntry) => {
    if (!api) return;
    // 预览警告（配置回滚、包外盘不回滚…）来自 Core：确认框必须如实转达——
    // "恢复后快照之后添加的硬盘会从配置里消失"这种事不能等恢复完才说
    let warnings: string[] = [];
    try {
      const plan = await api.coreCall<{ warnings: string[] }>('planRestoreSnapshot', {
        packagePath: props.vm.path,
        uuid: s.uuid,
      });
      warnings = plan.warnings;
    } catch {
      /* 预览失败不阻塞恢复本身 */
    }
    if (
      !window.confirm(
        `恢复到“${s.name}”？\n快照点之后的全部更改（自 ${new Date(s.createdAt).toLocaleString()} 以来）将被丢弃。` +
          (warnings.length > 0 ? `\n\n${warnings.join('\n')}` : ''),
      )
    )
      return;
    void run('恢复', () =>
      api.coreCall('restoreSnapshot', { packagePath: props.vm.path, uuid: s.uuid }),
    );
  };

  const remove = async (s: SnapshotEntry) => {
    if (!api) return;
    let plan: DeletePlan;
    try {
      plan = await api.coreCall<DeletePlan>('planDeleteSnapshot', {
        packagePath: props.vm.path,
        uuid: s.uuid,
      });
    } catch (e) {
      setError(`读取删除计划失败：${e instanceof Error ? e.message : String(e)}`);
      return;
    }
    const cloneNote =
      plan.affectedLinkedClones.length > 0
        ? `\n依赖此快照的链接克隆：${plan.affectedLinkedClones.map((c) => c.childVmName).join('、')}\n${
            // 链根基座与 keepsBase 的删除都走"保留物理文件"（keepPhysical）——
            // 克隆的 backing 依然有效、照常启动。吓唬用户"克隆将无法再启动"
            // 是不实指控（与同框的"磁盘文件会保留"自相矛盾）；只有真的会
            // commit/rebase 并删除基线文件时（requiresMerge 且无任何盘保留）
            // 克隆才失效
            plan.isChainRoot || plan.keepsBase
              ? '底层磁盘文件会保留，这些克隆不受影响。'
              : '删除后这些克隆将【无法再启动】（磁盘引用会失效）。如需保留它们，请先把克隆转换为完整克隆。'
          }`
        : '';
    // 措辞如实区分四种物理行为（与 Core 的 DeletePlan 一一对应）：
    // 链根基座 = 保留磁盘文件、仅移除列表项（快）；keptAsBase = 父快照不含
    // 某些盘（建盘时点更晚）→ 这些盘保留文件、不做合并；有依赖者 = 数据
    // commit 进父层（可能数分钟）；无依赖者 = 数据随删除直接丢弃（快）。
    // 不能对链根说"合并进前一个快照"——它没有前一个；也不能对无依赖层的删除
    // 说"会合并"——那是把"丢弃"说成"合并"，用户会以为数据还在
    const dataNote = plan.isChainRoot
      ? '\n这是最初的快照点：磁盘文件会保留，仅从快照列表中移除，很快完成。'
      : plan.requiresMerge && plan.keepsBase
        ? '\n混合处置：部分磁盘的数据会合并进前一个快照（可能需要一些时间），其余磁盘（不属于上一个快照的）文件原样保留。'
        : plan.requiresMerge
          ? '\n其中的数据会合并进前一个快照（可能需要一些时间）。'
          : plan.keepsBase
            ? '\n其中部分磁盘不属于上一个快照（建盘更晚）：这些文件会原样保留，不做合并，很快完成。'
            : '\n此快照的数据将随删除直接丢弃（没有其他层依赖它），很快完成。';
    if (
      !window.confirm(
        `删除快照“${s.name}”？` + dataNote + cloneNote,
      )
    )
      return;
    await run('删除', () =>
      api.coreCall('deleteSnapshot', { packagePath: props.vm.path, uuid: s.uuid }),
    );
  };

  const linkedClone = async (s: SnapshotEntry) => {
    if (!api) return;
    // 包外（外部）硬盘在链接克隆里同样不复制、直接共享同一物理文件——
    // 与完整克隆同一风险，必须同样提前告知（否则父子同跑 = 双写者损毁数据）
    let hasExternal = false;
    try {
      const c = await api.coreCall<{ devices: Array<{ deviceType: string; isExternal?: boolean }> }>(
        'getConfig',
        { packagePath: props.vm.path },
      );
      hasExternal = c.devices.some((d) => d.deviceType === 'disk' && d.isExternal === true);
    } catch {
      /* 拿不到配置就按普通提示走 */
    }
    const base = `基于快照“${s.name}”创建链接克隆（不复制磁盘数据，节省空间；依赖这台虚拟机存在）`;
    const name = window.prompt(
      hasExternal
        ? `${base}\n注意：这台虚拟机的包外硬盘不会被复制，克隆将继续直接使用同一文件——不要同时运行两台。`
        : base,
      `${props.vm.name} @ ${s.name}`,
    );
    if (!name?.trim()) return;
    void run('创建链接克隆', () =>
      api.coreCall('linkedClone', {
        packagePath: props.vm.path,
        snapshotUuid: s.uuid,
        newName: name.trim(),
      }),
    );
  };

  return (
    <div className="modal-backdrop">
      <div className="dialog snapshots">
        <header className="settings-header">
          <h2>{props.vm.name} 的快照</h2>
          <button className="btn-ghost" onClick={props.onClose}>
            完成
          </button>
        </header>
        {locked && (
          <div className="banner-quiet">
            快照操作需要先正常关机。当前状态下仅可查看列表。
          </div>
        )}
        {error && <div className="banner-error">{error}</div>}
        <div className="dialog-actions">
          <button className="btn-primary" disabled={busy || locked} onClick={create}>
            创建快照
          </button>
        </div>
        {items.length === 0 && <p>还没有快照。</p>}
        <ul className="snapshot-list">
          {items.map((s) => (
            <li key={s.uuid}>
              <div>
                <strong>{s.name}</strong>
                <span className="hint"> · {new Date(s.createdAt).toLocaleString()}</span>
              </div>
              <div className="dialog-actions">
                <button className="btn-ghost" disabled={busy || locked} onClick={() => restore(s)}>
                  恢复
                </button>
                <button className="btn-ghost" disabled={busy || locked} onClick={() => linkedClone(s)}>
                  链接克隆
                </button>
                <button className="btn-danger" disabled={busy || locked} onClick={() => void remove(s)}>
                  删除
                </button>
              </div>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
