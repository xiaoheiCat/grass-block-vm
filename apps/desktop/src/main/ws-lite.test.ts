import { describe, expect, it } from 'vitest';
import net from 'node:net';
import http from 'node:http';
import crypto from 'node:crypto';
import { WebSocketServer } from './ws-lite';

/** 构造带 mask 的客户端 WebSocket 帧（RFC 6455：客户端→服务端必须 mask） */
function clientFrame(opcode: number, payload: Buffer, fin = true): Buffer {
  const mask = crypto.randomBytes(4);
  const masked = Buffer.allocUnsafe(payload.length);
  for (let i = 0; i < payload.length; i++) masked[i] = payload[i] ^ mask[i % 4];
  let header: Buffer;
  if (payload.length < 126) {
    header = Buffer.from([(fin ? 0x80 : 0x00) | opcode, 0x80 | payload.length]);
  } else {
    header = Buffer.alloc(8);
    header[0] = (fin ? 0x80 : 0x00) | opcode;
    header[1] = 0x80 | 126;
    header.writeUInt16BE(payload.length, 2);
  }
  return Buffer.concat([header, mask, masked]);
}

interface Fixture {
  port: number;
  token: string;
  wss: WebSocketServer;
  done(): Promise<void>;
}

async function withServer(fn: (fx: Fixture) => Promise<void>): Promise<void> {
  const server = http.createServer();
  const wss = new WebSocketServer(server);
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  const port = (server.address() as net.AddressInfo).port;
  try {
    await fn({ port, token: wss.token, wss, done: async () => {
      for (const c of wss.clients) c.terminate();
      server.close();
      await new Promise((r2) => setTimeout(r2, 30));
    } });
  } finally {
    for (const c of wss.clients) c.terminate();
    server.close();
  }
}

function upgradeAndConnect(port: number, path: string, timeoutMs = 1500): Promise<net.Socket> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('upgrade timeout')), timeoutMs);
    const sock = net.connect(port, '127.0.0.1');
    sock.on('connect', () => {
      sock.write(
        `GET ${path} HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
          `Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n`,
      );
    });
    sock.once('data', (chunk: Buffer) => {
      clearTimeout(timer);
      if (chunk.toString().includes('101')) resolve(sock);
      else reject(new Error(`expected 101, got: ${chunk.toString().split('\r\n')[0]}`));
    });
    sock.once('error', (e) => {
      clearTimeout(timer);
      reject(e);
    });
  });
}

describe('ws-lite 帧协议（SPICE 桥的地基）', () => {
  it('单帧二进制消息完整送达', async () => {
    await withServer(async (fx) => {
      const received: Buffer[] = [];
      fx.wss.on('connection', (ws) => {
        ws.on('message', (data: Buffer) => received.push(data));
      });
      const sock = await upgradeAndConnect(fx.port, `/${fx.token}`);
      sock.write(clientFrame(0x2, Buffer.from('spice-handshake')));
      await new Promise((r) => setTimeout(r, 100));
      expect(received.length).toBe(1);
      expect(received[0].toString()).toBe('spice-handshake');
      sock.destroy();
      await fx.done();
    });
  });

  it('分片消息（首帧无 FIN + continuation）重组后一次交付', async () => {
    await withServer(async (fx) => {
      const received: Buffer[] = [];
      fx.wss.on('connection', (ws) => {
        ws.on('message', (data: Buffer) => received.push(data));
      });
      const sock = await upgradeAndConnect(fx.port, `/${fx.token}`);
      sock.write(clientFrame(0x2, Buffer.from('part1-'), false)); // FIN=0 首帧
      sock.write(clientFrame(0x0, Buffer.from('part2'), true)); // continuation + FIN
      await new Promise((r) => setTimeout(r, 100));
      expect(received.length).toBe(1); // 合并成一条，不能撕成两半
      expect(received[0].toString()).toBe('part1-part2');
      sock.destroy();
      await fx.done();
    });
  });

  it('路径 token 不符的 upgrade 被拒绝（SPICE 禁票运行的安全门）', async () => {
    await withServer(async (fx) => {
      await expect(upgradeAndConnect(fx.port, `/${fx.token}-wrong`)).rejects.toThrow();
      await expect(upgradeAndConnect(fx.port, '/')).rejects.toThrow();
      await fx.done();
    });
  });
});
