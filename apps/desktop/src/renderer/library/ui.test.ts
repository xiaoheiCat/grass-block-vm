import { describe, expect, it } from 'vitest';
import {
  forceOffConfirmText,
  memoryStepMiB,
  osBadge,
  primaryAction,
  primaryActionLabel,
  stateLabel,
  validateWizard,
  vmSubtitle,
} from './vm-state';
import type { VmSummary } from '../../shared/contract';
import {
  advance,
  canAdvance,
  defaultVmName,
  goBack,
  initialWizardState,
  selectProfile,
  sortProfilesForDisplay,
  toCreateRequest,
} from './wizard-state';
import { shouldShowHelperWarning, helperTooltip } from '../display/helper-state';
import type { OsProfileDto } from '../../shared/contract';

describe('vm-state：卡片主操作与状态标签', () => {
  it('关机→启动；挂起→恢复；运行→打开显示器；过渡态无操作', () => {
    expect(primaryAction('stopped')).toBe('start');
    expect(primaryAction('suspended')).toBe('resume');
    expect(primaryAction('running')).toBe('open-display');
    expect(primaryAction('starting')).toBeNull();
    expect(primaryAction('suspending')).toBeNull();
  });

  it('状态标签为中文且完整覆盖', () => {
    expect(stateLabel('stopped')).toBe('已关机');
    expect(stateLabel('running')).toBe('正在运行');
    expect(stateLabel('suspended')).toBe('已挂起');
  });

  it('主按钮文案与动作一一对应', () => {
    expect(primaryActionLabel('start')).toBe('启动');
    expect(primaryActionLabel('resume')).toBe('恢复');
    expect(primaryActionLabel('open-display')).toBe('打开显示器');
    expect(primaryActionLabel(null)).toBe('');
  });

  it('强制关机确认文案讲清"拔电源线"后果', () => {
    const t = forceOffConfirmText();
    expect(t).toContain('拔掉电源线');
    expect(t).toContain('仅在虚拟机无响应时使用');
  });

  it('卡片副标题：整 GB 不带小数', () => {
    const vm = { cpuCores: 4, memoryMiB: 8192 } as VmSummary;
    expect(vmSubtitle(vm)).toBe('4 核 CPU · 8 GB 内存');
    const vm2 = { cpuCores: 2, memoryMiB: 2560 } as VmSummary;
    expect(vmSubtitle(vm2)).toBe('2 核 CPU · 2.5 GB 内存');
  });

  it('OS 徽标按 profile 家族', () => {
    expect(osBadge('windows-11').emoji).toBe('🪟');
    expect(osBadge('ubuntu').emoji).toBe('🐧');
    expect(osBadge('other').emoji).toBe('📦');
  });
});

describe('vm-state：向导校验与内存步进', () => {
  it('名称非法字符与 ISO 缺失都阻止前进', () => {
    expect(validateWizard('My VM', 'C:/a.iso')).toBeNull();
    expect(validateWizard('', 'C:/a.iso')).toContain('名字');
    expect(validateWizard('bad:name', 'C:/a.iso')).toContain('名称');
    expect(validateWizard('ok', null)).toContain('ISO');
    expect(validateWizard('ok', 'C:/a.img')).toContain('.iso');
  });

  it('内存步进以 0.5GB 为步、宿主一半为上限', () => {
    const host = 16 * 1024;
    expect(memoryStepMiB(host, 2048, 512)).toBe(2560);
    expect(memoryStepMiB(host, 8192 + 512, 512)).toBe(8192); // clamp 到一半
    expect(memoryStepMiB(1024, 512, -512)).toBe(512); // 下限
  });
});

