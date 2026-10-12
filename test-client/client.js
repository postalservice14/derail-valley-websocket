#!/usr/bin/env node
'use strict';
// Test client for the Derail Valley WebSocket mod (protocol v2).
//
//   node client.js              interactive prompt (type `help`)
//   node client.js --watch      live one-line status only
//   node client.js --selftest   scripted read/write check; sit in a stationary loco first
//   --url ws://host:9450/dv     override the server address

const readline = require('readline');
const { DvConnection } = require('./dv-connection');

const args = process.argv.slice(2);
const urlArg = args.indexOf('--url');
const URL = urlArg >= 0 ? args[urlArg + 1] : 'ws://127.0.0.1:9450/dv';
const MODE = args.includes('--selftest') ? 'selftest' : args.includes('--watch') ? 'watch' : 'repl';

const conn = new DvConnection(URL);
conn.on('log', (m) => console.log(`[conn] ${m}`));
conn.on('connected', () => console.log(`[conn] connected to ${URL}`));
conn.on('disconnected', () => console.log('[conn] disconnected, retrying...'));
conn.on('event', (e) => console.log(`[event] ${e.name} = ${JSON.stringify(e.value)}`));

// ---------- formatting ----------

const f = (v, d = 2) => (v === null || v === undefined ? '-' : Number(v).toFixed(d));
const pct = (v, max) => (v === null || v === undefined || !max ? '-' : `${Math.round((v / max) * 100)}%`);
const ctl = (c) => (c ? f(c.value) + (c.notches ? `/${c.notches}n` : '') : '-');

function statusLine(s) {
  if (!s) return 'waiting for state...';
  const l = s.loco;
  if (!l) return `not in a loco (car: ${s.carName || 'none'}) | jobs ${s.jobs ? s.jobs.length : '-'}`;
  const c = l.controls;
  const b = l.brakes || {};
  const r = l.resources || {};
  let line =
    `[${l.id} ${l.liveryId} ${l.kind}] ${f(l.speedKph, 1)} km/h` +
    ` | thr ${ctl(c.throttle)} brk ${ctl(c.trainBrake)} ind ${ctl(c.independentBrake)} dyn ${ctl(c.dynamicBrake)} rev ${ctl(c.reverser)}` +
    ` | BP ${f(b.brakePipe, 1)} MR ${f(b.mainReservoir, 1)} BC ${f(b.brakeCylinder, 1)} bar`;
  if (l.steam) {
    line += ` | boiler ${f(l.steam.boilerPressure, 1)} bar water ${f(l.steam.boilerWaterLevel)} cutoff ${f(l.steam.cutoff)} reg ${f(l.steam.regulator)}`;
  } else {
    line += ` | fuel ${pct(r.fuel, r.fuelMax)}`;
  }
  line += ` sand ${pct(r.sand, r.sandMax)} | slip ${l.isWheelslipping ? 'YES' : 'no'} | jobs ${s.jobs ? s.jobs.length : '-'}`;
  return line;
}

// ---------- interactive ----------

const HELP = `
  set <control> <value>      absolute lever position 0..1 (reverser/cutoff: 0.5 = neutral)
  nudge <control> <delta>    relative, e.g. nudge throttle 0.05 (notched levers move >= 1 notch)
  toggle <control>           sander, cabLight, headlights, headlightsRear
  press <control> [value]    e.g. press horn  (held until release)
  release <control>
  horn [seconds]             press, wait, release (default 1 s)
  status                     one-line summary        state   full JSON snapshot
  watch                      toggle live status line  quit
  controls: throttle trainBrake independentBrake dynamicBrake reverser regulator cutoff
            horn sander headlights headlightsRear cabLight`;

async function runCommand(control, action, value) {
  try {
    const res = await conn.command(control, action, value);
    console.log(res.ok ? `ok  ${control} -> ${f(res.value, 3)}` : `ERR ${res.error}`);
  } catch (e) {
    console.log(`ERR ${e.message}`);
  }
}

