/**
 * playModeUtils.mjs — Resilient Play Mode entry helpers.
 *
 * Root cause: EnterPlayModeOptions=0 (default Unity project setting) triggers a full
 * domain reload when entering Play Mode. The bridge's static HttpListener is torn down
 * mid-reload, so `editor.setPlayMode` and `editor.getPlayMode` frequently return an
 * empty-body 200 (or a connection-reset / ECONNRESET fetch error). Previously this was
 * treated as a hard failure. These helpers treat it as a transient "bridge reloading"
 * signal and poll until the bridge is back + the editor is truly playing.
 *
 * Real fix (DisableDomainReload) is deferred — this is the safe interim (#5).
 */

// ---------------------------------------------------------------------------
// Transient-signal classifier
// ---------------------------------------------------------------------------

/**
 * Returns true when a bridgeCall result represents a transient "bridge reloading"
 * condition that should be retried, as opposed to a hard logic/comms error.
 *
 * Transient signals on setPlayMode / getPlayMode:
 *   - Empty-body 200 → JSON.parse("") throws → { ok:false, error:"Invalid JSON from bridge (200): " }
 *   - ECONNRESET / fetch failed during domain-reload teardown
 *   - AbortError from our own fetch timeout (very short timeout during reload)
 *
 * @param {object} result  The object returned by bridgeCall.
 * @returns {boolean}
 */
export function isTransientReloadSignal(result) {
  if (result?.ok) return false; // successful response is never transient
  const err = String(result?.error ?? '');
  // Empty-body 200 — bridge dropped the connection before writing a response body
  if (err.startsWith('Invalid JSON from bridge (200):')) return true;
  // Connection reset / ECONNRESET during domain reload teardown
  if (err.includes('ECONNRESET')) return true;
  if (err.includes('fetch failed')) return true;
  if (err.includes('Cannot reach Unity bridge')) return true;
  // Fetch timed out — can happen on a very short per-call timeout during reload
  if (err.includes('timed out')) return true;
  return false;
}

// ---------------------------------------------------------------------------
// enterPlayAndWaitReady
// ---------------------------------------------------------------------------

const NAVMESH_READY_MARKER = '[NavMeshBootstrap] NavMesh bake complete';

/**
 * Enter Play Mode and wait until the editor is fully playing AND the scene is ready
 * (NavMesh bake complete).
 *
 * Strategy:
 *   1. Issue editor.setPlayMode {playing:true} — tolerates a transient reload drop (returns
 *      without throwing; the drop is expected behaviour during domain reload).
 *   2. Poll editor.getPlayMode until isPlaying===true && isChanging===false, treating any
 *      transient response as "bridge still reloading" rather than a hard failure.
 *   3. Then poll get_console_logs for the '[NavMeshBootstrap] NavMesh bake complete' line
 *      (logged AFTER the scene is fully initialised) to confirm scene-ready.
 *   4. Return { ok:true } on success; { ok:false, error } if the total timeout elapses.
 *
 * @param {Function} bridgeCall  async (command, args?, timeoutMs?) => object
 * @param {object}   [opts]
 * @param {number}   [opts.timeoutMs=90000]  Total time budget (cold reload+scene+bake can stack).
 * @param {number}   [opts.pollMs=1500]       Interval between getPlayMode polls.
 * @param {number}   [opts.navMeshTimeoutMs=60000]  Extra time budget for the NavMesh bake after
 *                                                   the editor reports isPlaying.
 * @returns {Promise<{ok:boolean, error?:string}>}
 */
