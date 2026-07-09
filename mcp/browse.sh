#!/usr/bin/env bash
#
# browse.sh — agent-facing wrapper around the Unity Bridge LEASE DISPATCHER (port 8788).
#
# The dispatcher serializes access to the single Unity Editor so multiple agents can
# take TURNS driving the one game world. This script (a) acquires/reuses a lease for
# <agent> (token cached in /tmp/gamebrew-browse-<agent>.token), (b) forwards a command, and
# (c) prints the JSON reply.
#
# Usage:
#   browse.sh <agent> <verb> [json-args]
#
# BROWSE-SAFE COMMANDS ONLY (the dispatcher enforces a default-deny allowlist):
#   ALLOWED : ping, ALL play.* (navTo/viewObject/orbitView/move/mouseLook/sendKey/
#             mouseButton/aimAt/pickup/minigame.*), editor.captureGameView,
#             editor.getPlayMode, editor.getBridgeStatus, debug.dumpComponent,
#             gameobject.get, gameobject.find, scene.getHierarchy, perceive.aimHit/locate
#   BLOCKED : editor.setPlayMode/stopBridge/runTests/waitForCompile/executeMenuItem,
#             scene.open/save, gameobject.create/reparent/setActive/rename,
#             component.add/setProperty/callMethod, test.*, time.advance, playtest.*
#   The ORCHESTRATOR owns lifecycle / scene / tests / structural mutation via 8787.
#
# Lease control verbs:
#   browse.sh <agent> acquire            # take (or re-use) the lease
#   browse.sh <agent> release            # give the lease back (DO THIS PROMPTLY)
#   browse.sh <agent> status             # holder, queue, editor_reachable
#
# Convenience verbs:
#   browse.sh <agent> capture [name]     # editor.captureGameView (PLAYER camera) → Logs/browse-<agent>-<name>.png
#   browse.sh <agent> orbit <name> '{…}' # play.orbitView (FREE camera around a prop) → Logs/browse-<agent>-<name>.png
#   browse.sh <agent> dump <GO> [Comp]   # debug.dumpComponent on GameObject <GO> (optional Component)
#
# Raw command verbs (any BROWSE-SAFE bridge command; args are JSON):
#   browse.sh <agent> ping
#   browse.sh <agent> play.navTo   '{"target":"Environment/Player","standoff":2}'
#   browse.sh <agent> play.viewObject '{"target":"Environment/Player"}'
#   browse.sh <agent> play.sendKey '{"key":"E"}'
#
# Env:
#   DISPATCHER_URL   (default http://127.0.0.1:8788)
#   GAMEBREW_BROWSE_RUN    per-session nonce so two PARALLEL runs of the SAME agent id are
#                    distinct lease instances. Set it ONCE at the start of your browse
#                    session:  export GAMEBREW_BROWSE_RUN=$(uuidgen)   — every call inherits it.
#
set -euo pipefail

DISPATCHER_URL="${DISPATCHER_URL:-http://127.0.0.1:8788}"
AGENT="${1:-}"
VERB="${2:-}"
RAW_ARGS="${3:-}"

if [[ -z "$AGENT" || -z "$VERB" ]]; then
  echo "usage: browse.sh <agent> <verb> [json-args]" >&2
  echo "verbs: acquire | release | status | capture [name] | orbit <name> '{json}' | dump <GO> [Comp] | <browse-safe.command> [json]" >&2
  echo "set GAMEBREW_BROWSE_RUN once (export GAMEBREW_BROWSE_RUN=\$(uuidgen)) if your agent id runs in parallel." >&2
  exit 2
fi

# Per-browse-session nonce. Two parallel runs of the same agent id MUST NOT share a lease,
# so the lease identity is (agent + run). Set GAMEBREW_BROWSE_RUN once; a lone agent may omit it.
RUN="${GAMEBREW_BROWSE_RUN:-}"
if [[ -z "$RUN" ]]; then
  RUN="default"
  echo "NOTE: GAMEBREW_BROWSE_RUN not set — using run id \"default\". If another task-run shares your" >&2
  echo "      agent id concurrently, set a unique nonce once:  export GAMEBREW_BROWSE_RUN=\$(uuidgen)" >&2
fi
RUN="$(printf '%s' "$RUN" | tr -c 'A-Za-z0-9._-' '_')" # filename-safe

TOKEN_FILE="/tmp/gamebrew-browse-${AGENT}-${RUN}.token"

# ── JSON helpers (jq if present, else python3) ───────────────────────────────
have_jq() { command -v jq >/dev/null 2>&1; }
have_py() { command -v python3 >/dev/null 2>&1; }