function repl(watchOnly) {
  let watching = watchOnly;
  conn.on('state', (s) => {
    if (watching) process.stdout.write(`\r\x1b[K${statusLine(s)}`);
  });
  if (watchOnly) return;

  console.log(`Type 'help' for commands.`);
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout, prompt: 'dv> ' });
  rl.prompt();
  rl.on('line', async (line) => {
    const [cmd, a, b] = line.trim().split(/\s+/);
    const num = (x) => (x === undefined ? undefined : Number(x));
    switch ((cmd || '').toLowerCase()) {
      case '': break;
      case 'help': console.log(HELP); break;
      case 'status': console.log(statusLine(conn.state)); break;
      case 'state': console.log(JSON.stringify(conn.state, null, 2)); break;
      case 'watch': watching = !watching; if (!watching) console.log(); break;
      case 'quit': case 'exit': conn.close(); rl.close(); return;
      case 'set': case 'nudge': await runCommand(a, cmd, num(b)); break;
      case 'toggle': await runCommand(a, 'toggle'); break;
      case 'press': await runCommand(a, 'press', num(b)); break;
      case 'release': await runCommand(a, 'release'); break;
      case 'horn': {
        await runCommand('horn', 'press');
        await sleep((num(a) || 1) * 1000);
        await runCommand('horn', 'release');
        break;
      }
      default: console.log(`Unknown command '${cmd}'. Type 'help'.`);
    }
    rl.prompt();
  });
  rl.on('close', () => process.exit(0));
}

// ---------- self test ----------

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function waitForState(predicate, timeoutMs, what) {
  return new Promise((resolve, reject) => {
    if (conn.state && predicate(conn.state)) return resolve(conn.state);
    const timer = setTimeout(() => {
      conn.off('state', onState);
      reject(new Error(`timed out waiting for ${what}`));
    }, timeoutMs);
    function onState(s) {
      if (predicate(s)) {
        clearTimeout(timer);
        conn.off('state', onState);
        resolve(s);
      }
    }
    conn.on('state', onState);
  });
}

