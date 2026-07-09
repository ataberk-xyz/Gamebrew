#!/usr/bin/env node
/**
 * Unity Bridge — LEASE DISPATCHER.
 *
 * Sits IN FRONT of the Editor HTTP listener (BridgeServer.cs, http://127.0.0.1:8787/)
 * on a NEW loopback port (default 127.0.0.1:8788). It does NOT touch the 8787 path or
 * the C# bridge — it is purely additive Node infra so multiple agents can take TURNS
 * driving the single Unity Editor (one Play session / one camera / one world).
 *
 * Concurrency model: ONE lease at a time.
 *   - /lease/acquire  → grant to a free lease, or enqueue + report queue_position.
 *   - /cmd            → forward {command,args} to 8787 ONLY for the current holder.
 *   - /lease/release  → release + promote the next queued agent.
 *   - GET /status     → holder, queue, editor_reachable.
 *   - Idle-timeout    → auto-release the lease after DISPATCHER_LEASE_TTL of no /cmd.
 *
 * Single-threaded Node event loop == the serialization primitive: every request
 * handler runs to completion before the next starts, so lease state never races.
 *
 * No heavy deps — Node built-in `http` + global `fetch` only.
 */
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { isBrowseSafe, browseDenyMessage } from './browseAllowlist.mjs';

const HOST = '127.0.0.1';
const PORT = Number(process.env.DISPATCHER_PORT || 8788);
const EDITOR_URL =
  process.env.UNITY_BRIDGE_URL || process.env.GAMEBREW_BRIDGE_URL || 'http://127.0.0.1:8787/';
// Idle TTL: auto-reclaim the lease after this many seconds without a /cmd from the holder.
const LEASE_TTL_MS = Number(process.env.DISPATCHER_LEASE_TTL || 90) * 1000;
// Per-command forward timeout. Belt-and-suspenders (#1c): keep it STRICTLY BELOW the
// lease TTL by default so a slow command can never outlive the lease. The inFlight
// guard (below) is the primary defense; this just shrinks the race window. Overridable
// for tests that deliberately invert the relationship to exercise the guard.
const FORWARD_TIMEOUT_MS = Number(
  process.env.DISPATCHER_FORWARD_TIMEOUT_MS ||
    Math.max(1_000, Math.min(120_000, LEASE_TTL_MS - 10_000))
);
// Short grace re-arm when an idle-timer fire is refused (a command is in flight, or the
// holder is legitimately backing off a domain-reload). Counts idle from COMPLETION.
const RECLAIM_GRACE_MS = 5_000;
// A promoted queue head gets a RESERVATION it must confirm (via /lease/acquire) within
// this window; a dead/unconfirmed reservation is dropped so a live agent isn't blocked.
const RESERVATION_GRACE_MS = Number(process.env.DISPATCHER_RESERVATION_GRACE_MS || 12_000);

// ── Lease state ─────────────────────────────────────────────────────────────
// holder: { agent, run, token, lastActivity, inFlight, pending:Set<AbortController> } | null
//   `run` is a per-browse-session nonce so two parallel runs of the SAME agent id are
//   distinct instances (they must not both believe they hold the lease).
let holder = null;
// reservation: { agent, run, reservedAt, timer } | null — a promoted-but-unconfirmed head.
let reservation = null;
// queue: FIFO of waiting instances { agent, run } (dedup'd by agent+run).
const queue = [];
let idleTimer = null;

// Identity helpers: an "instance" is the (agent, run) pair. `run` may be undefined for
// legacy callers / the raw API — two undefined-run entries for one agent collapse to a
// single instance (back-compat), only DIFFERENT runs separate.
function sameInstance(a, b) {
  return !!a && !!b && a.agent === b.agent && a.run === b.run;
}
function queueIndexOf(agent, run) {
  return queue.findIndex((q) => q.agent === agent && q.run === run);
}
function dropFromQueue(agent, run) {
  const i = queueIndexOf(agent, run);
  if (i >= 0) queue.splice(i, 1);
}
// Nominal ms left before the holder could idle-out (for observability; not authoritative).
function ttlRemainingMs() {
  if (!holder) return 0;
  return Math.max(0, holder.lastActivity + LEASE_TTL_MS - now());
}
// Abort any in-flight forwards on a holder being torn down (release/reclaim) so a
// reclaimed agent's request is cancelled rather than left to land on the Editor.
function abortHolder(h) {
  if (!h || !h.pending) return;
  for (const c of h.pending) {
    try {
      c.abort();
    } catch {
      /* already settled */
    }
  }
  h.pending.clear();
}

function log(...a) {
  console.error('[dispatcher]', ...a);
}

