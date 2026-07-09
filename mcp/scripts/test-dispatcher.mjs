#!/usr/bin/env node
/**
 * test-dispatcher.mjs — self-contained lease-serialization + hardening test for dispatcher.mjs.
 *
 * Spins up:
 *   1. a tiny local STUB standing in for the Unity Editor bridge on 127.0.0.1:<stubPort>
 *      (echoes {command,args}; can DELAY via args.__delayMs to simulate a slow command),
 *   2. the real dispatcher on 127.0.0.1:<dispPort> (forwarding to the stub),
 * then drives:
 *   BASELINE — two agents contend for one lease (serialize, promote via two-phase
 *              reservation, idle-reclaim).
 *   (a) a >TTL in-flight command is NOT reclaimed mid-forward (inFlight guard).
 *   (b) two run-nonces for the SAME agent id are mutually exclusive (no shared token).
 *   (c) a dead/unconfirmed reserved queue head is dropped after grace → next live agent wins.
 *   (d) a blocked (non-allowlisted) command is rejected WITHOUT reaching the stub.
 *
 * Run:  node scripts/test-dispatcher.mjs
 * Exits 0 on success, 1 on any failed assertion.
 */
import http from 'node:http';

// Pick non-default ports so a real dispatcher / Editor running locally isn't disturbed.
const STUB_PORT = 8799;
const DISP_PORT = 8798;
process.env.DISPATCHER_PORT = String(DISP_PORT);
process.env.UNITY_BRIDGE_URL = `http://127.0.0.1:${STUB_PORT}/`;
// Short TTL so idle-reclaim assertions run fast.
process.env.DISPATCHER_LEASE_TTL = '1';
// Forward timeout ABOVE the TTL on purpose: exercises the inFlight guard — a slow command
// must NOT be reclaimed mid-forward even though it outlives the lease TTL.
process.env.DISPATCHER_FORWARD_TIMEOUT_MS = '5000';
// Short reservation grace so the dead-reservation-drop assertion runs fast.
process.env.DISPATCHER_RESERVATION_GRACE_MS = '250';

const { startDispatcher, stopDispatcher } = await import('../src/dispatcher.mjs');

let failures = 0;
let passes = 0;
function assert(cond, msg) {
  if (cond) {
    passes++;
    console.log('  ✓', msg);
  } else {
    failures++;
    console.error('  ✗ FAIL:', msg);
  }
}

// ── Stub Editor bridge — echoes {command,args}; delays if args.__delayMs is set ──
let stubHits = 0; // every request that gets a response (incl. pingEditor liveness probes)
let delayedHits = 0; // only responses that were deliberately delayed (the slow-command probe)
const stub = http.createServer((req, res) => {
  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', () => {
    let parsed = {};
    try {
      parsed = JSON.parse(body || '{}');
    } catch {}
    const delayMs = Number(parsed.args && parsed.args.__delayMs) || 0;
    const respond = () => {
      stubHits++;
      if (delayMs > 0) delayedHits++;
      const out = JSON.stringify({
        ok: true,
        data: { echoedCommand: parsed.command, echoedArgs: parsed.args, stubHit: stubHits },
      });
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(out);
    };
    if (delayMs > 0) setTimeout(respond, delayMs);
    else respond();
  });
});

