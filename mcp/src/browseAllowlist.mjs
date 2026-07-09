/**
 * browseAllowlist.mjs — the browse-safe command policy for the lease dispatcher.
 *
 * DOCTRINE: the lease is COOPERATIVE turn-taking for GAMEPLAY BROWSING. The
 * ORCHESTRATOR owns lifecycle (Play enter/exit, scene load, tests, structural
 * mutation) via the direct 8787 MCP path. AGENTS holding a lease may only PLAY,
 * INTERACT, INSPECT, and PERCEIVE. This allowlist enforces that split so a
 * "browsing" agent cannot (accidentally or otherwise) kill the shared bridge,
 * toggle Play Mode, mutate the scene graph, or launch tests — any of which would
 * DoS or desync the whole team.
 *
 * DESIGN: DEFAULT-DENY. A command is browse-safe ONLY if it is either
 *   (a) matched by an allowed PREFIX (all `play.*` gameplay verbs — new play verbs
 *       are auto-allowed), or
 *   (b) an exact member of the small read/inspect/perceive ALLOW set below.
 * Everything else — every editor.* lifecycle verb, scene.* load/save,
 * gameobject.* and component.* structural mutation, test.* fixtures, time.advance,
 * playtest.* — is BLOCKED.
 *
 * Verb names were verified against the C# command surface (two routing layers):
 *   - unity-package/Editor/Bridge/BridgeServer.cs  (fast-path: ping,
 *     editor.runTests, editor.getTestResults, editor.waitForCompile,
 *     editor.setPlayMode, play.sendKey)
 *   - unity-package/Editor/Bridge/CommandRouter.cs (the command switch)
 */

// Exact read / inspect / perceive verbs an agent may run while browsing.
export const ALLOW_EXACT = new Set([
  'ping', // liveness
  'editor.captureGameView', // write a PNG the agent can Read (no world mutation)
  'editor.getPlayMode', // read: are we in Play Mode?
  'editor.getBridgeStatus', // read: bridge health  (NB: real verb name — the brief's "editor.getStatus" does not exist)
  'debug.dumpComponent', // read: live component fields as JSON
  'gameobject.get', // read: a single GameObject
  'gameobject.find', // read: locate a GameObject
  'scene.getHierarchy', // read: the scene hierarchy
  'perceive.aimHit', // read: raycast what the player is aiming at (the brief's "play.aimHit" is really perceive.aimHit)
  'perceive.locate', // read: locate an object in view
]);

// Prefixes whose ENTIRE namespace is browse-safe. `play.*` covers navTo/viewObject/
// orbitView/move/moveTo/mouseLook/mouseButton/setLook/sendKey/aimAt/aimAtObject/
// pickup/navTo.status and all play.minigame.* — pure gameplay interaction.
export const ALLOW_PREFIXES = ['play.'];

// Representative BLOCKED verbs (for help text / docs). NOT exhaustive — default-deny
// means anything not explicitly allowed above is blocked regardless of this list.
export const BLOCKED_EXAMPLES = [
  'editor.setPlayMode',
  'editor.stopBridge',
  'editor.runTests',
  'editor.getTestResults',
  'editor.waitForCompile',
  'editor.executeMenuItem',
  'scene.open',
  'scene.save',
  'gameobject.create',
  'gameobject.setActive',
  'gameobject.rename',
  'gameobject.reparent',
  'component.add',
  'component.setProperty',
  'component.getProperty',
  'component.callMethod',
  'test.spawnWall',
  'test.despawnWall',
  'time.advance',
  'playtest.clear',
];

// The rejection message handed back for a non-browse-safe command.
export function browseDenyMessage(command) {
  return (
    `'${command}' is not a browse-safe command — the orchestrator owns ` +
    'lifecycle / scene / tests / structural mutation'
  );
}

/** True iff `command` may be forwarded by a lease holder. Default-deny. */
export function isBrowseSafe(command) {
  if (!command || typeof command !== 'string') return false;
  if (ALLOW_EXACT.has(command)) return true;
  for (const p of ALLOW_PREFIXES) {
    if (command.startsWith(p)) return true;
  }
  return false;
}
