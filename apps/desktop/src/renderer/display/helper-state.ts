/**
 * 客户机帮助程序（底层 = SPICE Guest Tools / spice-vdagent）状态策略：
 * 正常时完全安静（不显示"已安装/正常"）；只有功能不可用时才提示。
 * 检测按实际能力，而不是"装没装过"。
 */
export type HelperState = 'detecting' | 'connected' | 'not-installed' | 'disconnected' | 'stopped';

export function shouldShowHelperWarning(state: HelperState | boolean, guestAgentConnected = true): boolean {
  return typeof state === 'boolean' ? !state : state !== 'connected' || !guestAgentConnected;
}

export function helperTooltip(state: HelperState | boolean, guestAgentConnected = true): string | null {
  // 正常状态保持安静：返回 null 表示什么都不显示
  if ((state === true || state === 'connected') && guestAgentConnected) return null;
  if (state === 'connected' && !guestAgentConnected)
    return '客户机 Guest Tools 尚未连接。缺少它时，自动调整分辨率、剪贴板和文件拖放不可用。';
  if (state === 'detecting') return '正在检测客户机帮助程序能力…';
  if (state === 'stopped') return '虚拟机尚未运行。';
  if (state === 'disconnected')
    return '客户机帮助程序已安装但当前未连接。请重启虚拟机或在客户机内启动 Guest Tools。';
  return (
    '客户机帮助程序未安装。缺少它时，以下功能不可用或体验受限：\n' +
    '· 自动调整分辨率\n· 主机与虚拟机共享剪贴板\n· 鼠标无缝移动\n· 文件拖放'
  );
}

/** 帮助程序安装入口（打开说明页，不弹下载链接列表）。 */
export const HELPER_DOC_ROUTE = 'helper-install';
