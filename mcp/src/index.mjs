#!/usr/bin/env node
/**
 * Unity Bridge — MCP stdio server.
 * Forwards tool calls to the Editor HTTP listener (Tools → Unity Bridge → Start).
 */
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { mkdirSync, writeFileSync } from 'fs';
import { join } from 'path';
import { z } from 'zod';
// NOTE(decouple): the original imported `runPlaytestScenario` from a game-specific
// './playtestScenario.mjs' (drove the Forbidden Brew Cultivation→sell happy path). That
// module + its two MCP tools (bridge_run_playtest_scenario / bridge_get_playtest_report)
// were dropped from the generic core — a "playtest scenario" is game-authored. Add your
// own scenario module + tool if you want a one-shot acceptance run. See examples/.
import { enterPlayAndWaitReady, exitPlayTolerant, isTransientReloadSignal } from './playModeUtils.mjs';

const BRIDGE_URL =
  process.env.UNITY_BRIDGE_URL || process.env.GAMEBREW_BRIDGE_URL || 'http://127.0.0.1:8787/';
const BRIDGE_CONTROL_FILE =
  process.env.UNITY_BRIDGE_CONTROL_FILE ||
  join(process.cwd(), '.unity-bridge', 'control.json');
const BRIDGE_MENU = process.env.UNITY_BRIDGE_MENU || 'Tools → Unity Bridge → Start';

function requestBridgeControl(action) {
  mkdirSync(join(BRIDGE_CONTROL_FILE, '..'), { recursive: true });
  writeFileSync(BRIDGE_CONTROL_FILE, JSON.stringify({ action, at: Date.now() }));
}

async function sleep(ms) {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitForBridge(timeoutMs = 15_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const ping = await bridgeCall('ping', {}, 2_000);
    if (ping.ok) return ping;
    await sleep(500);
  }
  return null;
}

/**
 * @param {string} command
 * @param {object} args
 * @param {number} [timeoutMs=30_000] - per-fetch timeout in ms. editor.runTests now returns
 *   immediately (start); use the short default and poll editor.getTestResults for completion.
 */
async function bridgeCall(command, args = {}, timeoutMs = 30_000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  let res;
  try {
    res = await fetch(BRIDGE_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command, args }),
      signal: controller.signal,
    });
  } catch (err) {
    if (err.name === 'AbortError') {
      return {
        ok: false,
        error: `Bridge call timed out on the MCP side after ${timeoutMs / 1000}s. The Unity side may still be running — check console logs.`,
      };
    }
    return {
      ok: false,
      error: `Cannot reach Unity bridge at ${BRIDGE_URL}. Start it in Unity: ${BRIDGE_MENU}. (${err.message})`,
    };
  } finally {
    clearTimeout(timer);
  }

  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch {
    return { ok: false, error: `Invalid JSON from bridge (${res.status}): ${text.slice(0, 200)}` };
  }
}

function formatResult(json) {
  return JSON.stringify(json, null, 2);
}

const server = new McpServer({
  name: 'gamebrew-bridge',
  version: '0.1.0',
});

