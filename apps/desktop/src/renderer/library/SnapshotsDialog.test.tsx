import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, expect, it, vi } from 'vitest';
import type { VmSummary } from '../../shared/contract';

beforeEach(() => { vi.resetModules(); });

it.each([false, true])('保留隐藏系统检查点的当前位置且禁止操作（仅系统节点：%s）', async (systemOnly) => {
  const entries = [
    { uuid: 'root', parent: null, name: '根快照', hidden: false, isCurrent: false },
    { uuid: 'system', parent: systemOnly ? null : 'root', name: '内部名称', hidden: true, isCurrent: true },
    { uuid: 'child', parent: 'system', name: '子快照', hidden: false, isCurrent: false },
  ].filter(entry => !systemOnly || entry.hidden).map(entry => ({
    ...entry, description: null, createdAt: '2026-10-18T00:00:00Z',
    hasMemory: false, cleanupPending: false,
  }));
  const coreCall = vi.fn(async () => entries);
  vi.stubGlobal('grassvm', { coreCall });
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true);
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  try {
    const { SnapshotsDialog } = await import('./SnapshotsDialog');
    const vm: VmSummary = {
      path: '/library/test.grassvm', name: '测试虚拟机', osProfileId: 'ubuntu',
      state: 'stopped', cpuCores: 2, memoryMiB: 1024, locked: false, hasAutostart: false,
    };
    await act(async () => { root.render(<SnapshotsDialog vm={vm} onClose={() => undefined} />); });
    const rows = Array.from(container.querySelectorAll('.snapshot-list li'));
    expect(rows.map(row => row.getAttribute('data-depth'))).toEqual(systemOnly ? ['0'] : ['0', '1', '2']);
    const system = rows.find(row => row.textContent?.includes('系统保护检查点（不可操作）'));
    expect(system).toBeDefined();
    expect(system?.querySelector('[aria-current="location"]')?.textContent).toContain('你在这里');
    expect(system?.querySelectorAll('button')).toHaveLength(0);
    expect(container.textContent).not.toContain('内部名称');
    expect(container.textContent).not.toContain('还没有快照');
  } finally {
    await act(async () => { root.unmount(); });
    container.remove();
    vi.unstubAllGlobals();
  }
});

it('删除部分完成后失败时立即刷新位置及清理状态并保留错误', async () => {
  let cleanupPending = false;
  const coreCall = vi.fn(async (method: string) => {
    if (method === 'listSnapshots') return [{
      uuid: 'snapshot', parent: null, name: '测试快照', description: null,
      createdAt: '2026-10-18T00:00:00Z', hasMemory: false, hidden: false,
      cleanupPending, isCurrent: !cleanupPending,
    }];
    if (method === 'planDeleteSnapshot') return {
      cleanupOnly: false, isChainRoot: false, requiresMerge: true, keepsBase: false,
      affectedLinkedClones: [], rebindings: [],
    };
    if (method === 'deleteSnapshot') {
      cleanupPending = true;
      throw new Error('残留文件暂时被占用');
    }
    throw new Error(`Unexpected method: ${method}`);
  });
  vi.stubGlobal('grassvm', { coreCall });
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true);
  vi.spyOn(window, 'confirm').mockReturnValue(true);
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  try {
    const { SnapshotsDialog } = await import('./SnapshotsDialog');
    const vm: VmSummary = {
      path: '/library/test.grassvm', name: '测试虚拟机', osProfileId: 'ubuntu',
      state: 'stopped', cpuCores: 2, memoryMiB: 1024, locked: false, hasAutostart: false,
    };
    await act(async () => { root.render(<SnapshotsDialog vm={vm} onClose={() => undefined} />); });
    expect(container.querySelector('[aria-current="location"]')).not.toBeNull();
    const remove = Array.from(container.querySelectorAll('button')).find(button => button.textContent === '删除');
    expect(remove).toBeDefined();
    await act(async () => { remove?.click(); });
    expect(coreCall.mock.calls.filter(([method]) => method === 'listSnapshots')).toHaveLength(2);
    expect(container.querySelector('[aria-current="location"]')).toBeNull();
    expect(container.querySelector('.banner-error')?.textContent).toContain('残留文件暂时被占用');
    expect(container.textContent).toContain('删除未完成，等待清理');
    const buttons = Array.from(container.querySelectorAll<HTMLButtonElement>('.snapshot-list button'));
    expect(buttons.find(button => button.textContent === '恢复')?.disabled).toBe(true);
    expect(buttons.find(button => button.textContent === '链接克隆')?.disabled).toBe(true);
    expect(buttons.find(button => button.textContent === '重试清理')?.disabled).toBe(false);
  } finally {
    await act(async () => { root.unmount(); });
    container.remove();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  }
});