if ! have_jq && ! have_py; then
  echo "ERROR: neither jq nor python3 found — need one to parse JSON." >&2
  exit 3
fi

# Extract a top-level string field from a JSON blob on stdin.
json_field() {
  local field="$1"
  if have_jq; then
    jq -r --arg f "$field" '.[$f] // empty'
  else
    python3 -c "import sys,json
try:
    d=json.load(sys.stdin)
except Exception:
    sys.exit(0)
v=d.get('$field')
print('' if v is None else v)"
  fi
}

# Pretty-print JSON on stdin (passthrough if it is not valid JSON).
pretty() {
  local blob
  blob="$(cat)"
  if have_jq; then
    printf '%s' "$blob" | jq . 2>/dev/null || printf '%s\n' "$blob"
  elif have_py; then
    printf '%s' "$blob" | python3 -c "import sys,json
s=sys.stdin.read()
try:
    print(json.dumps(json.loads(s),indent=2))
except Exception:
    sys.stdout.write(s)" 2>/dev/null || printf '%s\n' "$blob"
  else
    printf '%s\n' "$blob"
  fi
}

# POST helper. $1=path  $2=json-body. Fails loudly if dispatcher is unreachable.
post() {
  local path="$1" body="$2"
  local out
  if ! out="$(curl -sS --max-time 130 -X POST \
      -H 'Content-Type: application/json' \
      -d "$body" "${DISPATCHER_URL}${path}" 2>&1)"; then
    echo "ERROR: cannot reach dispatcher at ${DISPATCHER_URL} — is it running?" >&2
    echo "       start it with:  (cd Tools/bridge && npm run dispatcher)" >&2
    echo "       curl said: $out" >&2
    exit 4
  fi
  printf '%s' "$out"
}

get() {
  local path="$1" out
  if ! out="$(curl -sS --max-time 5 "${DISPATCHER_URL}${path}" 2>&1)"; then
    echo "ERROR: cannot reach dispatcher at ${DISPATCHER_URL} — is it running?" >&2
    echo "       start it with:  (cd Tools/bridge && npm run dispatcher)" >&2
    exit 4
  fi
  printf '%s' "$out"
}

# ── Lease management ─────────────────────────────────────────────────────────
# Ensure we hold a lease; cache the token. Prints a message + exits non-zero if queued.
ensure_lease() {
  # Try the cached token first via a cheap re-acquire (idempotent server-side).
  local resp token prev ok held pos ttl secs
  prev="$(read_token)"
  resp="$(post /lease/acquire "{\"agent\":\"${AGENT}\",\"run\":\"${RUN}\"}")"
  ok="$(printf '%s' "$resp" | json_field ok)"
  if [[ "$ok" == "true" ]]; then
    token="$(printf '%s' "$resp" | json_field token)"
    # If a fresh acquire returns a DIFFERENT token than we cached, our prior lease lapsed.
    if [[ -n "$prev" && "$prev" != "$token" ]]; then
      echo "WARNING: prior lease expired/reclaimed — you now hold a FRESH lease; the world" >&2
      echo "         may have changed since you last drove (someone else may have moved things)." >&2
    fi
    printf '%s' "$token" > "$TOKEN_FILE"
    return 0
  fi
  held="$(printf '%s' "$resp" | json_field held_by)"
  pos="$(printf '%s' "$resp" | json_field queue_position)"
  ttl="$(printf '%s' "$resp" | json_field ttl_remaining_ms)"
  secs=""
  if [[ "$ttl" =~ ^[0-9]+$ ]]; then secs=" — ~$(( (ttl + 999) / 1000 ))s until the holder could idle-out"; fi
  echo "LEASE BUSY: held by \"${held}\". You are queued at position ${pos}${secs}." >&2
  echo "Retry 'browse.sh ${AGENT} acquire' shortly, or wait — the holder auto-releases after idle." >&2
  printf '%s\n' "$resp" | pretty
  exit 5
}

read_token() {
  if [[ -f "$TOKEN_FILE" ]]; then
    cat "$TOKEN_FILE"
  else
    echo ""
  fi
}

# Forward a bridge command through /cmd using the cached token; auto-acquire if needed.
cmd() {
  # NOTE: do NOT default with ${2:-{}} — bash mis-parses the first '}' as the end of
  # the expansion, yielding '{}}'. build_cmd_body defaults an empty args to '{}'.
  local command="$1" args="${2:-}"
  ensure_lease >/dev/null
  local token
  token="$(read_token)"
  local body
  body="$(build_cmd_body "$command" "$token" "$args")"
  post /cmd "$body"
}

