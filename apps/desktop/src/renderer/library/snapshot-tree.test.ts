import { describe, expect, it } from 'vitest';
import { buildSnapshotTreeRows, type SnapshotTreeEntry } from './snapshot-tree';

function snapshot(
  uuid: string,
  parent: string | null,
  options: Partial<SnapshotTreeEntry> = {},
): SnapshotTreeEntry {
  return {
    uuid,
    parent,
    name: uuid,
    createdAt: '2026-10-18T00:00:00Z',
    hidden: false,
    isCurrent: false,
    ...options,
  };
}

describe('buildSnapshotTreeRows', () => {
  it('保留真实分支、系统节点与当前位置', () => {
    const rows = buildSnapshotTreeRows([
      snapshot('branch-b', 'root'),
      snapshot('hidden-base', 'root', { hidden: true }),
      snapshot('branch-a', 'root', { isCurrent: true }),
      snapshot('hidden-child', 'hidden-base', { isCurrent: true }),
      snapshot('root', null),
      snapshot('branch-b-child', 'branch-b'),
    ]);

    expect(rows.map(({ snapshot: item, depth }) => [item.uuid, depth])).toEqual([
      ['root', 0],
      ['branch-a', 1],
      ['branch-b', 1],
      ['branch-b-child', 2],
      ['hidden-base', 1],
      ['hidden-child', 2],
    ]);
    expect(rows.filter(({ snapshot: item }) => item.isCurrent).map(({ snapshot: item }) => item.uuid))
      .toEqual(['branch-a', 'hidden-child']);
    expect(rows.some(({ snapshot: item }) => item.uuid === 'hidden-base')).toBe(true);
  });

  it('保留缺失父节点、循环引用及待清理节点，且不把待清理项当父节点', () => {
    const rows = buildSnapshotTreeRows([
      snapshot('missing-parent-child', 'absent'),
      snapshot('cycle-a', 'cycle-b'),
      snapshot('cycle-b', 'cycle-a'),
      snapshot('pending', null, { cleanupPending: true }),
      snapshot('pending-child', 'pending'),
    ]);

    expect(rows.map(({ snapshot: item }) => item.uuid).sort()).toEqual([
      'cycle-a', 'cycle-b', 'missing-parent-child', 'pending', 'pending-child',
    ]);
    expect(rows.find(({ snapshot: item }) => item.uuid === 'missing-parent-child')).toMatchObject({
      depth: 0,
      treeIssue: 'missing-parent',
    });
    expect(rows.find(({ snapshot: item }) => item.uuid === 'pending-child')).toMatchObject({
      depth: 0,
      treeIssue: 'cleanup-parent',
    });
    expect(rows.filter(({ treeIssue }) => treeIssue === 'cycle')).toHaveLength(1);
    expect(rows.every(({ depth }) => Number.isFinite(depth))).toBe(true);
  });
});