function now() {
  return Date.now();
}

// Arm the idle timer. `delayMs` defaults to the full TTL; callers pass a shorter grace
// (in-flight refusal) or a longer grace (domain-reload back-off) as needed.
function armIdleTimer(delayMs = LEASE_TTL_MS) {
  clearTimeout(idleTimer);
  if (!holder) return;
  idleTimer = setTimeout(onIdleFire, delayMs);
  // Do not keep the process alive solely for the idle timer.
  if (idleTimer.unref) idleTimer.unref();
}

// Idle-timer expiry. REFUSES to reclaim while a /cmd forward is in flight — the whole
// point of the lease is that A's already-sent request must never land while B drives.
function onIdleFire() {
  if (!holder) return;
  if (holder.inFlight > 0) {
    // A command is mid-forward — re-arm a short grace so idle counts from COMPLETION,
    // not from when the (slow) command started. The forward's own timeout bounds this.
    log(
      `idle-timer fired but "${holder.agent}" has ${holder.inFlight} command(s) in flight — ` +
        `re-arming ${RECLAIM_GRACE_MS / 1000}s grace (no reclaim while in flight).`
    );
    armIdleTimer(RECLAIM_GRACE_MS);
    return;
  }
  const stale = holder;
  log(
    `idle-reclaim: lease held by "${stale.agent}" (token ${stale.token}) ` +
      `idle > ${LEASE_TTL_MS / 1000}s — reclaiming.`
  );
  abortHolder(stale); // belt-and-suspenders: cancel anything lingering on the wire
  holder = null;
  promoteNext();
}

function touchHolder() {
  if (!holder) return;
  holder.lastActivity = now();
  armIdleTimer();
}

// Two-phase promotion: promote the head of the queue to a short RESERVATION (not straight
// to holder). The agent must confirm via /lease/acquire within RESERVATION_GRACE_MS; a
// dead/unconfirmed reservation is dropped and the next instance is reserved, so a live
// agent is never blocked for the full TTL behind a crashed one.
function promoteNext() {
  if (holder || reservation) return;
  const next = queue.shift();
  if (!next) {
    clearTimeout(idleTimer);
    return;
  }
  reservation = { agent: next.agent, run: next.run, reservedAt: now(), timer: null };
  reservation.timer = setTimeout(onReservationExpire, RESERVATION_GRACE_MS);
  if (reservation.timer.unref) reservation.timer.unref();
  log(
    `reserved lease for "${next.agent}" — must confirm via /lease/acquire within ` +
      `${RESERVATION_GRACE_MS / 1000}s or it is dropped.`
  );
}

function onReservationExpire() {
  if (!reservation) return;
  log(`reservation for "${reservation.agent}" expired unconfirmed — dropping, trying next.`);
  clearReservation();
  promoteNext();
}

function clearReservation() {
  if (reservation && reservation.timer) clearTimeout(reservation.timer);
  reservation = null;
}

function queuePosition(agent, run) {
  return queueIndexOf(agent, run);
}

// ── HTTP plumbing ───────────────────────────────────────────────────────────
function sendJson(res, status, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(status, {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(body),
  });
  res.end(body);
}

function isLoopback(req) {
  const addr = req.socket.remoteAddress || '';
  // Node reports IPv4-mapped IPv6 as ::ffff:127.0.0.1; accept the loopback forms.
  return (
    addr === '127.0.0.1' ||
    addr === '::1' ||
    addr === '::ffff:127.0.0.1' ||
    addr.startsWith('127.')
  );
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > 5 * 1024 * 1024) {
        reject(new Error('request body too large'));
        req.destroy();
        return;
      }
      data += chunk;
    });
    req.on('end', () => resolve(data));
    req.on('error', reject);
  });
}

async function pingEditor(timeoutMs = 2000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(EDITOR_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: 'ping', args: {} }),
      signal: controller.signal,
    });
    const text = await res.text();
    try {
      const json = JSON.parse(text);
      return !!json.ok;
    } catch {
      return false;
    }
  } catch {
    return false;
  } finally {
    clearTimeout(timer);
  }
}

