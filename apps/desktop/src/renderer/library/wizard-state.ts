/**
 * 创建向导状态机（纯逻辑，vitest 覆盖）。
 * 四步：选择系统 → 选择安装镜像 → 确认配置（默认=Profile 推荐）→ 创建。
 * 原则：默认聪明——80% 用户一路"下一步"即可得到合理配置。
 */
import type { CreateVmRequest, OsProfileDto } from '../../shared/contract';

export const WIZARD_STEPS = ['os', 'iso', 'config', 'confirm'] as const;
export type WizardStep = (typeof WIZARD_STEPS)[number];

export interface WizardState {
  step: WizardStep;
  profiles: OsProfileDto[];
  selectedProfileId: string | null;
  vmName: string;
  isoPath: string | null;
  cpuCores: number;
  memoryMiB: number;
  diskGiB: number;
  startAfterCreate: boolean;
}

export function initialWizardState(profiles: OsProfileDto[]): WizardState {
  const recommended = profiles.find((p) => p.id === 'windows-11') ?? profiles[0] ?? null;
  return {
    step: 'os',
    profiles,
    selectedProfileId: recommended?.id ?? null,
    vmName: '',
    isoPath: null,
    cpuCores: recommended?.cpu ?? 2,
    memoryMiB: recommended?.memoryMiB ?? 2048,
    diskGiB: recommended?.diskGiB ?? 40,
    startAfterCreate: true,
  };
}

/** 选择 Profile → 资源默认值跟随 Profile（用户仍可在配置页覆盖）。 */
export function selectProfile(state: WizardState, profileId: string): WizardState {
  const p = state.profiles.find((x) => x.id === profileId);
  if (!p) return state;
  return {
    ...state,
    selectedProfileId: profileId,
    cpuCores: p.cpu,
    memoryMiB: p.memoryMiB,
    diskGiB: p.diskGiB,
    // 名字留空跟随还是自动补全：不自动填——避免一排同名 VM
  };
}

/** Profile 排序：已验证优先，Windows 家族在前（大多数用户的默认路径最短）。 */
export function sortProfilesForDisplay(profiles: OsProfileDto[]): OsProfileDto[] {
  return [...profiles].sort((a, b) => {
    if (a.verified !== b.verified) return a.verified ? -1 : 1;
    const famOrder = (f: string) => (f === 'windows' ? 0 : f === 'linux' ? 1 : 2);
    if (famOrder(a.family) !== famOrder(b.family)) return famOrder(a.family) - famOrder(b.family);
    return a.name.localeCompare(b.name);
  });
}

export function canAdvance(state: WizardState): boolean {
  switch (state.step) {
    case 'os':
      return state.selectedProfileId != null;
    case 'iso':
      return state.isoPath != null && state.isoPath.toLowerCase().endsWith('.iso');
    case 'config':
      return state.cpuCores >= 1 && state.memoryMiB >= 512 && state.diskGiB >= 8;
    case 'confirm':
      return true;
  }
}

export function advance(state: WizardState): WizardState {
  const i = WIZARD_STEPS.indexOf(state.step);
  if (i < 0 || i === WIZARD_STEPS.length - 1 || !canAdvance(state)) return state;
  return { ...state, step: WIZARD_STEPS[i + 1] };
}

export function goBack(state: WizardState): WizardState {
  const i = WIZARD_STEPS.indexOf(state.step);
  if (i <= 0) return state;
  return { ...state, step: WIZARD_STEPS[i - 1] };
}

export function toCreateRequest(state: WizardState, fallbackName: string): CreateVmRequest | null {
  if (!state.selectedProfileId) return null;
  const name = state.vmName.trim() || fallbackName;
  return {
    name,
    profileId: state.selectedProfileId,
    diskGiB: state.diskGiB,
    cpuCores: state.cpuCores,
    memoryMiB: state.memoryMiB,
    isoPath: state.isoPath,
    startAfterCreate: state.startAfterCreate,
  };
}

/** 默认 VM 名：Profile 名 + 可用序号（Windows 11 2 / Windows 11 3 …）。 */
export function defaultVmName(existingNames: ReadonlyArray<string>, profileName: string): string {
  if (!existingNames.some((n) => n === profileName)) return profileName;
  let i = 2;
  while (existingNames.some((n) => n === `${profileName} ${i}`)) i++;
  return `${profileName} ${i}`;
}