# Build the /cmd body safely (embeds args as a JSON object).
build_cmd_body() {
  local command="$1" token="$2" args="$3"
  [[ -z "$args" ]] && args='{}'
  if have_jq; then
    jq -cn --arg a "$AGENT" --arg r "$RUN" --arg t "$token" --arg c "$command" --argjson args "$args" \
      '{agent:$a, run:$r, token:$t, command:$c, args:$args}'
  else
    AGENT="$AGENT" RUN="$RUN" TOKEN="$token" COMMAND="$command" ARGS="$args" python3 -c "import os,json
print(json.dumps({'agent':os.environ['AGENT'],'run':os.environ['RUN'],'token':os.environ['TOKEN'],'command':os.environ['COMMAND'],'args':json.loads(os.environ['ARGS'] or '{}')}))"
  fi
}

# ── Verb dispatch ────────────────────────────────────────────────────────────
case "$VERB" in
  acquire)
    ensure_lease >/dev/null
    echo "OK: \"${AGENT}\" holds the bridge lease (token cached in ${TOKEN_FILE})." >&2
    get /status | pretty
    ;;

  release)
    token="$(read_token)"
    resp="$(post /lease/release "{\"agent\":\"${AGENT}\",\"token\":\"${token}\",\"run\":\"${RUN}\"}")"
    rm -f "$TOKEN_FILE"
    printf '%s\n' "$resp" | pretty
    ;;

  status)
    get /status | pretty
    ;;

  capture)
    name="${RAW_ARGS:-shot}"
    # 'capture' takes a bare name, not JSON — sanitize to a filename-safe token.
    name="$(printf '%s' "$name" | tr -c 'A-Za-z0-9._-' '_')"
    path="Logs/browse-${AGENT}-${name}.png"
    cmd editor.captureGameView "{\"path\":\"${path}\"}" | pretty
    echo "CAPTURED → ${path}  (Read this path to see the frame)" >&2
    ;;

  dump)
    # debug.dumpComponent needs a GameObject "path" AND a "component" type name
    # (or use a raw command with {"type":"..."} to find the first instance by type).
    go="${RAW_ARGS:-}"
    comp="${4:-}"
    if [[ -z "$go" || -z "$comp" ]]; then
      echo "usage: browse.sh ${AGENT} dump <GameObjectPath> <Component>" >&2
      echo "  e.g. browse.sh ${AGENT} dump Environment/Player Rigidbody" >&2
      echo "  (to look up by component type across the scene, use:" >&2
      echo "   browse.sh ${AGENT} debug.dumpComponent '{\"type\":\"Rigidbody\"}')" >&2
      exit 2
    fi
    args="{\"path\":\"${go}\",\"component\":\"${comp}\"}"
    cmd debug.dumpComponent "$args" | pretty
    ;;

  orbit)
    # play.orbitView renders a FREE camera to Logs/orbit.png (NOT the player camera that
    # `capture` reads). This verb runs the orbit then copies that render to a stable,
    # agent-named path so you Read the ACTUAL orbit frame — the source of the prior mis-frames.
    # Usage: browse.sh <agent> orbit <name> '{"target":"<path>","pitch":15,"yaw":90,"distance":2.5}'
    name="${RAW_ARGS:-orbit}"
    name="$(printf '%s' "$name" | tr -c 'A-Za-z0-9._-' '_')"
    orbit_args="${4:-}"
    if [[ -z "$orbit_args" ]]; then
      echo "usage: browse.sh ${AGENT} orbit <name> '{\"target\":\"<scene/path>\",\"pitch\":15,\"yaw\":90,\"distance\":2.5}'" >&2
      echo "  yaw sweeps the camera AROUND the object (try 0 / 90 / 180); pitch ~12-20; distance ~1.8-3m." >&2
      exit 2
    fi
    cmd play.orbitView "$orbit_args" | pretty
    dest="Logs/browse-${AGENT}-${name}.png"
    if [[ -f Logs/orbit.png ]]; then
      cp Logs/orbit.png "$dest"
      echo "ORBIT FRAME → ${dest}  (Read THIS path — it is the orbit camera, not the capture/player view)" >&2
    else
      echo "WARNING: Logs/orbit.png not found after play.orbitView — did the orbit run? Check the JSON reply above." >&2
    fi
    ;;

  *)
    # Raw bridge command. RAW_ARGS is the JSON args object (empty ⇒ {} in build_cmd_body).
    cmd "$VERB" "$RAW_ARGS" | pretty
    ;;
esac
