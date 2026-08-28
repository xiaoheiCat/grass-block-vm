/**
 * UI ↔ GrassCore 的共享契约（与 GrassCore C# 端 JSON-RPC DTO 对应）。
 * UI 永远不直接写 .grassvm；所有写操作都经过 GrassCore。
 */

export type VmRunState =
  | 'stopped'    // 已关机
  | 'starting'   // 正在启动
  | 'running'    // 正在运行
  | 'suspending' // 正在挂起
  | 'suspended'; // 已挂起

export interface VmSummary {
  /** .grassvm 包根路径——这台 VM 的身份 */
  path: string;
  name: string;
  osProfileId: string;
  state: VmRunState;
  cpuCores: number;
  memoryMiB: number;
  locked: boolean;
  hasAutostart: boolean;
}

export interface OsProfileDto {
  id: string;
  name: string;
  family: 'windows' | 'linux' | 'other';
  verified: boolean;
  cpu: number;
  memoryMiB: number;
  diskGiB: number;
}

/** 创建向导的最终提交：Core 负责落盘一切（目录、QCOW2、config、NVRAM） */
export interface CreateVmRequest {
  name: string;
  profileId: string;
  diskGiB: number;
  cpuCores: number;
  memoryMiB: number;
  isoPath: string | null;
  /** "创建后立即启动"（默认开） */
  startAfterCreate: boolean;
}

/** 电源动作：UI 只暴露 正常关机 / 强制关机(二次确认) / 挂起 / 恢复 */
export type PowerAction = 'shutdown' | 'forceOff' | 'suspend';

/** 关闭显示器窗口时的四选项（不提供"记住此选择"） */
export type CloseDisplayChoice = 'background' | 'shutdown' | 'suspend' | 'cancel';

/** JSON-RPC 2.0 帧格式（与 GrassCore 的 4 字节长度前缀协议对应） */
export interface JsonRpcRequest {
  jsonrpc: '2.0';
  id: number;
  method: string;
  params?: unknown;
}
