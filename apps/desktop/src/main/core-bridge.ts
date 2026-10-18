/**
 * GrassCore 进程桥：
 * - 按需启动 GrassCore.exe（打开 UI 时拉起；UI 退出但仍有 VM 运行时 Core 继续存活）
 * - Windows Named Pipe + JSON-RPC（不占 TCP 端口；权限交给 Windows ACL）
 * - 非 Windows 开发机：GrassCore 以 stdio 承载同一协议（便于本机联调）
 * - UI 崩溃不杀 Core；Core 崩溃不杀 QEMU（Core 侧负责无损重接管）
 */
import { spawn, execFileSync, type ChildProcess } from 'node:child_process';
import net from 'node:net';
import os from 'node:os';
import crypto from 'node:crypto';
import { EventEmitter } from 'node:events';

function currentUserIdentity(): string {
  if (process.platform === 'win32') {
    try {
      const output = execFileSync('whoami.exe', ['/user'], {
        encoding: 'utf8',
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'ignore'],
      });
      const sid = output.match(/\bS-\d-\d+(?:-\d+)+\b/)?.[0];
      if (sid) return sid;
    } catch {
      // 受限环境中 whoami 可能不可用；Core 端也有同样的用户名回退。
    }
  }
  return os.userInfo().username;
}

const userKey = crypto.createHash('sha256')
  .update(currentUserIdentity().toUpperCase(), 'utf8')
  .digest('hex')
  .slice(0, 16);
const PIPE_NAME = `\\\\.\\pipe\\grassvm-core-${userKey}`;
const MAX_FRAME_BYTES = 16 * 1024 * 1024;

type BridgeChannel = { writable: NodeJS.WritableStream; readable: NodeJS.ReadableStream };

export class CoreBridge extends EventEmitter {
  private proc: ChildProcess | null = null;
  private channelProc: ChildProcess | null = null;
  private nextId = 1;
  private pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
  /** 单飞：并发首调共享同一个连接尝试（否则会孵出多个 GrassCore 进程，双重接管） */
  private connecting: Promise<void> | null = null;
  /** 单飞：一次重连只触发一次运行中 VM 接管，避免并发 RPC 重复扫描状态。 */
  private adopting: Promise<void> | null = null;

  constructor(private coreExe: string) {
    super();
  }

  /** 连接（必要时先拉起 GrassCore 进程）。 */
  async ensureRunning(): Promise<void> {
    if (this.connecting) return this.connecting;
    this.connecting = this.connectInner().finally(() => {
      this.connecting = null;
    });
    return this.connecting;
  }

  private async connectInner(): Promise<void> {
    if (process.platform === 'win32') {
      // Windows：先试连接既有 Core（UI 重开时复用），失败再拉起
      try {
        await this.connectNamedPipe(PIPE_NAME);
        return;
      } catch {
        const spawned = this.spawnCore();
        for (let i = 0; i < 50; i++) {
          await sleep(200);
          try {
            // 命名管道连接不携带服务端进程身份：即使刚启动的候选进程
            // 触发了连接，也可能实际接入了已经存在的 Core（候选进程
            // 可能因单实例 mutex 直接退出）。因此不能把 spawned 绑定为
            // 通道所有者，否则通道关闭时会误杀不属于本桥的 Core。
            await this.connectNamedPipe(PIPE_NAME);
            return;
          } catch {
            /* 重试 */
          }
        }
        if (this.proc === spawned && !spawned.killed) {
          try { spawned.kill(); } catch { /* 进程可能已退出 */ }
        }
        throw new Error('无法连接 GrassCore。');
      }
    } else {
      // 开发机：stdio 直连
      const proc = spawn(this.coreExe, [], { stdio: ['pipe', 'pipe', 'inherit'] });
      this.proc = proc;
      const channel = this.wire(proc.stdin!, proc.stdout!, proc);
      // 进程事件可能滞后到重连之后才到达；必须同时校验进程和通道身份，
      // 不能用 null 无条件拆掉已经建立的新连接。
      proc.on('error', () => {
        if (this.proc === proc) this.teardownChannel(channel, new Error('无法启动 GrassCore。'));
      });
      proc.on('exit', () => {
        if (this.proc === proc && (this.channel === null || this.channelProc === proc))
          this.teardownChannel(channel, new Error('GrassCore 已退出。'));
      });
      await this.call('ping');
    }
  }

  private spawnCore(): ChildProcess {
    const proc = spawn(this.coreExe, [], { stdio: 'ignore' });
    this.proc = proc;
    // spawn 失败（路径错误/ENOENT）走 error 事件——没有监听器会变成主进程未捕获异常
    proc.on('error', () => {
      // spawnCore 期间可能已经连上了另一个既有 Core。此时
      // this.channel 属于旧进程，不能因新进程的延迟 error 事件拆掉它。
      if (this.proc === proc && this.channel === null)
        this.emit('core-exit');
    });
    proc.on('exit', (code) => {
      // Windows 重试连接可能在 spawned Core 尚未监听时接到另一个既有
      // Core；此时 channelProc 不能证明该通道属于 proc。活动通道的 close/
      // error 事件会负责拆除，只有没有活动通道时才把 proc 退出上报。
      if (this.proc === proc && this.channel === null) this.emit('core-exit', code);
    });
    return proc;
  }

