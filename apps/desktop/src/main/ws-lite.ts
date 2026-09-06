/**
 * 极简 WebSocket 服务端（RFC 6455 子集：文本/二进制帧 + ping/pong + close）。
 * 仅用于本机 SPICE 字节桥；生产可替换为 ws 包（发布件按计划做完整依赖审计）。
 * 参考实现策略来自 electerm：WS 字节直通 TCP，无协议转换，不改 spice-client 源码。
 */
import type http from 'node:http';
import type net from 'node:net';
import { EventEmitter } from 'node:events';
import crypto from 'node:crypto';

export interface WsConnection extends EventEmitter {
  send(data: Buffer): void;
  close(): void;
  /** 立即断开（服务端拆除时用，不走关闭握手）。 */
  terminate(): void;
  on(event: 'message', listener: (data: Buffer) => void): this;
  on(event: 'close', listener: () => void): this;
}

export class WebSocketServer extends EventEmitter {
  // spice-html5 为一条显示器拆分建立 main/display/inputs/cursor/playback 等多条通道。
  private static readonly MaxClientsPerBridge = 8;
  private readonly conns = new Set<WsConnImpl>();

  /** 当前连接（拆除桥时用于终止全部会话）。 */
  get clients(): ReadonlySet<WsConnImpl> {
    return this.conns;
  }

  constructor(server: http.Server) {
    super();
    server.on('upgrade', (req, socket, head) => this.handleUpgrade(req, socket as net.Socket, head));
  }

  /** 每桥一个随机 token：upgrade 请求路径必须精确匹配（SPICE 禁票运行，本地浏览器里的任意页面不得直连） */
  readonly token = crypto.randomUUID();

  private handleUpgrade(req: http.IncomingMessage, socket: net.Socket, head: Buffer): void {
    const key = req.headers['sec-websocket-key'];
    if (req.method !== 'GET'
      || req.headers.upgrade?.toLowerCase() !== 'websocket'
      || !String(req.headers.connection ?? '').toLowerCase().includes('upgrade')
      || req.headers['sec-websocket-version'] !== '13'
      || !key || (req.url ?? '') !== `/${this.token}`) {
      socket.destroy();
      return;
    }
    // 一个显示器只需要一个正常客户端，短暂重连最多再容纳一个；
    // 限制连接数避免同一令牌被滥用时为每条 WS 各建一条 SPICE TCP 连接。
    if (this.conns.size >= WebSocketServer.MaxClientsPerBridge) {
      socket.destroy();
      return;
    }
    const accept = crypto
      .createHash('sha1')
      .update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')
      .digest('base64');
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n' +
        'Upgrade: websocket\r\n' +
        'Connection: Upgrade\r\n' +
        `Sec-WebSocket-Accept: ${accept}\r\n\r\n`,
    );
    socket.setNoDelay(true);

    const conn = new WsConnImpl(socket);
    this.conns.add(conn);
    conn.on('close', () => this.conns.delete(conn));
    this.emit('connection', conn);

    socket.on('data', (chunk: Buffer) => conn.pushRaw(chunk));
    socket.on('close', () => conn.emit('close'));
    // 升级后的 socket 必须接住 error：显示器窗口被杀时 SPICE 帧还在途 → RST，
    // 无 error 监听的 socket 会把异常抛进主进程（未捕获异常 = 整个应用退出，
    // 库窗口陪葬，VM 变成无头运行）。按断开处理即可
    socket.on('error', () => {
      this.conns.delete(conn);
      conn.emit('close');
    });
    if (head.length) conn.pushRaw(head);
  }
}

class WsConnImpl extends EventEmitter implements WsConnection {
  private static readonly MaxBufferedBytes = 32 * 1024 * 1024;
  private static readonly MaxInputBytes = 16 * 1024 * 1024;
  private static readonly MaxFragments = 4096;
  private buf = Buffer.alloc(0);
  private closed = false;
  private bufferedBytes = 0;

  constructor(private socket: net.Socket) {
    super();
  }

  /** 喂入原始字节；解析数据帧（客户端帧必须带 mask）。 */
  private fragments: Buffer[] = [];
  private fragmentedBytes = 0;