it('按真实父子关系展示层级并标记当前工作位置', async () => {
  const coreCall = vi.fn(async (method: string) => {
    if (method === 'listSnapshots') return [
      {
        uuid: 'child', parent: 'root', name: '子分支', description: null,
        createdAt: '2026-10-18T00:02:00Z', hasMemory: false, hidden: false,
        cleanupPending: false, isCurrent: false,
      },
      {
        uuid: 'root', parent: null, name: '根快照', description: null,
        createdAt: '2026-10-18T00:01:00Z', hasMemory: false, hidden: false,
        cleanupPending: false, isCurrent: true,
      },
    ];
    throw new Error(`Unexpected method: ${method}`);
  });
  vi.stubGlobal('grassvm', { coreCall });
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true);
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  try {
    const { SnapshotsDialog } = await import('./SnapshotsDialog');
    const vm: VmSummary = {
      path: '/library/test.grassvm', name: '测试虚拟机', osProfileId: 'ubuntu',
      state: 'stopped', cpuCores: 2, memoryMiB: 1024, locked: false, hasAutostart: false,
    };
    await act(async () => { root.render(<SnapshotsDialog vm={vm} onClose={() => undefined} />); });

    const rows = Array.from(container.querySelectorAll('.snapshot-list li'));
    expect(rows.map(row => [row.getAttribute('data-depth'), row.textContent?.includes('你在这里')])).toEqual([
      ['0', true], ['1', false],
    ]);
    expect(rows[1].getAttribute('style')).toContain('24px');
  } finally {
    await act(async () => { root.unmount(); });
    container.remove();
    vi.unstubAllGlobals();
  }
});

it('重新打开面板后仍可重试清理，且不能恢复或克隆待清理快照', async () => {
  let deleted = false;
  const coreCall = vi.fn(async (method: string) => {
    if (method === 'listSnapshots') return deleted ? [] : [{
      uuid: 'pending-snapshot', parent: null, name: '待清理检查点', description: null,
      createdAt: '2026-10-18T00:00:00Z', hasMemory: false, hidden: false, cleanupPending: true, isCurrent: false,
    }];
    if (method === 'planDeleteSnapshot') return {
      cleanupOnly: true, isChainRoot: false, requiresMerge: false, keepsBase: false,
      affectedLinkedClones: [], rebindings: [],
    };
    if (method === 'deleteSnapshot') {
      deleted = true;
      return {};
    }
    throw new Error(`Unexpected method: ${method}`);
  });
  vi.stubGlobal('grassvm', { coreCall });
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true);
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  try {
    const { SnapshotsDialog } = await import('./SnapshotsDialog');
    const vm: VmSummary = {
      path: '/library/test.grassvm', name: '测试虚拟机', osProfileId: 'ubuntu',
      state: 'stopped', cpuCores: 2, memoryMiB: 1024, locked: false, hasAutostart: false,
    };
    await act(async () => { root.render(<SnapshotsDialog vm={vm} onClose={() => undefined} />); });
    const buttons = Array.from(container.querySelectorAll('button'));
    expect(buttons.find(button => button.textContent === '恢复')?.disabled).toBe(true);
    expect(buttons.find(button => button.textContent === '链接克隆')?.disabled).toBe(true);
    const retry = buttons.find(button => button.textContent === '重试清理');
    expect(retry?.disabled).toBe(false);
    await act(async () => { retry?.click(); });
    expect(confirm).toHaveBeenCalledTimes(1);
    const confirmation = confirm.mock.calls[0][0];
    expect(confirmation).toContain('继续完成已确认的快照删除及残留文件清理');
    expect(confirmation).not.toContain('快照已删除');
    expect(confirmation).not.toContain('链接克隆将失效');
    expect(coreCall).toHaveBeenCalledWith('planDeleteSnapshot', {
      packagePath: vm.path, uuid: 'pending-snapshot',
    });
    expect(coreCall).toHaveBeenCalledWith('deleteSnapshot', {
      packagePath: vm.path, uuid: 'pending-snapshot',
    });
    expect(container.querySelectorAll('.snapshot-list li')).toHaveLength(0);
  } finally {
    await act(async () => { root.unmount(); });
    container.remove();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  }
});

