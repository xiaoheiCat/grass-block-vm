/**
 * VM 状态与卡片行为的纯逻辑（vitest 覆盖）。
 * 产品规则：卡片上单一主操作；正常状态安静（"已安装客户机帮助程序"之类不显示）。
 */
import type { VmRunState, VmSummary } from '../../shared/contract';

export function stateLabel(state: VmRunState): string {
  switch (state) {
    case 'stopped':
      return '已关机';
    case 'starting':
      return '正在启动';
    case 'running':
      return '正在运行';
    case 'suspending':
      return '正在挂起';
    case 'suspended':
      return '已挂起';
  }
}

/** 卡片唯一主按钮：关机→启动；挂起→恢复；运行→打开显示器；过渡态→无操作。 */
export function primaryAction(state: VmRunState): 'start' | 'resume' | 'open-display' | null {
  switch (state) {
    case 'stopped':
      return 'start';
    case 'suspended':
      return 'resume';
    case 'running':
      return 'open-display';
    default:
      return null;
  }
}

export function primaryActionLabel(action: ReturnType<typeof primaryAction>): string {
  switch (action) {
    case 'start':
      return '启动';
    case 'resume':
      return '恢复';
    case 'open-display':
      return '打开显示器';
    default:
      return '';
  }
}

/** 电源菜单：正常关机永远是第一项；强制关机永远带二次确认（不放默认焦点）。 */
export const POWER_MENU_ORDER = ['shutdown', 'suspend', 'forceOff'] as const;

export function powerMenuItems(): ReadonlyArray<(typeof POWER_MENU_ORDER)[number]> {
  return POWER_MENU_ORDER;
}

export function forceOffConfirmText(): string {
  return (
    '强制关机会立即停止虚拟机，这相当于给一台正在运行的电脑直接拔掉电源线，' +
    '可能导致未保存的数据丢失、文件系统损坏，甚至造成不可逆的问题。仅在虚拟机无响应时使用。'
  );
}

/** 卡片角标信息：cpu/内存摘要（磁盘大小不占卡片空间）。 */
export function vmSubtitle(vm: VmSummary): string {
  return `${vm.cpuCores} 核 CPU · ${(vm.memoryMiB / 1024).toFixed(vm.memoryMiB % 1024 === 0 ? 0 : 1)} GB 内存`;
}

export function osBadge(osProfileId: string): { emoji: string; label: string } {
  if (osProfileId.startsWith('windows')) return { emoji: '🪟', label: 'Windows' };
  if (['ubuntu', 'debian', 'fedora', 'arch'].includes(osProfileId)) return { emoji: '🐧', label: 'Linux' };
  return { emoji: '📦', label: '其他系统' };
}

/** 向导校验：名称非空且不含文件系统非法字符；ISO 必须选择（1.0 常规安装流）。 */
export function validateWizard(name: string, isoPath: string | null): string | null {
  if (!name.trim()) return '请给虚拟机起个名字。';
  if (/[\\/:*?"<>|]/.test(name)) return '名称不能包含这些字符：\\ / : * ? " < > |';
  if (name.includes(',')) return '名称不能包含逗号。';
  if (!isoPath) return '请选择一个安装光盘镜像（ISO）。';
  if (!isoPath.toLowerCase().endsWith('.iso')) return '请选择 .iso 格式的安装镜像。';
  return null;
}

/** 向导内存步进：0.5 GB 步进，上限 = 宿主物理内存的一半（默认值来自 Profile 推荐）。 */
export function memoryStepMiB(hostTotalMiB: number, currentMiB: number, delta: number): number {
  const maxMiB = Math.floor((hostTotalMiB / 2) / 512) * 512;
  return Math.min(Math.max(currentMiB + delta, 512), Math.max(maxMiB, 512));
}