  private async connectNamedPipe(name: string): Promise<void> {
    const socket = net.connect({ path: name });
    await new Promise<void>((resolve, reject) => {
      socket.once('connect', () => resolve());
      socket.once('error', (error) => {
        socket.destroy();
        reject(error);
      });
    });
    this.wire(socket, socket);
  }

  private wire(writable: NodeJS.WritableStream, readable: NodeJS.ReadableStream,
    ownerProc: ChildProcess | null = null): BridgeChannel {
    const currentChannel = { writable, readable };
    this.channel = currentChannel;
    this.channelProc = ownerProc;
    let buf = Buffer.alloc(0);
    readable.on('data', (chunk: Buffer) => {
      buf = Buffer.concat([buf, chunk]);
      while (buf.length >= 4) {
        const len = buf.readInt32LE(0);
        // 帧长合法域（与 Core 端一致）：撕裂帧/脏缓冲会给出天文数字或负数——
        // 照常 slice 会乱吞缓冲，负数直接让 subarray 抛异常炸掉主进程
        if (!(len > 0 && len <= MAX_FRAME_BYTES)) {
          this.teardownChannel(currentChannel, new Error('与 GrassCore 的通信帧损坏，正在重连…'));
          return;
        }
        if (buf.length < 4 + len) break;
        const json = buf.subarray(4, 4 + len).toString('utf8');
        buf = buf.subarray(4 + len);
        try {
          this.onMessage(json);
        } catch {
          // 半条 JSON（Core 死在帧中间）：坏帧不炸主进程——拆通道重生
          this.teardownChannel(currentChannel, new Error('与 GrassCore 的通信帧损坏，正在重连…'));
          return;
        }
      }
    });
    // Core 死亡契约：在途请求立即失败（否则 spinner 转到天荒地老），通道拆除，
    // 下次 call() 重新拉起 Core 并重接管运行中的 VM（QEMU 由 Core 重接管，不受影响）
    let tornDown = false;
    const onDown = () => {
      if (tornDown) return;
      tornDown = true;
      this.teardownChannel(currentChannel, new Error('GrassCore 已退出。正在尝试恢复…'));
    };
    readable.once('close', onDown);
    readable.once('end', onDown);
    readable.once('error', onDown);
    (writable as NodeJS.WritableStream & { once?: unknown }).once?.('close', onDown);
    (writable as NodeJS.WritableStream & { once?: unknown }).once?.('error', onDown);
    return currentChannel;
  }

  /** 拆除通道：失败所有在途请求；下次 call 自动重生 Core。忽略来自已过时通道的滞后事件。 */
  private teardownChannel(
    targetChannel: BridgeChannel | null,
    reason: Error,
  ): void {
    if (targetChannel && this.channel !== targetChannel) return;
    const ownerProc = this.channelProc;
    this.channel = null;
    this.channelProc = null;
    // 先摘掉当前通道身份，再销毁底层资源；销毁触发的 close/error 事件
    // 会被上面的身份检查视为过时事件，不会拆掉随后建立的新连接。
    if (targetChannel) {
      const resources: Array<NodeJS.ReadableStream | NodeJS.WritableStream> =
        [targetChannel.readable, targetChannel.writable];
      for (const resource of resources) {
        const destroy = (resource as NodeJS.ReadableStream & { destroy?: () => void }).destroy;
        if (typeof destroy === 'function') destroy.call(resource);
      }
    }
    // stdio 模式的 Core 是本桥自己启动的；协议损坏后终止它，避免坏进程
    // 留在后台并与下一次重连并存。Windows 命名管道没有 ownerProc，不能
    // 误杀可能属于另一个 UI 实例的 Core。
    if (ownerProc && this.proc === ownerProc && !ownerProc.killed) {
      try { ownerProc.kill(); } catch { /* 进程可能已退出 */ }
    }
    for (const [, p] of this.pending) p.reject(reason);
    this.pending.clear();
    this.emit('core-exit');
  }

  private channel: { writable: NodeJS.WritableStream; readable: NodeJS.ReadableStream } | null = null;

  private onMessage(json: string): void {
    const msg = JSON.parse(json) as { id?: number; result?: unknown; error?: { message: string } };
    if (msg.id != null && this.pending.has(msg.id)) {
      const p = this.pending.get(msg.id)!;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(new Error(msg.error.message));
      else p.resolve(msg.result);
    }
  }

  async call<T = unknown>(method: string, params?: unknown): Promise<T> {
    // Core 崩溃后自动重生（对上层透明；重接管由 Core 启动时的 AdoptRunningVms 完成）
    if (!this.channel) {
      this.proc?.removeAllListeners?.('exit');
      await this.ensureRunning().catch(() => {
        throw new Error('GrassCore 未连接且无法重新启动。');
      });
      // 重生后触发重接管（Core 启动时也会自动执行；此处兜底连接到既有 Core 的场景）
      if (this.channel) this.scheduleAdoption();
    }
    if (!this.channel) throw new Error('GrassCore 未连接。');
    const id = this.nextId++;
    const frame = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id, method, params }), 'utf8');
    const len = Buffer.alloc(4);
    len.writeInt32LE(frame.length, 0);
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      try {
        this.channel!.writable.write(Buffer.concat([len, frame]), () => undefined);
      } catch (error) {
        this.pending.delete(id);
        reject(error);
        this.teardownChannel(this.channel, error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private scheduleAdoption(): void {
    if (this.adopting) return;
    this.adopting = this.call('adoptRunningVms')
      .then(() => undefined, () => undefined)
      .finally(() => {
        this.adopting = null;
      });
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}
