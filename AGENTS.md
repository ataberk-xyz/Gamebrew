# AGENTS.md — how an AI agent uses Gamebrew effectively

**You are an AI agent. This file is your playbook.** It tells you how to drive a *running* Unity
game through Gamebrew and get correct answers fast. Read it before your first command.

---

## What you are doing

You control a live Unity Editor over a loopback HTTP bridge. You can **move** a player, **aim**
and **orbit** cameras, **capture** the screen, **inspect** live objects/components, and **send**
input. You do this by shelling out to `mcp/browse.sh` (or the `bridge_*` MCP tools if your client
exposes them).

You **cannot stream video**. You *see* by writing a PNG and then **Reading that file**.

## Mental model (internalize this)

- **One shared world, one Editor.** If a lease dispatcher is running you must **take turns**:
  `acquire` a lease, drive, `release`. Others queue behind you.
- **Two senses, use both:**
  - **CAPTURE** = a PNG you Read → for *look, geometry, layout, legibility*.
  - **DUMP** (`debug.dumpComponent`) = live fields as JSON → for *exact numbers and state*.
  - A dim/cluttered screenshot **lies**. For anything numeric (position, color, enabled, a
    counter, a state enum) — **dump, don't squint**.
- **`play.*` verbs need Play Mode**, and you **cannot enter it** (blocked by design). If a play
  verb says "not in Play Mode", ask the human/orchestrator to enter it — don't retry.
- **FEEL is the human's call.** You judge whether something is *understandable* and *correct*
  (does the silhouette read? is the value right? is the prompt legible?). Never claim it "looks
  good."

## The core loop

```
acquire  →  navTo / orbit  →  capture  →  Read the PNG  →  dump to confirm  →  release
```

## Rules that make you effective

1. **Read every capture you take.** `capture x` prints a path. An un-Read capture proves nothing —
   Read it before you conclude anything.
2. **To judge a prop, ORBIT it — never trust one head-on shot.** Orbit at `yaw:0` (front),
   `yaw:90` (side), `yaw:180` (behind). One `navTo` capture from the player's eye hides the
   silhouette and has produced *false* verdicts ("a plain table" that actually had a full rack on
   its side).
3. **Know the two cameras (the #1 gotcha).** `play.orbitView` renders a **free** camera to
   `Logs/orbit.png`; `capture` reads the **player** camera. After an orbit, a plain `capture`
   shows the *player's* view — which the orbit never moved. **Use the `orbit` convenience verb**,
   which runs the orbit *and* copies the correct frame to `Logs/browse-<agent>-<name>.png` for
   you. If you call raw `play.orbitView`, Read **`Logs/orbit.png`** (copy it first — the next
   orbit overwrites it).
4. **Dump before you conclude.** Reading the gate/field is not running it. Confirm state with
   `debug.dumpComponent`, then decide. Instrument first, judge second.
5. **Verify by the outcome, not by a green marker.** If you changed or triggered something, dump
   the field or re-capture the frame to prove it actually happened. "It should have worked" isn't
   evidence.
6. **Release promptly. Never sit on the lease.** Idle leases auto-reclaim at ~90s and every other
   agent is blocked while you hold it. Acquire → do your thing → `release`.
7. **Don't fight the allowlist.** You get `play.*` + read/inspect verbs. Play-Mode toggle, scene
   load/save, tests, structural mutation, and killing the bridge are **rejected on purpose**.
   That's not a bug to route around — ask the orchestrator for lifecycle actions.
8. **Target by scene path; get the path first.** Use `scene.getHierarchy` / `gameobject.find` to
   learn exact paths like `Environment/Player`. Don't guess a path — a wrong target errors.
9. **One instance per run.** If the same agent id might run twice in parallel, set a distinct
   nonce once: `export GAMEBREW_BROWSE_RUN=$(uuidgen)` — every `browse.sh` call inherits it.
10. **Pace yourself to the world.** A `navTo` walk takes time and advances the game clock; capture
    *after* it arrives, not during. Commands can take up to ~130s — a slow verb is normal, not a
    hang.

## Copy-paste patterns

```bash
cd mcp

# take a turn
./browse.sh me acquire

# learn the world, then go there
./browse.sh me scene.getHierarchy '{}'
./browse.sh me gameobject.find '{"name":"Cauldron"}'
./browse.sh me play.navTo '{"target":"Environment/Cauldron","standoff":2}'

# SEE it (then Read the printed path)
./browse.sh me capture cauldron-front            # → Logs/browse-me-cauldron-front.png

# INSPECT a prop from any angle (orbit → reads the RIGHT file)
./browse.sh me orbit cauldron-side '{"target":"Environment/Cauldron","pitch":15,"yaw":90,"distance":2.5}'

# READ EXACT STATE as JSON (beats a low-contrast capture)
./browse.sh me dump Environment/Cauldron BrewController

# interact (Play Mode only)
./browse.sh me play.sendKey '{"key":"E"}'

# give the turn back
./browse.sh me release
```

## When something's off → do this

| Symptom | What it means | Fix |
|---|---|---|
| `not found: X` | wrong scene path | `scene.getHierarchy` / `gameobject.find` to get the real path |
| capture shows the player's view right after an orbit | you Read the wrong file | Read `Logs/orbit.png` (or use the `orbit` verb) |
| `LEASE BUSY … queued at N` | someone else holds the lease | wait and re-`acquire`; do not force commands |
| a `play.*` verb errors "not in Play Mode" | you can't enter Play Mode | ask the orchestrator to enter it first |
| `'<cmd>' is not a browse-safe command` | you hit the allowlist (lifecycle/mutation) | that's intentional; ask the orchestrator |
| a `capture` looks black / crushed | low-contrast scene, or wrong framing | `dump` the numbers instead of trusting the frame |

## The one-line version

> **Acquire → navigate/orbit → capture *and Read it* → dump to confirm → release.**
> Orbit props (don't shoot head-on). Trust dumps over screenshots. Feel is the human's, not yours.
> Never hold the lease.