// Forward a command to the Editor bridge, relaying its JSON verbatim.
// Transient reload/connection errors are surfaced with retryable:true.
// `controller` is supplied by the caller so the reclaim path can abort an in-flight
// request (a reclaimed agent's request must be cancelled, not left to land).
async function forwardToEditor(command, args, timeoutMs = FORWARD_TIMEOUT_MS, controller = new AbortController()) {
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  let res;
  try {
    res = await fetch(EDITOR_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command, args: args || {} }),
      signal: controller.signal,
    });
  } catch (err) {
    // Connection refused mid-reload, DNS, abort — all treated as transient.
    const timedOut = err.name === 'AbortError';
    return {
      ok: false,
      error: timedOut
        ? `Editor bridge timed out after ${timeoutMs / 1000}s at ${EDITOR_URL} (may still be running).`
        : `Cannot reach Unity Editor bridge at ${EDITOR_URL} (${err.message}). It may be mid domain-reload.`,
      retryable: true,
    };
  } finally {
    clearTimeout(timer);
  }
  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch {
    return {
      ok: false,
      error: `Invalid JSON from Editor bridge (${res.status}): ${text.slice(0, 200)}`,
    };
  }
}

// ── Endpoint handlers ───────────────────────────────────────────────────────
function newHolder(agent, run) {
  return { agent, run, token: randomUUID(), lastActivity: now(), inFlight: 0, pending: new Set() };
}

function grant(agent, run) {
  holder = newHolder(agent, run);
  dropFromQueue(agent, run);
  armIdleTimer();
  return holder;
}

function handleAcquire(body, res) {
  const agent = body.agent;
  const run = body.run; // per-session nonce; undefined for legacy/raw callers
  if (!agent || typeof agent !== 'string') {
    return sendJson(res, 400, { ok: false, error: 'acquire requires a string "agent"' });
  }
  const self = { agent, run };

  // Idempotent re-acquire by the SAME instance (agent+run) → return existing token.
  if (holder && sameInstance(holder, self)) {
    touchHolder();
    return sendJson(res, 200, { ok: true, token: holder.token, agent, run, reused: true });
  }

  // Confirming a reservation held for THIS instance → promote to holder.
  if (reservation && sameInstance(reservation, self)) {
    clearReservation();
    grant(agent, run);
    log(`granted lease to "${agent}" via reservation (token ${holder.token}).`);
    return sendJson(res, 200, { ok: true, token: holder.token, agent, run });
  }

  // Free (no holder AND no pending reservation) → grant immediately.
  if (!holder && !reservation) {
    grant(agent, run);
    log(`granted lease to "${agent}" (token ${holder.token}).`);
    return sendJson(res, 200, { ok: true, token: holder.token, agent, run });
  }

  // Busy — held by another instance, OR reserved for another instance, OR the SAME
  // agent id under a DIFFERENT run nonce (a second parallel instance: NOT a reuse).
  // Enqueue this instance (dedup by agent+run) and report position + ETA.
  if (queueIndexOf(agent, run) < 0) queue.push({ agent, run });
  return sendJson(res, 200, {
    ok: false,
    held_by: holder ? holder.agent : null,
    reserved_for: reservation ? reservation.agent : null,
    queue_position: queuePosition(agent, run),
    queue_length: queue.length,
    ttl_remaining_ms: ttlRemainingMs(),
  });
}

function handleRelease(body, res) {
  const { agent, token, run } = body;
  if (!agent) {
    return sendJson(res, 400, { ok: false, error: 'release requires "agent"' });
  }

  // Release-when-not-holding is a no-op success (idempotent). Also drop any queue entry.
  if (!holder || holder.agent !== agent || (token && holder.token !== token)) {
    dropFromQueue(agent, run);
    return sendJson(res, 200, { ok: true, released: false, note: 'not the current holder (no-op)' });
  }

  log(`released lease from "${agent}" (token ${holder.token}).`);
  abortHolder(holder); // cancel any in-flight forward so it can't land after release
  holder = null;
  clearTimeout(idleTimer);
  promoteNext();
  return sendJson(res, 200, {
    ok: true,
    released: true,
    // Two-phase promotion: the next agent is now RESERVED (must confirm), not yet holder.
    reserved_for: reservation ? reservation.agent : null,
  });
}