const profiles: OsProfileDto[] = [
  { id: 'other', name: '其他系统', family: 'other', verified: false, cpu: 1, memoryMiB: 2048, diskGiB: 20 },
  { id: 'ubuntu', name: 'Ubuntu', family: 'linux', verified: true, cpu: 4, memoryMiB: 4096, diskGiB: 40 },
  { id: 'windows-11', name: 'Windows 11', family: 'windows', verified: true, cpu: 4, memoryMiB: 8192, diskGiB: 80 },
  { id: 'windows-xp', name: 'Windows XP', family: 'windows', verified: false, cpu: 1, memoryMiB: 1024, diskGiB: 20 },
];

describe('wizard-state：四步向导', () => {
  it('初始默认 Windows 11（推荐路径最短）', () => {
    const s = initialWizardState(profiles);
    expect(s.selectedProfileId).toBe('windows-11');
    expect(s.memoryMiB).toBe(8192);
    expect(s.step).toBe('os');
  });

  it('选择 Profile 后资源默认值跟随', () => {
    const s = selectProfile(initialWizardState(profiles), 'ubuntu');
    expect(s.cpuCores).toBe(4);
    expect(s.memoryMiB).toBe(4096);
    expect(s.diskGiB).toBe(40);
  });

  it('OS 步必须先选系统；ISO 步必须有 .iso', () => {
    const s = initialWizardState(profiles);
    expect(canAdvance({ ...s, selectedProfileId: null })).toBe(false);
    expect(canAdvance({ ...s })).toBe(true);
    expect(canAdvance({ ...s, step: 'iso', isoPath: null })).toBe(false);
    expect(canAdvance({ ...s, step: 'iso', isoPath: 'D:/x.iso' })).toBe(true);
    expect(canAdvance({ ...s, step: 'iso', isoPath: 'D:/x.img' })).toBe(false);
  });

  it('advance/goBack 按步骤走且不能跳步', () => {
    let s = initialWizardState(profiles);
    expect(advance({ ...s, selectedProfileId: null }).step).toBe('os'); // 卡住
    s = advance(s);
    expect(s.step).toBe('iso');
    s = goBack(s);
    expect(s.step).toBe('os');
    s = { ...advance(s), isoPath: '/iso/ubuntu.iso' };
    s = advance(s);
    expect(s.step).toBe('config');
    s = advance(s);
    expect(s.step).toBe('confirm');
    expect(advance(s).step).toBe('confirm'); // 最后一步不再前进
  });

  it('Profile 排序：已验证优先，Windows 在前', () => {
    const sorted = sortProfilesForDisplay(profiles);
    expect(sorted[0].id).toBe('windows-11');
    expect(sorted.map((p) => p.id)).toEqual(['windows-11', 'ubuntu', 'windows-xp', 'other']);
  });

  it('默认 VM 名自动补可用序号', () => {
    expect(defaultVmName([], 'Windows 11')).toBe('Windows 11');
    expect(defaultVmName(['Windows 11'], 'Windows 11')).toBe('Windows 11 2');
    expect(defaultVmName(['Windows 11', 'Windows 11 2'], 'Windows 11')).toBe('Windows 11 3');
  });

  it('toCreateRequest：空名称回退默认名', () => {
    const s = { ...initialWizardState(profiles), isoPath: '/iso/win11.iso' };
    const req = toCreateRequest(s, 'Windows 11');
    expect(req).not.toBeNull();
    expect(req!.name).toBe('Windows 11');
    expect(req!.startAfterCreate).toBe(true);
    expect(req!.profileId).toBe('windows-11');
  });
});

describe('helper-state：客户机帮助程序安静原则', () => {
  it('正常时完全安静（无提示）；能力不可用时才警告', () => {
    expect(shouldShowHelperWarning(true)).toBe(false);
    expect(shouldShowHelperWarning(false)).toBe(true);
    expect(helperTooltip(true)).toBeNull();
    const tip = helperTooltip(false)!;
    expect(tip).toContain('自动调整分辨率');
    expect(tip).toContain('共享剪贴板');
    expect(shouldShowHelperWarning('connected', false)).toBe(true);
    expect(helperTooltip('connected', false)).toContain('Guest Tools');
    expect(helperTooltip('disconnected')).toContain('已安装');
  });
});
