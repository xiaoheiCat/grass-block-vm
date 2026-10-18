import { describe, expect, it } from 'vitest';
import { isAutostartLaunch } from './startup-mode';

describe('Electron 启动模式', () => {
  it('只在显式 --autostart 参数存在时进入无窗口自启动模式', () => {
    expect(isAutostartLaunch(['Grass Block VM.exe', '--autostart'])).toBe(true);
    expect(isAutostartLaunch(['Grass Block VM.exe'])).toBe(false);
    expect(isAutostartLaunch(['Grass Block VM.exe', '--autostart-disabled'])).toBe(false);
  });
});