  pushRaw(chunk: Buffer): void {
    if (this.closed) return;
    this.buf = Buffer.concat([this.buf, chunk]);
    if (this.buf.length > WsConnImpl.MaxInputBytes) {
      this.terminate();
      return;
    }
    for (;;) {
      if (this.buf.length < 2) break;
      const fin = (this.buf[0] & 0x80) !== 0;
      const opcode = this.buf[0] & 0x0f;
      const masked = (this.buf[1] & 0x80) !== 0;
      if (!masked) { this.terminate(); return; }
      let len = this.buf[1] & 0x7f;
      let off = 2;
      if (len === 126) {
        if (this.buf.length < 4) break;
        len = this.buf.readUInt16BE(2);
        off = 4;
      } else if (len === 127) {
        if (this.buf.length < 10) break;
        const longLen = this.buf.readBigUInt64BE(2);
        if (longLen > BigInt(16 * 1024 * 1024)) { this.terminate(); return; }
        len = Number(longLen);
        off = 10;
      }
      if ((opcode & 0x8) !== 0 && (!fin || len > 125)) { this.terminate(); return; }
      if (![0x0, 0x1, 0x2, 0x8, 0x9, 0xa].includes(opcode)) { this.terminate(); return; }
      if (len > 16 * 1024 * 1024) { this.terminate(); return; }
      const maskLen = masked ? 4 : 0;
      if (this.buf.length < off + maskLen + len) break;
      const mask = this.buf.subarray(off, off + maskLen);
      let payload = this.buf.subarray(off + maskLen, off + maskLen + len);
      this.buf = this.buf.subarray(off + maskLen + len);
      if (masked && maskLen === 4) {
        const out = Buffer.allocUnsafe(len);
        for (let i = 0; i < len; i++) out[i] = payload[i] ^ mask[i % 4];
        payload = out;
      }
      if (opcode === 0x8) {
        // close
        this.closed = true;
        this.socket.end();
        return;
      }
      if ((opcode === 0x1 || opcode === 0x2) && this.fragments.length > 0) { this.terminate(); return; }
      if (opcode === 0x0 && this.fragments.length === 0) { this.terminate(); return; }
      if (opcode === 0x9) {
        this.writeFrame(0xa, Buffer.alloc(0)); // ping → pong
        continue;
      }
      // 分片消息：首帧（text/binary）缓存，continuation（0x0）追加，FIN 时合并交付
      if (opcode === 0x1 || opcode === 0x2) {
        if (fin) {
          this.emit('message', payload);
        } else {
          this.fragments = [payload];
          this.fragmentedBytes = payload.length;
        }
      } else if (opcode === 0x0) {
        // 每个 continuation 到达时都计入上限；只在 FIN 时检查会让攻击者
        // 发送无限长的分片消息，令 fragments 数组在最终合并前耗尽内存。
        if (this.fragments.length >= WsConnImpl.MaxFragments
          || this.fragmentedBytes + payload.length > WsConnImpl.MaxInputBytes) {
          this.terminate();
          return;
        }
        this.fragments.push(payload);
        this.fragmentedBytes += payload.length;
        if (fin) {
          const whole = Buffer.concat(this.fragments);
          this.fragments = [];
          this.fragmentedBytes = 0;
          this.emit('message', whole);
        }
      }
    }
  }

  send(data: Buffer): void {
    if (this.closed) return;
    if (this.bufferedBytes + data.length > WsConnImpl.MaxBufferedBytes) {
      this.terminate();
      return;
    }
    this.writeFrame(0x2, data);
  }

  close(): void {
    if (this.closed) return;
    this.writeFrame(0x8, Buffer.alloc(0));
    this.closed = true;
    this.socket.end();
  }

  terminate(): void {
    this.closed = true;
    this.socket.destroy();
    this.emit('close');
  }

  private writeFrame(opcode: number, payload: Buffer): void {
    // 已销毁/已断开的 socket 写入会抛 EPIPE——SPICE 桥的回写不能因此炸主进程
    if (this.socket.destroyed) {
      this.closed = true;
      return;
    }
    const len = payload.length;
    if (opcode === 0x2) this.bufferedBytes += len;
    let header: Buffer;
    if (len < 126) {
      header = Buffer.from([0x80 | opcode, len]);
    } else if (len < 65536) {
      header = Buffer.alloc(4);
      header[0] = 0x80 | opcode;
      header[1] = 126;
      header.writeUInt16BE(len, 2);
    } else {
      header = Buffer.alloc(10);
      header[0] = 0x80 | opcode;
      header[1] = 127;
      header.writeBigUInt64BE(BigInt(len), 2);
    }
    const frame = Buffer.concat([header, payload]);
    this.socket.write(frame, () => {
      if (opcode === 0x2) this.bufferedBytes = Math.max(0, this.bufferedBytes - len);
    });
  }
}