function post(port, path, body) {
  return fetch(`http://127.0.0.1:${port}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).then((r) => r.json());
}
function get(port, path) {
  return fetch(`http://127.0.0.1:${port}${path}`).then((r) => r.json());
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
  await new Promise((r) => stub.listen(STUB_PORT, '127.0.0.1', r));
  await startDispatcher(DISP_PORT);
  console.log(`\n[test] stub@${STUB_PORT} dispatcher@${DISP_PORT}\n`);

  console.log('scenario: two agents contend for one lease');

  // 1. A acquires.
  const a1 = await post(DISP_PORT, '/lease/acquire', { agent: 'agentA' });
  assert(a1.ok === true && typeof a1.token === 'string', 'A acquires → ok:true with a token');
  const tokenA = a1.token;

  // 2. B acquires → queued behind A.
  const b1 = await post(DISP_PORT, '/lease/acquire', { agent: 'agentB' });
  assert(
    b1.ok === false && b1.held_by === 'agentA' && b1.queue_position === 0,
    'B acquires while A holds → ok:false, held_by=agentA, queue_position=0'
  );

  // 2b. A re-acquires → idempotent, SAME token.
  const aReacq = await post(DISP_PORT, '/lease/acquire', { agent: 'agentA' });
  assert(
    aReacq.ok === true && aReacq.token === tokenA && aReacq.reused === true,
    'A re-acquires → idempotent, returns the SAME token'
  );

  // 3. A /cmd → forwarded to the stub.
  const hitsBefore = stubHits;
  const aCmd = await post(DISP_PORT, '/cmd', {
    agent: 'agentA',
    token: tokenA,
    command: 'ping',
    args: { foo: 42 },
  });
  assert(
    aCmd.ok === true && aCmd.data && aCmd.data.echoedCommand === 'ping' && aCmd.data.echoedArgs.foo === 42,
    'A /cmd → forwarded to stub, reply relayed verbatim'
  );
  assert(stubHits === hitsBefore + 1, 'A /cmd hit the stub exactly once');

  // 4. B /cmd without the lease → rejected, stub NOT hit.
  const hitsBeforeB = stubHits;
  const bCmd = await post(DISP_PORT, '/cmd', {
    agent: 'agentB',
    token: 'bogus',
    command: 'ping',
    args: {},
  });
  assert(
    bCmd.ok === false && /do not hold the bridge lease/.test(bCmd.error) && bCmd.held_by === 'agentA',
    'B /cmd without lease → rejected with held_by=agentA'
  );
  assert(stubHits === hitsBeforeB, 'B /cmd did NOT reach the stub (serialized)');

  // 5. A releases → B RESERVED (two-phase promotion; not yet holder until it confirms).
  const aRel = await post(DISP_PORT, '/lease/release', { agent: 'agentA', token: tokenA });
  assert(
    aRel.ok === true && aRel.released === true && aRel.reserved_for === 'agentB',
    'A releases → B reserved (two-phase promotion)'
  );

  // 5b. Release-when-not-holding is a no-op success.
  const aRel2 = await post(DISP_PORT, '/lease/release', { agent: 'agentA', token: tokenA });
  assert(aRel2.ok === true && aRel2.released === false, 'A releases again → idempotent no-op ok:true');

  // 6. B acquires → confirms its reservation → now the holder (fresh token minted).
  const b2 = await post(DISP_PORT, '/lease/acquire', { agent: 'agentB' });
  assert(b2.ok === true && typeof b2.token === 'string', 'B acquires → confirms reservation → holder with a token');
  const tokenB = b2.token;

  // 7. B /cmd → forwarded.
  const hitsBeforeB2 = stubHits;
  const bCmd2 = await post(DISP_PORT, '/cmd', {
    agent: 'agentB',
    token: tokenB,
    command: 'editor.captureGameView',
    args: { path: 'Logs/x.png' },
  });
  assert(
    bCmd2.ok === true && bCmd2.data.echoedCommand === 'editor.captureGameView',
    'B /cmd (editor.captureGameView is browse-safe) → forwarded to stub'
  );
  assert(stubHits === hitsBeforeB2 + 1, 'B /cmd hit the stub exactly once');

  // 8. /status reflects B holding and reachable stub.
  const status = await get(DISP_PORT, '/status');
  assert(
    status.ok === true && status.holder && status.holder.agent === 'agentB' && status.editor_reachable === true,
    '/status → holder=agentB, editor_reachable=true'
  );

  // 9. Idle-timeout auto-reclaim (TTL=1s). No /cmd from B → lease reclaimed → A can take it.
  await sleep(1400);
  const statusAfterIdle = await get(DISP_PORT, '/status');
  assert(statusAfterIdle.holder === null, 'idle-timeout → lease auto-reclaimed (holder=null)');
  const aTakeover = await post(DISP_PORT, '/lease/acquire', { agent: 'agentA' });
  assert(aTakeover.ok === true, 'after idle-reclaim, A can acquire the free lease');
  // Clean slate for the hardening scenarios.
  await post(DISP_PORT, '/lease/release', { agent: 'agentA', token: aTakeover.token });

  // ── (a) A >TTL in-flight command is NOT reclaimed mid-forward ────────────────
  console.log('\nscenario (a): a >TTL in-flight command is NOT reclaimed mid-forward');
  const inA = await post(DISP_PORT, '/lease/acquire', { agent: 'slowAgent' });
  assert(inA.ok === true, 'slowAgent acquires');
  const delayedBefore = delayedHits;
  // Fire a browse-safe command (ping) that the stub delays 1600ms — well past the 1s TTL.
  const slowPromise = post(DISP_PORT, '/cmd', {
    agent: 'slowAgent',
    token: inA.token,
    command: 'ping',
    args: { __delayMs: 1600 },
  });
  await sleep(1300); // past the TTL, while the forward is still outstanding
  const midStatus = await get(DISP_PORT, '/status');
  assert(
    midStatus.holder && midStatus.holder.agent === 'slowAgent',
    'mid-forward (t>TTL): lease still held (idle-reclaim REFUSED while in flight)'
  );
  assert(midStatus.holder && midStatus.holder.in_flight === 1, 'mid-forward: in_flight=1 reported on /status');
  const slowRes = await slowPromise;
  assert(
    slowRes.ok === true && slowRes.data && slowRes.data.echoedCommand === 'ping',
    'slow command completed and relayed (not aborted)'
  );
  assert(delayedHits === delayedBefore + 1, 'slow command reached the stub exactly once');
  const afterStatus = await get(DISP_PORT, '/status');
  assert(
    afterStatus.holder && afterStatus.holder.agent === 'slowAgent' && afterStatus.holder.in_flight === 0,
    'after completion: still holder, in_flight back to 0'
  );
  await post(DISP_PORT, '/lease/release', { agent: 'slowAgent', token: inA.token });

  // ── (b) two run-nonces for the same agent id are mutually exclusive ──────────
  console.log('\nscenario (b): two run-nonces for the SAME agent id are mutually exclusive');
  const r1 = await post(DISP_PORT, '/lease/acquire', { agent: 'dup', run: 'run-1' });
  assert(r1.ok === true && typeof r1.token === 'string', 'dup/run-1 acquires the free lease');
  const r2 = await post(DISP_PORT, '/lease/acquire', { agent: 'dup', run: 'run-2' });
  assert(
    r2.ok === false && r2.held_by === 'dup',
    'dup/run-2 is NOT treated as a reuse → reported busy (held_by=dup)'
  );
  assert(typeof r2.ttl_remaining_ms === 'number', 'busy response carries ttl_remaining_ms ETA');
  const r1again = await post(DISP_PORT, '/lease/acquire', { agent: 'dup', run: 'run-1' });
  assert(
    r1again.ok === true && r1again.token === r1.token && r1again.reused === true,
    'dup/run-1 re-acquire is idempotent (same token) — only the same run reuses'
  );
  const dupWrong = await post(DISP_PORT, '/cmd', {
    agent: 'dup',
    token: 'not-run-1-token',
    command: 'ping',
    args: {},
  });
  assert(
    dupWrong.ok === false && /do not hold the bridge lease/.test(dupWrong.error),
    'dup/run-2 (wrong token) cannot drive the bridge'
  );
  const dupRight = await post(DISP_PORT, '/cmd', { agent: 'dup', token: r1.token, command: 'ping', args: {} });
  assert(dupRight.ok === true, 'dup/run-1 (correct token) drives fine');
  await post(DISP_PORT, '/lease/release', { agent: 'dup', token: r1.token, run: 'run-1' });
  await sleep(320); // let dup/run-2's reservation lapse so the next scenario starts free

  // ── (c) a dead/unconfirmed reserved queue head is dropped after grace ────────
  console.log('\nscenario (c): a dead/unconfirmed reservation is dropped so the next live agent wins');
  const cA = await post(DISP_PORT, '/lease/acquire', { agent: 'holdC' });
  assert(cA.ok === true, 'holdC acquires');
  const cB = await post(DISP_PORT, '/lease/acquire', { agent: 'deadC' });
  assert(cB.ok === false && cB.queue_position === 0, 'deadC queued behind holdC (position 0)');
  const cC = await post(DISP_PORT, '/lease/acquire', { agent: 'liveC' });
  assert(cC.ok === false && cC.queue_position === 1, 'liveC queued behind deadC (position 1)');
  const relC = await post(DISP_PORT, '/lease/release', { agent: 'holdC', token: cA.token });
  assert(relC.reserved_for === 'deadC', 'on release, deadC (head of queue) is reserved');
  // deadC never confirms → after the grace it is dropped and liveC is reserved instead.
  await sleep(400); // > 250ms reservation grace
  const liveGrant = await post(DISP_PORT, '/lease/acquire', { agent: 'liveC' });
  assert(liveGrant.ok === true && typeof liveGrant.token === 'string', 'after deadC lapses, liveC gets the lease');
  const stC = await get(DISP_PORT, '/status');
  assert(stC.holder && stC.holder.agent === 'liveC', 'status: holder=liveC (dead deadC was skipped)');
  await post(DISP_PORT, '/lease/release', { agent: 'liveC', token: liveGrant.token });
  await sleep(20);

  // ── (d) a blocked command is rejected WITHOUT reaching the stub ──────────────
  console.log('\nscenario (d): non-allowlisted commands are rejected WITHOUT reaching the Editor');
  const dA = await post(DISP_PORT, '/lease/acquire', { agent: 'browseD' });
  assert(dA.ok === true, 'browseD acquires');
  const hitsBeforeBlocked = stubHits;
  const blocked = [
    'editor.stopBridge',
    'editor.setPlayMode',
    'editor.runTests',
    'editor.waitForCompile',
    'scene.open',
    'gameobject.create',
    'gameobject.reparent',
    'component.setProperty',
    'component.callMethod',
    'editor.executeMenuItem',
    'test.spawnWall',
    'time.advance',
  ];
  let allBlocked = true;
  for (const bad of blocked) {
    const r = await post(DISP_PORT, '/cmd', { agent: 'browseD', token: dA.token, command: bad, args: {} });
    if (!(r.ok === false && /not a browse-safe command/.test(r.error || ''))) {
      allBlocked = false;
      console.error(`    (leak) ${bad} →`, JSON.stringify(r));
    }
  }
  assert(allBlocked, `all ${blocked.length} lifecycle/scene/structure/test verbs rejected as not-browse-safe`);
  assert(stubHits === hitsBeforeBlocked, 'no blocked command reached the stub');
  // A browse-safe command still passes through.
  const okCmd = await post(DISP_PORT, '/cmd', {
    agent: 'browseD',
    token: dA.token,
    command: 'play.navTo',
    args: { target: 'Environment/Player' },
  });
  assert(
    okCmd.ok === true && okCmd.data.echoedCommand === 'play.navTo',
    'a browse-safe verb (play.navTo) is still forwarded'
  );
  await post(DISP_PORT, '/lease/release', { agent: 'browseD', token: dA.token });

  // ── teardown ──
  await stopDispatcher();
  await new Promise((r) => stub.close(r));

  console.log(`\n[test] ${passes} passed, ${failures} failed\n`);
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((err) => {
  console.error('[test] harness error:', err);
  process.exit(1);
});