it.each([
  { label: '链根保留', isChainRoot: true, requiresMerge: false, keepsBase: false, cleanupOnly: false },
  { label: '部分基座保留', isChainRoot: false, requiresMerge: false, keepsBase: true, cleanupOnly: false },
  { label: '合并并保留基座', isChainRoot: false, requiresMerge: true, keepsBase: true, cleanupOnly: false },
  { label: '合并后删除', isChainRoot: false, requiresMerge: true, keepsBase: false, cleanupOnly: false },
  { label: '直接丢弃', isChainRoot: false, requiresMerge: false, keepsBase: false, cleanupOnly: false },
  { label: '继续删除与清理', isChainRoot: false, requiresMerge: false, keepsBase: false, cleanupOnly: true },
])('$label时明确警告链接克隆失效，且确认后才删除', async ({ label, ...plan }) => {
  const coreCall = vi.fn(async (method: string) => {
    if (method === 'listSnapshots') return [{
      uuid: 'clone-baseline', parent: null, name: label, description: null,
      createdAt: '2026-10-18T00:00:00Z', hasMemory: false, hidden: false, isCurrent: false,
      cleanupPending: plan.cleanupOnly,
    }];
    if (method === 'planDeleteSnapshot') return {
      ...plan,
      affectedLinkedClones: [
        { childVmName: '测试克隆一', childVmPath: '/library/clone-one.grassvm' },
        { childVmName: '测试克隆二', childVmPath: '/library/clone-two.grassvm' },
      ],
      rebindings: [],
    };
    if (method === 'deleteSnapshot') return {};
    throw new Error(`Unexpected method: ${method}`);
  });
  vi.stubGlobal('grassvm', { coreCall });
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true);
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  try {
    const { SnapshotsDialog } = await import('./SnapshotsDialog');
    const vm: VmSummary = {
      path: '/library/test.grassvm', name: '测试虚拟机', osProfileId: 'ubuntu',
      state: 'stopped', cpuCores: 2, memoryMiB: 1024, locked: false, hasAutostart: false,
    };
    await act(async () => { root.render(<SnapshotsDialog vm={vm} onClose={() => undefined} />); });
    const remove = Array.from(container.querySelectorAll('button'))
      .find(button => button.textContent === (plan.cleanupOnly ? '重试清理' : '删除'));
    expect(remove).toBeDefined();
    await act(async () => { remove?.click(); });
    expect(confirm).toHaveBeenCalledTimes(1);
    const confirmation = confirm.mock.calls[0][0];
    expect(confirmation).toContain('依赖此快照的链接克隆：测试克隆一、测试克隆二');
    expect(confirmation).toContain('链接克隆将失效，无法再启动，即使底层磁盘文件仍然保留');
    expect(confirmation).toContain('请先把克隆转换为完整克隆');
    expect(confirmation).not.toContain('克隆不受影响');
    if (plan.cleanupOnly) {
      expect(confirmation).toContain('继续完成已确认的快照删除及残留文件清理');
      expect(confirmation).not.toContain('快照已删除');
    }
    expect(coreCall.mock.calls.filter(([method]) => method === 'deleteSnapshot')).toHaveLength(0);
    confirm.mockReturnValue(true);
    await act(async () => { remove?.click(); });
    expect(confirm).toHaveBeenCalledTimes(2);
    expect(coreCall).toHaveBeenCalledWith('deleteSnapshot', {
      packagePath: vm.path, uuid: 'clone-baseline',
    });
    expect(coreCall.mock.calls.filter(([method]) => method === 'deleteSnapshot')).toHaveLength(1);
  } finally {
    await act(async () => { root.unmount(); });
    container.remove();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  }
});
