/**
 * 设置页（设备化）：处理器与内存 / 硬盘 / CD/DVD / 网络 / 显示器 / 声音 / USB /
 * 共享文件夹 / 摄像头 / 麦克风 / 安全芯片。
 * 原则：界面展示 = 当前事实。运行中不可修改的设备直接锁定；不存在"待应用"。
 * 设备按添加时间稳定排序显示（磁盘 #1 / 网络 #2…编号不漂移）。
 */
import React, { useState } from 'react';
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
] as const;

export function VmSettings(props: { vm: VmSummary; onClose(): void }): React.ReactElement {
  const [selected, setSelected] = useState<(typeof DEVICE_ORDER)[number]>('cpu-memory');
  const running = props.vm.state === 'running' || props.vm.state === 'suspending';

  return (
    <div className="modal-backdrop">
      <div className="settings">
        <header className="settings-header">
          <h2>{props.vm.name} 的设置</h2>
          <button className="btn-ghost" onClick={props.onClose}>
            完成
          </button>
        </header>
        {running && (
          <div className="banner-quiet">
            这台虚拟机正在运行。运行期间无法修改的设置已锁定，正常关机后即可更改。
          </div>
        )}
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
          <section className="settings-pane">{renderPane(selected, running)}</section>
        </div>
      </div>
    </div>
  );
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
  }
}

function renderPane(d: (typeof DEVICE_ORDER)[number], locked: boolean): React.ReactNode {
  const lock = locked ? <p className="lock-note">🔒 运行中无法修改，正常关机后可调整。</p> : null;
  switch (d) {
    case 'cpu-memory':
      return (
        <>
          {lock}
          <p>处理器与内存在虚拟机关机后可调整。调整后立即生效，无需重装系统。</p>
        </>
      );
    case 'disk':
      return (
        <>
          {lock}
          <p>硬盘容量只能扩大，不能缩小。扩大后需要在客户机内自行扩展分区。</p>
        </>
      );
    case 'cdrom':
      return <p>CD/DVD 可以在运行中更换或取出镜像（即插即用）。</p>;
    case 'network':
      return (
        <>
          {lock}
          <p>每张网卡可独立选择：NAT（默认，开箱即用）/ 桥接 / Host-only / 断开。</p>
        </>
      );
    case 'display':
      return <p>共享剪贴板与文件拖放默认开启；需要客户机帮助程序支持，缺失时自动降级。</p>;
    case 'audio':
      return <p>声音默认开启，底层输出自动选择。</p>;
    case 'usb':
      return <p>宿主 USB 设备从显示器窗口的设备栏"连接到此虚拟机"，运行中即可操作。</p>;
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