export async function enterPlayAndWaitReady(bridgeCall, opts = {}) {
  const timeoutMs       = opts.timeoutMs       ?? 90_000;
  const pollMs          = opts.pollMs          ?? 1_500;
  const navMeshTimeoutMs = opts.navMeshTimeoutMs ?? 60_000;

  // ---- Step 1: issue setPlayMode — tolerate a transient drop ----------------
  // Use a short per-call timeout so we don't block here if the bridge is mid-reload.
  const setRes = await bridgeCall('editor.setPlayMode', { playing: true }, 10_000);
  // Anything other than a transient reload signal is a genuine bridge error.
  if (!setRes?.ok && !isTransientReloadSignal(setRes)) {
    return { ok: false, error: `editor.setPlayMode failed (non-transient): ${setRes?.error ?? JSON.stringify(setRes)}` };
  }
  // If ok===false but it IS transient, that is expected — the bridge dropped mid-reload.
  // Continue to poll.

  // ---- Step 2: poll getPlayMode until isPlaying && !isChanging ---------------
  const playDeadline = Date.now() + timeoutMs;
  let isNowPlaying = false;

  while (Date.now() < playDeadline) {
    await _sleep(pollMs);

    const poll = await bridgeCall('editor.getPlayMode', {}, 5_000);
    if (!poll?.ok) {
      // Transient: bridge still reloading — keep waiting.
      if (isTransientReloadSignal(poll)) continue;
      // Non-transient bridge error — surface it.
      return { ok: false, error: `editor.getPlayMode failed (non-transient): ${poll?.error ?? JSON.stringify(poll)}` };
    }

    const d = poll.data ?? poll;
    if (d.isPlaying === true && d.isChanging === false) {
      isNowPlaying = true;
      break;
    }
    // isChanging===true means the transition is still in flight — keep polling.
  }

  if (!isNowPlaying) {
    return { ok: false, error: `Timed out waiting for Play Mode after ${timeoutMs / 1000}s (isPlaying never became true).` };
  }

  // ---- Step 3: wait for NavMesh bake complete --------------------------------
  // The marker appears in console logs AFTER the scene is fully bootstrapped.
  // We only check logs emitted AFTER entering play by recording a baseline count first.
  let baselineCount = 0;
  const logsRes = await bridgeCall('editor.getConsoleLogs', { count: 200 }, 8_000);
  if (logsRes?.ok) {
    const entries = logsRes.data?.logs ?? logsRes.data ?? [];
    baselineCount = Array.isArray(entries) ? entries.length : 0;
  }

  const bakeDeadline = Date.now() + navMeshTimeoutMs;
  while (Date.now() < bakeDeadline) {
    await _sleep(pollMs);

    const logsRes2 = await bridgeCall('editor.getConsoleLogs', { count: 300 }, 8_000);
    if (!logsRes2?.ok) continue; // transient — keep polling

    const entries = logsRes2.data?.logs ?? logsRes2.data ?? [];
    if (!Array.isArray(entries)) continue;

    // Check entries AFTER the baseline for the bake marker.
    const newEntries = entries.slice(baselineCount);
    const found = newEntries.some((e) => {
      const msg = e?.message ?? e?.text ?? String(e ?? '');
      return msg.includes(NAVMESH_READY_MARKER);
    });
    if (found) return { ok: true };
  }

  // NavMesh bake did not appear — still return ok:true with a warning.
  // The scene may have loaded without baking (e.g. no NavMesh in scene) or the log
  // key changed. Don't treat as a hard failure; the caller can decide.
  return {
    ok: true,
    warning: `Play Mode entered but '${NAVMESH_READY_MARKER}' was not seen within ${navMeshTimeoutMs / 1000}s. Scene may still be initialising.`,
  };
}

// ---------------------------------------------------------------------------
// exitPlayTolerant — symmetric helper for exiting play (lightweight)
// ---------------------------------------------------------------------------

/**
 * Exit Play Mode, tolerating a transient bridge drop on the setPlayMode call.
 * Polls getPlayMode until isPlaying===false && isChanging===false.
 *
 * @param {Function} bridgeCall
 * @param {object}   [opts]
 * @param {number}   [opts.timeoutMs=30000]
 * @param {number}   [opts.pollMs=1000]
 * @returns {Promise<{ok:boolean, error?:string}>}
 */
export async function exitPlayTolerant(bridgeCall, opts = {}) {
  const timeoutMs = opts.timeoutMs ?? 30_000;
  const pollMs    = opts.pollMs    ?? 1_000;

  const setRes = await bridgeCall('editor.setPlayMode', { playing: false }, 10_000);
  if (!setRes?.ok && !isTransientReloadSignal(setRes)) {
    return { ok: false, error: `editor.setPlayMode(false) failed: ${setRes?.error ?? JSON.stringify(setRes)}` };
  }

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    await _sleep(pollMs);
    const poll = await bridgeCall('editor.getPlayMode', {}, 5_000);
    if (!poll?.ok) {
      if (isTransientReloadSignal(poll)) continue;
      return { ok: false, error: `editor.getPlayMode failed: ${poll?.error ?? JSON.stringify(poll)}` };
    }
    const d = poll.data ?? poll;
    if (d.isPlaying === false && d.isChanging === false) return { ok: true };
  }

  return { ok: false, error: `Timed out waiting for editor to exit Play Mode after ${timeoutMs / 1000}s.` };
}

// ---------------------------------------------------------------------------
// Internal
// ---------------------------------------------------------------------------

function _sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}
