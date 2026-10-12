'use strict';
// Reconnecting client for the mod's v2 protocol. The Stream Deck plugin will port this to TypeScript.

const { EventEmitter } = require('events');
const WebSocket = require('ws');

const COMMAND_TIMEOUT_MS = 2000;
// the mod sends state on change plus a 1 s heartbeat; silence for this long means the game is hung or gone
const STALE_AFTER_MS = 3000;

class DvConnection extends EventEmitter {
  constructor(url) {
    super();
    this.url = url;
    this.ws = null;
    this.state = null;
    this.connected = false;
    this.nextId = 1;
    this.pending = new Map();
    this.backoffMs = 500;
    this.closedByUser = false;
    this.lastMessageAt = 0;
    this.staleTimer = null;
  }

  connect() {
    this.closedByUser = false;
    const ws = new WebSocket(this.url);
    this.ws = ws;

    ws.on('open', () => {
      this.connected = true;
      this.backoffMs = 500;
      this.lastMessageAt = Date.now();
      this.send('hello', { client: 'dv-test-client', protocol: 2 });
      this.emit('connected');
      this.staleTimer = setInterval(() => {
        if (Date.now() - this.lastMessageAt > STALE_AFTER_MS) {
          this.emit('log', 'No state for 3 s, reconnecting');
          ws.terminate();
        }
      }, 1000);
    });

    ws.on('message', (data) => {
      this.lastMessageAt = Date.now();
      let msg;
      try {
        msg = JSON.parse(data.toString());
      } catch (e) {
        this.emit('log', `Bad JSON from mod: ${e.message}`);
        return;
      }
      this.handle(msg);
    });

    ws.on('close', () => this.onClosed());
    // 'close' always follows 'error'; just surface the reason
    ws.on('error', (err) => this.emit('log', `Socket error: ${err.message}`));
  }

  onClosed() {
    clearInterval(this.staleTimer);
    const wasConnected = this.connected;
    this.connected = false;
    this.state = null;
    for (const [, p] of this.pending) p.reject(new Error('Disconnected'));
    this.pending.clear();
    if (wasConnected) this.emit('disconnected');
    if (this.closedByUser) return;

    setTimeout(() => this.connect(), this.backoffMs);
    this.backoffMs = Math.min(this.backoffMs * 2, 5000);
  }

  handle(msg) {
    switch (msg.type) {
      case 'state':
        this.state = msg.payload;
        this.emit('state', msg.payload);
        break;
      case 'commandResult': {
        const p = this.pending.get(msg.payload.id);
        if (p) {
          this.pending.delete(msg.payload.id);
          clearTimeout(p.timer);
          p.resolve(msg.payload);
        }
        break;
      }
      case 'event':
        this.emit('event', msg.payload);
        break;
      case 'error':
        this.emit('log', `Mod error: ${msg.payload && msg.payload.message}`);
        break;
      default:
        this.emit('log', `Unhandled message type '${msg.type}'`);
    }
  }

  send(type, payload) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return false;
    this.ws.send(JSON.stringify({ type, payload }));
    return true;
  }

  /** Resolves with the mod's commandResult payload ({ id, ok, error, value }). */
  command(control, action, value) {
    const id = String(this.nextId++);
    return new Promise((resolve, reject) => {
      const payload = { id, control, action };
      if (value !== undefined) payload.value = value;
      if (!this.send('command', payload)) {
        reject(new Error('Not connected'));
        return;
      }
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`No result for command ${id} within ${COMMAND_TIMEOUT_MS} ms`));
      }, COMMAND_TIMEOUT_MS);
      this.pending.set(id, { resolve, reject, timer });
    });
  }

  close() {
    this.closedByUser = true;
    if (this.ws) this.ws.close();
  }
}

module.exports = { DvConnection };