server.tool(
  'bridge_ping',
  `Ping the Unity Editor bridge. Requires ${BRIDGE_MENU} in Unity.`,
  {},
  async () => {
    const json = await bridgeCall('ping');
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_get_status',
  'Check whether the Unity HTTP bridge is reachable and whether the listener is running.',
  {},
  async () => {
    const ping = await bridgeCall('ping', {}, 3_000);
    if (!ping.ok) {
      return {
        content: [{
          type: 'text',
          text: formatResult({
            ok: true,
            data: {
              reachable: false,
              isRunning: false,
              url: BRIDGE_URL,
            },
          }),
        }],
      };
    }
    const status = await bridgeCall('editor.getBridgeStatus', {}, 3_000);
    if (status.ok) {
      return {
        content: [{
          type: 'text',
          text: formatResult({
            ok: true,
            data: {
              reachable: true,
              ...status.data,
              project: ping.data?.project,
              unityVersion: ping.data?.unityVersion,
            },
          }),
        }],
      };
    }
    return {
      content: [{
        type: 'text',
        text: formatResult({
          ok: true,
          data: {
            reachable: true,
            isRunning: true,
            url: BRIDGE_URL,
            project: ping.data?.project,
            unityVersion: ping.data?.unityVersion,
          },
        }),
      }],
    };
  },
);

server.tool(
  'bridge_start',
  [
    'Start the Unity HTTP bridge listener on :8787.',
    'If the bridge is already up, returns immediately.',
    'If not reachable, writes a control file and waits for Unity Editor to poll it (Editor must be open).',
  ].join(' '),
  {
    timeoutMs: z
      .number()
      .int()
      .min(2_000)
      .max(60_000)
      .optional()
      .describe('How long to wait for the bridge to become reachable (default 15000)'),
  },
  async ({ timeoutMs }) => {
    const waitMs = timeoutMs ?? 15_000;
    const existing = await bridgeCall('ping', {}, 2_000);
    if (existing.ok) {
      return {
        content: [{
          type: 'text',
          text: formatResult({
            ok: true,
            data: {
              isRunning: true,
              changed: false,
              url: BRIDGE_URL,
              project: existing.data?.project,
              unityVersion: existing.data?.unityVersion,
            },
          }),
        }],
      };
    }

    requestBridgeControl('start');
    const ping = await waitForBridge(waitMs);
    if (!ping) {
      return {
        content: [{
          type: 'text',
          text: formatResult({
            ok: false,
            error:
              `Bridge did not become reachable within ${waitMs / 1000}s. ` +
              'Ensure Unity Editor is open and scripts have recompiled.',
          }),
        }],
      };
    }

    return {
      content: [{
        type: 'text',
        text: formatResult({
          ok: true,
          data: {
            isRunning: true,
            changed: true,
            url: BRIDGE_URL,
            project: ping.data?.project,
            unityVersion: ping.data?.unityVersion,
          },
        }),
      }],
    };
  },
);

server.tool(
  'bridge_stop',
  [
    'Stop the Unity HTTP bridge listener.',
    'Uses HTTP when reachable; otherwise writes a control file for Unity to poll.',
  ].join(' '),
  {},
  async () => {
    const ping = await bridgeCall('ping', {}, 2_000);
    if (!ping.ok) {
      requestBridgeControl('stop');
      return {
        content: [{
          type: 'text',
          text: formatResult({
            ok: true,
            data: {
              isRunning: false,
              changed: true,
              scheduled: 'control-file',
              url: BRIDGE_URL,
            },
          }),
        }],
      };
    }

    const json = await bridgeCall('editor.stopBridge', {}, 5_000);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_get_console_logs',
  'Read recent Unity console logs captured by the bridge.',
  {
    count: z.number().int().min(1).max(500).optional().describe('Max entries (default 50)'),
    type: z.enum(['all', 'log', 'warning', 'error']).optional().describe('Filter by log type'),
  },
  async ({ count, type }) => {
    const args = {};
    if (count != null) args.count = count;
    if (type != null) args.type = type;
    const json = await bridgeCall('editor.getConsoleLogs', args);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_capture_game_view',
  'Capture the Game view to a PNG (Play Mode only). Default path: Logs/playtest-capture.png under the project root.',
  {
    path: z.string().optional().describe('Relative PNG path from project root, e.g. Logs/my-shot.png'),
  },
  async ({ path }) => {
    const args = {};
    if (path != null) args.path = path;
    const json = await bridgeCall('editor.captureGameView', args, 15_000);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

// NOTE(decouple): the game-specific MCP tools `bridge_get_playtest_report` and
// `bridge_run_playtest_scenario` were removed here — they drove a Forbidden-Brew-authored
// happy-path (fixed key sequence p,w,t,… and a [Playtest] structured log). Their Editor-side
// handlers (editor.getPlaytestReport / playtest.clear) are stubbed in CommandRouter.cs with
// TODO(decouple) markers. Re-add a scenario tool here + a playtest log in your game to restore.

server.tool(
  'bridge_get_scene_hierarchy',
  'Get the active Unity scene hierarchy: scene name and a recursive tree of root GameObjects with name, active flag, and children.',
  {},
  async () => {
    const json = await bridgeCall('scene.getHierarchy');
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_scene_open',
  [
    'Open a Unity scene by asset path (e.g. "Assets/Scenes/Main.unity").',
    'Path must start with Assets/ and end with .unity.',
    'Closes the current scene (OpenSceneMode.Single).',
  ].join(' '),
  {
    path: z
      .string()
      .describe('Asset path of the scene, e.g. Assets/Scenes/Main.unity'),
  },
  async ({ path }) => {
    const json = await bridgeCall('scene.open', { path });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_scene_save',
  'Save the active Unity scene to its path (or an explicit path). Never opens a save dialog.',
  {
    path: z.string().optional().describe('Optional Assets/.../*.unity path; defaults to active scene path'),
  },
  async ({ path }) => {
    const args = path != null ? { path } : {};
    const json = await bridgeCall('scene.save', args);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_command',
  'Run any bridge command against the Unity Editor HTTP API.',
  {
    command: z.string().describe('Bridge command name, e.g. ping, gameobject.create'),
    args: z.record(z.unknown()).optional().describe('Command arguments object'),
  },
  async ({ command, args }) => {
    const json = await bridgeCall(command, args ?? {});
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

const pathArg = { path: z.string().describe('Hierarchy path, e.g. Player/Weapons/Sword') };

server.tool(
  'bridge_gameobject_create',
  'Create an empty GameObject at a hierarchy path (creates missing parents).',
  pathArg,
  async ({ path }) => {
    const json = await bridgeCall('gameobject.create', { path });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_gameobject_find',
  'Check whether a GameObject exists at a hierarchy path.',
  pathArg,
  async ({ path }) => {
    const json = await bridgeCall('gameobject.find', { path });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_gameobject_get',
  'Get GameObject details (name, active, tag, layer, components, children).',
  pathArg,
  async ({ path }) => {
    const json = await bridgeCall('gameobject.get', { path });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_gameobject_set_active',
  'Set a GameObject active or inactive.',
  {
    path: z.string(),
    active: z.boolean(),
  },
  async ({ path, active }) => {
    const json = await bridgeCall('gameobject.setActive', { path, active });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_gameobject_rename',
  'Rename a GameObject.',
  {
    path: z.string(),
    name: z.string(),
  },
  async ({ path, name }) => {
    const json = await bridgeCall('gameobject.rename', { path, name });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_gameobject_reparent',
  'Reparent a GameObject. Use empty parentPath for scene root.',
  {
    path: z.string(),
    parentPath: z.string().optional(),
  },
  async ({ path, parentPath }) => {
    const json = await bridgeCall('gameobject.reparent', { path, parentPath: parentPath ?? '' });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

const componentPathTypeArgs = {
  path: z.string().describe('Hierarchy path to the GameObject, e.g. Player'),
  type: z.string().describe('Component type name (short or fully-qualified), e.g. Rigidbody or MyGame.PlayerController'),
};

server.tool(
  'bridge_component_add',
  'Add a component by type name to a GameObject. Idempotent: returns existing if already present.',
  componentPathTypeArgs,
  async ({ path, type }) => {
    const json = await bridgeCall('component.add', { path, type });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_component_set_property',
  'Set a field or property on a component via reflection. Supports int, float, bool, string, enum (by name), Vector3 ({x,y,z}), and UnityEngine.Object (by path). Includes [SerializeField] private fields.',
  {
    ...componentPathTypeArgs,
    member: z.string().describe('Field or property name'),
    value: z.unknown().describe('New value; coerced to the member type'),
  },
  async ({ path, type, member, value }) => {
    const json = await bridgeCall('component.setProperty', { path, type, member, value });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_component_get_property',
  'Get the current value of a field or property on a component via reflection.',
  {
    ...componentPathTypeArgs,
    member: z.string().describe('Field or property name'),
  },
  async ({ path, type, member }) => {
    const json = await bridgeCall('component.getProperty', { path, type, member });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_component_call_method',
  'Invoke a public method on a component via reflection. Args are coerced to each parameter type. Returns the method result (stringified if not JSON-native).',
  {
    ...componentPathTypeArgs,
    method: z.string().describe('Method name'),
    args: z.array(z.unknown()).optional().describe('Positional arguments, coerced to parameter types'),
  },
  async ({ path, type, method, args }) => {
    const json = await bridgeCall('component.callMethod', { path, type, method, args: args ?? [] });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_get_play_mode',
  'Read Unity Editor Play Mode state (isPlaying, isPaused, isChanging).',
  {},
  async () => {
    const json = await bridgeCall('editor.getPlayMode');
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_set_play_mode',
  [
    'Enter or exit Unity Play Mode from the Editor.',
    'Use playing:false before bridge_run_tests (EditMode) if tests fail with "cannot be used during play mode".',
    'bridge_run_tests also auto-exits Play Mode when possible.',
  ].join(' '),
  {
    playing: z.boolean().describe('true to enter Play Mode, false to exit'),
    wait: z
      .boolean()
      .optional()
      .describe('Wait for transition to finish (default true). false returns immediately after requesting the change.'),
    timeoutMs: z
      .number()
      .int()
      .min(1000)
      .max(120_000)
      .optional()
      .describe('Max wait for transition when wait=true (default 30000)'),
  },
  async ({ playing, wait, timeoutMs }) => {
    const shouldWait = wait ?? true;

    if (playing) {
      // Entering Play Mode: domain reload tears down the bridge mid-command, so
      // editor.setPlayMode often returns an empty-body 200.  Use the resilient helper
      // that treats that as a transient reload signal and polls until truly playing.
      // wait:false bypasses the ready-poll for callers that explicitly request it.
      if (!shouldWait) {
        // Fire-and-forget: issue setPlayMode but do not poll.
        const raw = await bridgeCall('editor.setPlayMode', { playing: true }, 10_000);
        // Transient drop is expected — return ok:true so the caller knows the request was sent.
        if (!raw?.ok && isTransientReloadSignal(raw)) {
          return { content: [{ type: 'text', text: formatResult({ ok: true, data: { requested: true, waited: false } }) }] };
        }
        return { content: [{ type: 'text', text: formatResult(raw) }] };
      }
      const result = await enterPlayAndWaitReady(bridgeCall, { timeoutMs: timeoutMs ?? 90_000 });
      return { content: [{ type: 'text', text: formatResult(result) }] };
    }

    // Exiting Play Mode: also tolerant of a transient drop (rare on exit, but symmetric).
    if (!shouldWait) {
      const raw = await bridgeCall('editor.setPlayMode', { playing: false }, 10_000);
      if (!raw?.ok && isTransientReloadSignal(raw)) {
        return { content: [{ type: 'text', text: formatResult({ ok: true, data: { requested: true, waited: false } }) }] };
      }
      return { content: [{ type: 'text', text: formatResult(raw) }] };
    }
    const result = await exitPlayTolerant(bridgeCall, { timeoutMs: timeoutMs ?? 30_000 });
    return { content: [{ type: 'text', text: formatResult(result) }] };
  },
);

server.tool(
  'bridge_wait_for_compile',
  [
    'Wait until Unity finishes asset import and script compilation.',
    'Use after writing new .cs files from outside the Editor, or before Play Mode / scene commands.',
    'Optionally calls AssetDatabase.Refresh() first.',
  ].join(' '),
  {
    refresh: z
      .boolean()
      .optional()
      .describe('Refresh the asset database before waiting (default false)'),
    timeoutMs: z
      .number()
      .int()
      .min(1_000)
      .max(300_000)
      .optional()
      .describe('Max wait in ms (default 120000)'),
  },
  async ({ refresh, timeoutMs }) => {
    const args = {};
    if (refresh != null) args.refresh = refresh;
    if (timeoutMs != null) args.timeoutMs = timeoutMs;
    const wait = timeoutMs ?? 120_000;
    const json = await bridgeCall('editor.waitForCompile', args, wait + 10_000);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_send_key',
  [
    'Send a keyboard key to the running game via the Unity Input System.',
    'Requires Play Mode (use bridge_set_play_mode with playing:true first) and the Input System package.',
    'Default action is tap: press, wait one editor frame, release — so wasPressedThisFrame handlers fire.',
    'Key examples: p, 1, f1, space, enter, escape. Modifiers: shift, ctrl, alt.',
    'For chords use shift:true with the main key (e.g. shift+o). Use action down/up to hold keys.',
  ].join(' '),
  {
    key: z
      .string()
      .describe('Key name: letter (a), digit (1), f1, leftShift, space, enter, escape, etc.'),
    action: z
      .enum(['tap', 'down', 'up'])
      .optional()
      .describe('tap=press+release (default), down=hold, up=release'),
    shift: z.boolean().optional().describe('Hold Left Shift with the key'),
    ctrl: z.boolean().optional().describe('Hold Left Ctrl with the key'),
    alt: z.boolean().optional().describe('Hold Left Alt with the key'),
    holdMs: z
      .number()
      .int()
      .min(0)
      .max(10_000)
      .optional()
      .describe('Extra hold time after the frame wait before release on tap (default 50)'),
    waitFrames: z
      .number()
      .int()
      .min(0)
      .max(600)
      .optional()
      .describe('Editor frames to wait after tap before returning (default 1)'),
    timeoutMs: z
      .number()
      .int()
      .min(1000)
      .max(120_000)
      .optional()
      .describe('Max wait for frame/hold delays (default 10000)'),
  },
  async ({ key, action, shift, ctrl, alt, holdMs, waitFrames, timeoutMs }) => {
    const args = { key };
    if (action != null) args.action = action;
    if (shift != null) args.shift = shift;
    if (ctrl != null) args.ctrl = ctrl;
    if (alt != null) args.alt = alt;
    if (holdMs != null) args.holdMs = holdMs;
    if (waitFrames != null) args.waitFrames = waitFrames;
    if (timeoutMs != null) args.timeoutMs = timeoutMs;

    const wait = (timeoutMs ?? 10_000) + (holdMs ?? 50) + 5_000;
    const json = await bridgeCall('play.sendKey', args, wait);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_hold_key',
  [
    'Hold a keyboard key for a real wall-clock duration so movement (WASD / CharacterController) works.',
    'Re-asserts the key pressed state every ~10 ms across real editor frames with real Time.deltaTime.',
    'Requires Play Mode. Key vocabulary is the same as bridge_send_key.',
    'durationMs is clamped to 50..15000 ms. MCP timeout is durationMs + 5000 ms.',
  ].join(' '),
  {
    key: z
      .string()
      .describe('Key name: letter (w), digit (1), space, enter, escape, etc.'),
    durationMs: z
      .number()
      .int()
      .min(50)
      .max(15_000)
      .describe('How long to hold the key in milliseconds (clamped to 50..15000)'),
    shift: z.boolean().optional().describe('Hold Left Shift with the key'),
    ctrl: z.boolean().optional().describe('Hold Left Ctrl with the key'),
    alt: z.boolean().optional().describe('Hold Left Alt with the key'),
  },
  async ({ key, durationMs, shift, ctrl, alt }) => {
    const args = { key, durationMs };
    if (shift != null) args.shift = shift;
    if (ctrl != null) args.ctrl = ctrl;
    if (alt != null) args.alt = alt;
    const mcpTimeout = durationMs + 5_000;
    const json = await bridgeCall('play.holdKey', args, mcpTimeout);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_look',
  [
    'Move the camera by injecting a mouse delta into the Unity Input System.',
    'Requires Play Mode and the Input System package.',
    'Applies the delta (dx, dy) across one or more editor frames so FirstPersonCamera.delta.ReadValue() fires.',
    'dx = horizontal yaw (positive = right), dy = vertical pitch (positive = up, camera inverts internally).',
    'After the last frame the delta is reset to zero.',
  ].join(' '),
  {
    dx: z.number().describe('Horizontal mouse delta (pixels/frame equivalent)'),
    dy: z.number().describe('Vertical mouse delta (pixels/frame equivalent)'),
    frames: z
      .number()
      .int()
      .min(1)
      .max(600)
      .optional()
      .describe('Number of frames to apply the delta (default 1)'),
    timeoutMs: z
      .number()
      .int()
      .min(1000)
      .max(120_000)
      .optional()
      .describe('Max wait for frame completion (default 10000)'),
  },
  async ({ dx, dy, frames, timeoutMs }) => {
    const args = { dx, dy };
    if (frames != null) args.frames = frames;
    if (timeoutMs != null) args.timeoutMs = timeoutMs;
    const wait = (timeoutMs ?? 10_000) + 5_000;
    const json = await bridgeCall('play.mouseLook', args, wait);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_click',
  [
    'Send a mouse button event to the running game via the Unity Input System.',
    'Requires Play Mode and the Input System package.',
    '"click" (default) presses and releases in one frame so wasPressedThisFrame fires once.',
    '"down" holds the button; "up" releases it.',
    'PlotClickHandler uses screen-center raycasting — mouse position is NOT needed for interaction.',
  ].join(' '),
  {
    button: z
      .enum(['left', 'right', 'middle'])
      .optional()
      .describe('Mouse button (default: left)'),
    action: z
      .enum(['click', 'down', 'up'])
      .optional()
      .describe('click=press+release (default), down=hold, up=release'),
    holdMs: z
      .number()
      .int()
      .min(0)
      .max(10_000)
      .optional()
      .describe('Extra hold time in ms before release on click (default 50)'),
    timeoutMs: z
      .number()
      .int()
      .min(1000)
      .max(120_000)
      .optional()
      .describe('Max wait (default 10000)'),
  },
  async ({ button, action, holdMs, timeoutMs }) => {
    const args = {};
    if (button != null) args.button = button;
    if (action != null) args.action = action;
    if (holdMs != null) args.holdMs = holdMs;
    if (timeoutMs != null) args.timeoutMs = timeoutMs;
    const wait = (timeoutMs ?? 10_000) + (holdMs ?? 50) + 5_000;
    const json = await bridgeCall('play.mouseButton', args, wait);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

// Poll editor.getTestResults until the run reports done (or we hit overallTimeoutMs).
// Each fetch is short (no long block), so the Editor stays responsive between polls.
async function pollTestResults(runId, { overallTimeoutMs = 600_000, intervalMs = 4_000 } = {}) {
  const deadline = Date.now() + overallTimeoutMs;
  const pollArgs = runId != null ? { runId } : {};

  // eslint-disable-next-line no-constant-condition
  while (true) {
    const poll = await bridgeCall('editor.getTestResults', pollArgs, 15_000);
    if (!poll.ok) return poll; // bridge unreachable / error envelope — surface as-is

    const status = poll.data?.status;
    if (status === 'done') return poll;
    if (status === 'none') {
      return {
        ok: false,
        error: `Test run ${runId ?? '(unknown)'} is no longer tracked by the bridge (status: none). It may have been superseded by a newer run.`,
      };
    }
    // status === 'running' (or anything else transient) → keep polling
    if (Date.now() >= deadline) {
      return {
        ok: false,
        error: `Test run polling timed out after ${Math.round(overallTimeoutMs / 1000)}s; the run may still be in progress. Poll bridge_get_test_results manually.`,
        data: poll.data,
      };
    }
    await sleep(intervalMs);
  }
}

server.tool(
  'bridge_run_tests',
  [
    'Run Unity EditMode tests from the Editor via the bridge.',
    'Starts the run, then polls until it finishes — large suites run to completion without freezing the Editor.',
    'Returns pass/fail counts and failure details without closing the Editor.',
    'Waits for script compilation/import to finish before starting tests.',
    'Automatically exits Play Mode first when needed.',
    'Polls for up to 10 minutes; use bridge_get_test_results to check a run that is still going.',
    `Requires the bridge listener: ${BRIDGE_MENU}.`,
  ].join(' '),
  {
    platform: z
      .enum(['EditMode', 'PlayMode'])
      .optional()
      .describe('Test mode (default: EditMode). PlayMode returns an error — not supported yet.'),
    assembly: z
      .string()
      .optional()
      .describe('Assembly filter, e.g. "MyProject.Editor.Tests". Omit to run all EditMode assemblies.'),
    filter: z
      .string()
      .optional()
      .describe('Substring match on full test name (case-insensitive). Applied client-side after the run.'),
  },
  async ({ platform, assembly, filter }) => {
    const args = {};
    if (platform != null) args.platform = platform;
    if (assembly  != null) args.assembly  = assembly;
    if (filter    != null) args.filter    = filter;

    // START: short fetch — the bridge returns immediately with { started, runId }.
    const start = await bridgeCall('editor.runTests', args, 30_000);
    if (!start.ok) {
      return { content: [{ type: 'text', text: formatResult(start) }] };
    }

    const runId = start.data?.runId;
    // If a run was already in progress (started:false), we still poll its runId to completion.
    const final = await pollTestResults(runId);
    return { content: [{ type: 'text', text: formatResult(final) }] };
  },
);

server.tool(
  'bridge_get_test_results',
  [
    'Poll the status of the current/last bridge test run (one short fetch, no blocking).',
    'Returns { status: "running" } while in progress, { status: "done", ... } with counts + failures when complete,',
    'or { status: "none" } if no run is tracked.',
    'Use this to check on a long run that bridge_run_tests started but stopped polling.',
    `Requires the bridge listener: ${BRIDGE_MENU}.`,
  ].join(' '),
  {
    runId: z
      .string()
      .optional()
      .describe('Specific run id to query. Omit to get the current/most-recent run.'),
  },
  async ({ runId }) => {
    const args = {};
    if (runId != null) args.runId = runId;
    const json = await bridgeCall('editor.getTestResults', args, 15_000);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_aim_hit',
  [
    'Read what the player is currently aiming at (viewport-center raycast).',
    'Requires Play Mode. Returns the scene path of the hit object, its kind',
    '("interactable", "plot", "testTarget", "none"), distance, and the current interact prompt.',
    'Use to verify the player is facing the right station before sending interaction keys.',
  ].join(' '),
  {
    range: z
      .number()
      .min(0.1)
      .max(100)
      .optional()
      .describe('Max ray distance in Unity units (default 10)'),
  },
  async ({ range }) => {
    const args = {};
    if (range != null) args.range = range;
    const json = await bridgeCall('perceive.aimHit', args);
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_locate',
  [
    'Project a scene GameObject into the player camera viewport.',
    'Requires Play Mode. Returns onScreen, viewportX/Y (0..1), behindCamera, distance, and current frameCount.',
    'Use to confirm a target is visible before navigating toward it.',
  ].join(' '),
  {
    path: z
      .string()
      .describe('Scene hierarchy path of the GameObject, e.g. "Environment/MainCamera"'),
  },
  async ({ path }) => {
    const json = await bridgeCall('perceive.locate', { path });
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_is_focused',
  [
    'Check whether the Unity Editor application window is currently focused.',
    'Works in both Edit Mode and Play Mode.',
    'Returns { focused: bool }.',
  ].join(' '),
  {},
  async () => {
    const json = await bridgeCall('editor.isFocused', {});
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

server.tool(
  'bridge_clear_playtest_log',
  [
    'Clear the structured [Playtest] event queue and advance the run identifier.',
    'Requires Play Mode. Returns { runId } so callers can detect stale reports.',
    'Call before starting a new playtest sequence to ensure bridge_get_playtest_report',
    'returns only events from the current run.',
  ].join(' '),
  {},
  async () => {
    const json = await bridgeCall('playtest.clear', {});
    return { content: [{ type: 'text', text: formatResult(json) }] };
  },
);

const transport = new StdioServerTransport();
await server.connect(transport);
