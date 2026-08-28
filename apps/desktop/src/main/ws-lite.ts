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
  private readonly conns = new Set<WsConnImpl>();

  /** 当前连接（拆除桥时用于终止全部会话）。 */
  get clients(): ReadonlySet<WsConnImpl> {
    return this.conns;
  }

  constructor(server: http.Server) {
    super();
    server.on('upgrade', (req, socket, head) => this.handleUpgrade(req, socket as net.Socket, head));
  }

  private handleUpgrade(req: http.IncomingMessage, socket: net.Socket, head: Buffer): void {
    const key = req.headers['sec-websocket-key'];
    if (!key) {
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
    if (head.length) conn.pushRaw(head);
  }
}

class WsConnImpl extends EventEmitter implements WsConnection {
  private buf = Buffer.alloc(0);
  private closed = false;

  constructor(private socket: net.Socket) {
    super();
  }

  /** 喂入原始字节；解析数据帧（客户端帧必须带 mask）。 */
  private fragments: Buffer[] = [];

  pushRaw(chunk: Buffer): void {
    if (this.closed) return;
    this.buf = Buffer.concat([this.buf, chunk]);
    for (;;) {
      if (this.buf.length < 2) break;
      const fin = (this.buf[0] & 0x80) !== 0;
      const opcode = this.buf[0] & 0x0f;
      const masked = (this.buf[1] & 0x80) !== 0;
      let len = this.buf[1] & 0x7f;
      let off = 2;
      if (len === 126) {
        if (this.buf.length < 4) break;
        len = this.buf.readUInt16BE(2);
        off = 4;
      } else if (len === 127) {
        if (this.buf.length < 10) break;
        len = Number(this.buf.readBigUInt64BE(2));
        off = 10;
      }
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
        }
      } else if (opcode === 0x0) {
        this.fragments.push(payload);
        if (fin) {
          const whole = Buffer.concat(this.fragments);
          this.fragments = [];
          this.emit('message', whole);
        }
      }
    }
  }

  send(data: Buffer): void {
    if (this.closed) return;
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
    const len = payload.length;
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
    this.socket.write(Buffer.concat([header, payload]));
  }
}
