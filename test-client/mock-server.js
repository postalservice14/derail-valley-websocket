#!/usr/bin/env node
'use strict';
// Stand-in for the mod's v2 protocol, for developing clients (and the Stream Deck plugin) without the
// game running. Not a simulation: just enough physics for gauges to move.
//
//   node mock-server.js [--steam] [--port 9450]

const { WebSocketServer } = require('ws');

const args = process.argv.slice(2);
const portArg = args.indexOf('--port');
const PORT = portArg >= 0 ? Number(args[portArg + 1]) : 9450;
const STEAM = args.includes('--steam');

const controls = {
  throttle: { value: 0, notches: STEAM ? null : 8 },
  trainBrake: { value: 0, notches: null },
  independentBrake: { value: 1, notches: null },
  dynamicBrake: STEAM ? null : { value: 0, notches: null },
  reverser: { value: 0.5, notches: STEAM ? null : 3 },
  horn: { value: 0, notches: null },
  sander: { value: 0, notches: null },
  headlightsFront: { value: 0.4, notches: null },
  headlightsRear: { value: 0.4, notches: null },
  cabLight: { value: 0, notches: null },
};
const sim = { speed: 0, bp: 5, mr: 8, boiler: 12, water: 0.7, fuel: 4000, sand: 180 };
const ALIASES = { regulator: 'throttle', cutoff: 'reverser', headlights: 'headlightsFront' };
const round = (v) => Math.round(v * 1000) / 1000;
const clamp01 = (v) => Math.min(1, Math.max(0, v));

function snap(c, v) {
  v = clamp01(v);
  // like the game: notches = number of positions, resting on k / (notches - 1)
  return c.notches ? Math.round(v * (c.notches - 1)) / (c.notches - 1) : v;
}

function applyCommand(cmd) {
  const name = (cmd.control || '').replace(/[_-]/g, '');
  const key = Object.keys(controls).find((k) => k.toLowerCase() === name.toLowerCase()) || ALIASES[name.toLowerCase()];
  if (!key) throw new Error(`Unknown control '${cmd.control}'`);
  if ((name.toLowerCase() === 'regulator' || name.toLowerCase() === 'cutoff') && !STEAM) throw new Error(`'${cmd.control}' is steam-only`);
  const c = controls[key];
  if (!c) throw new Error(`This loco has no '${cmd.control}' control`);

  switch ((cmd.action || '').toLowerCase()) {
    case 'set': c.value = snap(c, cmd.value); break;
    case 'nudge': {
      if (!cmd.value) break;
      const n = c.notches ? c.notches - 1 : 0;
      const step = n ? Math.max(1, Math.round(Math.abs(cmd.value) * n)) * Math.sign(cmd.value) / n : cmd.value;
      c.value = snap(c, c.value + step);
      break;
    }
    case 'toggle':
      if (key.startsWith('headlights')) c.value = Math.abs(c.value - 0.4) < 0.01 ? 1 : 0.4;
      else c.value = c.value > 0.5 ? 0 : 1;
      break;
    case 'press': c.value = cmd.value === undefined ? 1 : clamp01(cmd.value); break;
    case 'release': c.value = 0; break;
    default: throw new Error(`Unknown action '${cmd.action}'`);
  }
  return c.value;
}

function tick(dt) {
  const dir = (controls.reverser.value - 0.5) * 2;
  const brake = Math.max(controls.trainBrake.value, controls.independentBrake.value);
  sim.speed = Math.max(0, sim.speed + (controls.throttle.value * Math.abs(dir) * 3 - brake * 6 - 0.2) * dt);
  sim.bp = 5 - controls.trainBrake.value * 1.5;
  sim.fuel = Math.max(0, sim.fuel - controls.throttle.value * dt);
  if (controls.sander.value > 0.5) sim.sand = Math.max(0, sim.sand - dt);
}

function state() {
  const out = {};
  for (const [k, c] of Object.entries(controls)) out[k] = c && { value: round(c.value), notches: c.notches, blocked: false };
  return {
    protocol: 2,
    carName: STEAM ? 'LocoSteamHeavy LocoS282A' : 'LocoShunter LocoDE2',
    loco: {
      id: 'L-001', liveryId: STEAM ? 'LocoS282A' : 'LocoDE2', kind: STEAM ? 'steam' : 'diesel',
      speedKph: round(sim.speed), speedometerKph: round(sim.speed), wheelslip: 0, isWheelslipping: false,
      controls: out,
      brakes: { brakePipe: round(sim.bp), mainReservoir: sim.mr, brakeCylinder: round(Math.max(controls.trainBrake.value, controls.independentBrake.value) * 3.5) },
      steam: STEAM ? { boilerPressure: sim.boiler, boilerWaterLevel: sim.water, safetyValve: 0, regulator: round(controls.throttle.value), cutoff: round((controls.reverser.value - 0.5) * 2) } : null,
      resources: { fuel: STEAM ? null : round(sim.fuel), fuelMax: STEAM ? null : 6000, sand: round(sim.sand), sandMax: 200, oil: null, oilMax: null },
      engine: { on: true, rpm: 600 + controls.throttle.value * 1400, rpmMax: 2000, tractionMotorAmps: null, tractionMotorAmpsMax: null, temperature: 60 },
      lamps: { fuel: 0, oil: 0, sand: 0, sander: controls.sander.value, headlightFront: controls.headlightsFront.value === 0.4 ? 0 : 1, headlightRear: 0, cabLight: controls.cabLight.value },
    },
    jobs: [{ id: 'FH-SM-01', type: 'ShuntingLoad', state: 'InProgress', origin: 'FH', destination: 'SM', wage: 1200, timeLimit: 0, timeOnJob: 30 }],
  };
}

const wss = new WebSocketServer({ port: PORT, path: '/dv' });
const v2Clients = new Set();
const send = (ws, type, payload) => ws.send(JSON.stringify({ type, payload }));

wss.on('connection', (ws) => {
  ws.on('message', (data) => {
    let msg;
    try {
      msg = JSON.parse(data.toString());
      const type = String(msg.type).toLowerCase();
      if (type === 'hello') {
        v2Clients.add(ws);
        send(ws, 'state', state());
      } else if (type === 'command') {
        try {
          send(ws, 'commandResult', { id: msg.payload.id, ok: true, error: null, value: round(applyCommand(msg.payload)) });
        } catch (e) {
          send(ws, 'commandResult', { id: msg.payload.id, ok: false, error: e.message, value: null });
        }
      } else {
        throw new Error(`Unknown message type '${msg.type}'`);
      }
    } catch (e) {
      send(ws, 'error', { message: e.message });
    }
  });
  ws.on('close', () => {
    v2Clients.delete(ws);
    controls.horn.value = 0; // like the mod: a disconnecting client can't leave the horn on
  });
});

let last = Date.now();
setInterval(() => {
  const now = Date.now();
  tick((now - last) / 1000);
  last = now;
  const json = JSON.stringify({ type: 'state', payload: state() });
  for (const ws of v2Clients) ws.send(json);
}, 50);

console.log(`Mock DV mod (${STEAM ? 'steam S282' : 'diesel DE2'}) on ws://127.0.0.1:${PORT}/dv`);