async function handleCmd(body, res) {
  const { agent, token, command, args } = body;
  if (!command || typeof command !== 'string') {
    return sendJson(res, 400, { ok: false, error: 'cmd requires a string "command"' });
  }
  if (!holder || holder.agent !== agent || holder.token !== token) {
    const noHolder = !holder;
    return sendJson(res, 403, {
      ok: false,
      error: `you do not hold the bridge lease (held_by=${holder ? holder.agent : 'none'})`,
      held_by: holder ? holder.agent : null,
      // Raw-API stale-token hint: no holder at all → the lease must be re-acquired.
      ...(noHolder ? { retry_hint: 'lease not held — POST /lease/acquire' } : {}),
    });
  }

  // SECURITY: default-deny allowlist. A lease is for GAMEPLAY BROWSING only — the
  // orchestrator owns lifecycle / scene / tests / structural mutation via the direct
  // 8787 path. Reject blocked verbs WITHOUT forwarding (never reaches the Editor).
  if (!isBrowseSafe(command)) {
    return sendJson(res, 200, { ok: false, error: browseDenyMessage(command) });
  }

  // Guard the idle timer against the in-flight forward: increment BEFORE awaiting so a
  // timer fire during the (possibly slow) command is refused, and count idle from
  // COMPLETION. Register the AbortController so a release/reclaim can cancel the request.
  const h = holder; // capture: the finally must decrement THIS object even if holder swaps
  const controller = new AbortController();
  h.inFlight++;
  h.pending.add(controller);
  let relayed;
  try {
    relayed = await forwardToEditor(command, args, FORWARD_TIMEOUT_MS, controller);
  } finally {
    h.pending.delete(controller);
    h.inFlight--;
  }

  // Re-arm only if THIS instance is still the holder (a legit reclaim can't have fired
  // while inFlight>0, but release could have between the await settling and here).
  if (holder && holder.token === token) {
    if (relayed && relayed.retryable) {
      // Domain-reload back-off (#5): the holder keeps the lease but will poll/retry.
      // Give a LONGER grace so a big recompile can't lose the lease between attempts.
      armIdleTimer(2 * LEASE_TTL_MS);
    } else {
      touchHolder();
    }
  }
  return sendJson(res, 200, relayed);
}

async function handleStatus(res) {
  const editorReachable = await pingEditor();
  return sendJson(res, 200, {
    ok: true,
    holder: holder
      ? {
          agent: holder.agent,
          token: holder.token,
          lastActivity: holder.lastActivity,
          in_flight: holder.inFlight,
          ttl_remaining_ms: ttlRemainingMs(),
        }
      : null,
    reservation: reservation ? { agent: reservation.agent, reservedAt: reservation.reservedAt } : null,
    queue: queue.map((q) => q.agent),
    editor_reachable: editorReachable,
    lease_ttl_ms: LEASE_TTL_MS,
    forward_timeout_ms: FORWARD_TIMEOUT_MS,
    editor_url: EDITOR_URL,
  });
}

// ── Server ──────────────────────────────────────────────────────────────────
const server = http.createServer(async (req, res) => {
  if (!isLoopback(req)) {
    return sendJson(res, 403, { ok: false, error: 'non-loopback rejected' });
  }

  const { method } = req;
  const url = new URL(req.url, `http://${HOST}:${PORT}`);
  const path = url.pathname;

  try {
    if (method === 'GET' && path === '/status') {
      return await handleStatus(res);
    }

    if (method === 'POST') {
      const raw = await readBody(req);
      let body;
      try {
        body = raw ? JSON.parse(raw) : {};
      } catch {
        return sendJson(res, 400, { ok: false, error: 'invalid JSON body' });
      }

      if (path === '/lease/acquire') return handleAcquire(body, res);
      if (path === '/lease/release') return handleRelease(body, res);
      if (path === '/cmd') return await handleCmd(body, res);
    }

    return sendJson(res, 404, { ok: false, error: `no route for ${method} ${path}` });
  } catch (err) {
    return sendJson(res, 500, { ok: false, error: String(err && err.message ? err.message : err) });
  }
});

// Only auto-listen when run directly (the test harness imports startDispatcher instead).
export function startDispatcher(port = PORT) {
  return new Promise((resolve) => {
    server.listen(port, HOST, () => {
      log(`listening on http://${HOST}:${port}/  → forwarding to ${EDITOR_URL}`);
      log(`lease idle-TTL = ${LEASE_TTL_MS / 1000}s, forward-timeout = ${FORWARD_TIMEOUT_MS / 1000}s`);
      if (FORWARD_TIMEOUT_MS >= LEASE_TTL_MS) {
        log(
          `WARNING: forward-timeout (${FORWARD_TIMEOUT_MS / 1000}s) >= lease-TTL ` +
            `(${LEASE_TTL_MS / 1000}s). The inFlight guard still protects you, but the ` +
            `belt-and-suspenders margin is gone — prefer TTL strictly greater.`
        );
      }
      resolve(server);
    });
  });
}

export function stopDispatcher() {
  clearTimeout(idleTimer);
  clearReservation();
  if (holder) abortHolder(holder);
  // Reset lease state so a re-import within the same process starts clean (test harness).
  holder = null;
  queue.length = 0;
  return new Promise((resolve) => server.close(resolve));
}

// ESM "run directly" check.
import { fileURLToPath } from 'node:url';
const isMain = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (isMain) {
  startDispatcher();
}