async function selftest() {
  const results = [];
  const check = (name, ok, detail = '') => {
    results.push(ok);
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`);
  };
  const near = (a, b) => Math.abs(a - b) < 0.011;
  const ctlValue = (s, key) => s.loco && s.loco.controls[key] && s.loco.controls[key].value;

  // writes a control, then waits for the state stream to echo the mod-reported value back
  // `expected` asserts the exact landing position (e.g. reverser neutral), not just that state echoed it
  async function writeAndVerify(key, action, value, label, expected) {
    const res = await conn.command(key, action, value);
    if (!res.ok) return check(label, false, res.error);
    if (expected !== undefined && !near(res.value, expected)) return check(label, false, `landed on ${f(res.value, 3)}, expected ${expected}`);
    try {
      await waitForState((s) => near(ctlValue(s, key), res.value), 1500, `${key}=${res.value}`);
      check(label, true, `now ${f(res.value, 3)}`);
    } catch (e) {
      check(label, false, `${e.message}; state shows ${f(ctlValue(conn.state, key), 3)}`);
    }
    return res;
  }

  console.log(`Self-test against ${URL}\nWaiting for the game... sit in a stationary loco.`);
  await waitForState(() => true, 60000, 'first state (is the game running with the mod enabled?)');
  check('connected and received state', true);
  const s0 = await waitForState((s) => s.loco, 60000, 'player to sit in a loco');
  const l0 = s0.loco;
  console.log(`      ${statusLine(s0)}`);

  check('state: loco id/livery/kind', !!(l0.id && l0.liveryId && (l0.kind === 'diesel' || l0.kind === 'steam')), `${l0.liveryId} ${l0.kind}`);
  check('state: speed is a number', typeof l0.speedKph === 'number', `${l0.speedKph} km/h`);
  check('state: throttle readable', !!l0.controls.throttle);
  check('state: brake pressures', !!l0.brakes, l0.brakes ? `BP ${l0.brakes.brakePipe} MR ${l0.brakes.mainReservoir}` : 'null');
  if (l0.kind === 'steam') {
    check('state: boiler pressure + water level', !!(l0.steam && l0.steam.boilerPressure !== null && l0.steam.boilerWaterLevel !== null));
  }
  check('state: jobs list', Array.isArray(s0.jobs), s0.jobs ? `${s0.jobs.length} job(s)` : 'null');

  if (l0.speedKph > 1) {
    console.log('\nLoco is moving; skipping write tests. Stop the train and run again.');
    return finish(results);
  }

  // remember everything we touch so it can be put back exactly
  const original = {};
  for (const key of ['throttle', 'reverser', 'trainBrake', 'independentBrake', 'dynamicBrake', 'cabLight']) {
    if (l0.controls[key]) original[key] = l0.controls[key].value;
  }

  try {
    // throttle with the reverser in neutral so the train can't move
    if (original.reverser !== undefined) {
      const rev = await writeAndVerify('reverser', 'set', 0.5, 'write: reverser -> neutral', 0.5);
      // never open the throttle unless the reverser is provably in neutral
      if (!rev || !rev.ok || !near(rev.value, 0.5)) throw new Error('reverser not in neutral; skipping throttle tests');
    }
    await writeAndVerify('throttle', 'set', 0.3, 'write: set throttle 0.3');
    const before = ctlValue(conn.state, 'throttle');
    const nudged = await conn.command('throttle', 'nudge', 0.1);
    check('write: nudge throttle +0.1 increases it', nudged.ok && nudged.value > before, `${f(before, 3)} -> ${f(nudged.value, 3)}`);
    await writeAndVerify('throttle', 'set', 0, 'write: throttle back to 0');

    if (original.trainBrake !== undefined) await writeAndVerify('trainBrake', 'set', 0.5, 'write: train brake 0.5');
    if (original.independentBrake !== undefined) await writeAndVerify('independentBrake', 'set', 1, 'write: independent brake 1');
    if (original.dynamicBrake !== undefined) await writeAndVerify('dynamicBrake', 'set', 0.2, 'write: dynamic brake 0.2');
    if (original.cabLight !== undefined) {
      await writeAndVerify('cabLight', 'toggle', undefined, 'write: toggle cab light');
      await writeAndVerify('cabLight', 'toggle', undefined, 'write: toggle cab light back');
    }

    const hornOn = await conn.command('horn', 'press');
    await sleep(400);
    const hornOff = await conn.command('horn', 'release');
    check('write: horn press/release', hornOn.ok && hornOff.ok, hornOn.ok ? '' : hornOn.error);

    const bogus = await conn.command('warpDrive', 'set', 1);
    check('error: unknown control rejected', !bogus.ok, bogus.error);
    const steamOnly = await conn.command('regulator', 'set', 0);
    check(
      l0.kind === 'steam' ? 'steam: regulator accepted' : 'error: regulator rejected on diesel',
      l0.kind === 'steam' ? steamOnly.ok : !steamOnly.ok,
      steamOnly.error || '',
    );
  } catch (e) {
    check('write tests', false, e.message);
  } finally {
    // throttle first, then reverser, so restoring can't briefly apply power in gear
    for (const key of ['throttle', 'dynamicBrake', 'trainBrake', 'independentBrake', 'cabLight', 'reverser']) {
      if (original[key] === undefined) continue;
      try {
        await conn.command(key, 'set', original[key]);
      } catch (e) {
        console.log(`WARN  could not restore ${key}: ${e.message}`);
      }
    }
    console.log('      restored original control positions');
  }
  finish(results);
}

function finish(results) {
  const failed = results.filter((ok) => !ok).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  conn.close();
  process.exit(failed ? 1 : 0);
}

// ---------- main ----------

conn.connect();
if (MODE === 'selftest') {
  selftest().catch((e) => {
    console.log(`FAIL  ${e.message}`);
    conn.close();
    process.exit(1);
  });
} else {
  repl(MODE === 'watch');
}
