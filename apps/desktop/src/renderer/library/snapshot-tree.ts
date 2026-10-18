export interface SnapshotTreeEntry {
  uuid: string;
  parent: string | null;
  name: string;
  createdAt: string;
  hidden: boolean;
  cleanupPending?: boolean;
  isCurrent: boolean;
}

export interface SnapshotTreeRow<T extends SnapshotTreeEntry = SnapshotTreeEntry> {
  snapshot: T;
  depth: number;
  treeIssue?: 'missing-parent' | 'cleanup-parent' | 'cycle';
}

function compareSnapshots(a: SnapshotTreeEntry, b: SnapshotTreeEntry): number {
  const aTime = Date.parse(a.createdAt);
  const bTime = Date.parse(b.createdAt);
  if (Number.isFinite(aTime) && Number.isFinite(bTime) && aTime !== bTime) return aTime - bTime;
  if (a.createdAt !== b.createdAt) return a.createdAt < b.createdAt ? -1 : 1;
  if (a.uuid !== b.uuid) return a.uuid < b.uuid ? -1 : 1;
  return a.name < b.name ? -1 : a.name > b.name ? 1 : 0;
}

function key(uuid: string): string {
  return uuid.toLowerCase();
}

export function buildSnapshotTreeRows<T extends SnapshotTreeEntry>(entries: readonly T[]): SnapshotTreeRow<T>[] {
  const sorted = entries
    .map((entry, index) => ({ entry, index }))
    .sort((a, b) => compareSnapshots(a.entry, b.entry) || a.index - b.index)
    .map(({ entry }) => entry);
  const snapshots = sorted.filter((entry) => !entry.cleanupPending);
  const pending = sorted.filter((entry) => entry.cleanupPending);
  const snapshotByUuid = new Map<string, T>();
  const pendingUuids = new Set(pending.map((entry) => key(entry.uuid)));
  const rank = new Map<T, number>();
  for (const snapshot of snapshots) {
    if (!snapshotByUuid.has(key(snapshot.uuid))) snapshotByUuid.set(key(snapshot.uuid), snapshot);
    rank.set(snapshot, rank.size);
  }

  const parentBySnapshot = new Map<T, T | undefined>();
  const rootIssues = new Map<T, SnapshotTreeRow<T>['treeIssue']>();
  for (const snapshot of snapshots) {
    if (!snapshot.parent) {
      parentBySnapshot.set(snapshot, undefined);
      continue;
    }
    const parent = snapshotByUuid.get(key(snapshot.parent));
    if (!parent) {
      parentBySnapshot.set(snapshot, undefined);
      rootIssues.set(snapshot, pendingUuids.has(key(snapshot.parent)) ? 'cleanup-parent' : 'missing-parent');
      continue;
    }
    parentBySnapshot.set(snapshot, parent);
  }

  const processed = new Set<T>();
  const cycleRoots = new Set<T>();
  for (const start of snapshots) {
    if (processed.has(start)) continue;
    const path: T[] = [];
    const pathIndex = new Map<T, number>();
    let current: T | undefined = start;
    while (current && !processed.has(current) && !pathIndex.has(current)) {
      pathIndex.set(current, path.length);
      path.push(current);
      current = parentBySnapshot.get(current);
    }
    if (current && pathIndex.has(current)) {
      const cycle = path.slice(pathIndex.get(current)!);
      const cycleRoot = cycle.reduce((best, candidate) =>
        (rank.get(candidate) ?? Number.MAX_SAFE_INTEGER) < (rank.get(best) ?? Number.MAX_SAFE_INTEGER)
          ? candidate
          : best,
      );
      parentBySnapshot.set(cycleRoot, undefined);
      rootIssues.set(cycleRoot, 'cycle');
      cycleRoots.add(cycleRoot);
    }
    for (const snapshot of path) processed.add(snapshot);
  }

  const children = new Map<T, T[]>();
  const roots: T[] = [];
  for (const snapshot of snapshots) {
    const parent = parentBySnapshot.get(snapshot);
    if (!parent) {
      roots.push(snapshot);
      continue;
    }
    const siblings = children.get(parent) ?? [];
    siblings.push(snapshot);
    children.set(parent, siblings);
  }

  const rows: SnapshotTreeRow<T>[] = [];
  const visited = new Set<T>();
  const visit = (forest: readonly T[]) => {
    const stack = forest
      .slice()
      .reverse()
      .map((snapshot) => ({ snapshot, parentDepth: -1, inheritedIssue: undefined as SnapshotTreeRow<T>['treeIssue'] }));
    while (stack.length > 0) {
      const frame = stack.pop()!;
      const { snapshot } = frame;
      if (visited.has(snapshot)) continue;
      visited.add(snapshot);
      const issue = frame.inheritedIssue ?? rootIssues.get(snapshot);
      const depth = frame.parentDepth + 1;
      rows.push({ snapshot, depth, treeIssue: issue });
      const childParentDepth = depth;
      const snapshotChildren = children.get(snapshot) ?? [];
      for (let index = snapshotChildren.length - 1; index >= 0; index -= 1) {
        stack.push({
          snapshot: snapshotChildren[index],
          parentDepth: childParentDepth,
          inheritedIssue: undefined,
        });
      }
    }
  };

  visit(roots);
  for (const snapshot of snapshots) {
    if (!visited.has(snapshot)) {
      if (!cycleRoots.has(snapshot)) rootIssues.set(snapshot, rootIssues.get(snapshot) ?? 'cycle');
      visit([snapshot]);
    }
  }
  for (const snapshot of pending) rows.push({ snapshot, depth: 0 });
  return rows;
}
