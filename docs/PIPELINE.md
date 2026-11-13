# DartScape — pipeline

What is done, what is missing, and in what order the missing pieces should be
built. This document is the plan of record; if it disagrees with the code, the
code is right and this file is stale.

## Done

### Foundation

| Area | State |
|---|---|
| Build system | .NET Framework 4.0, C# 7.3, x86, no third-party packages |
| Logging | `Trace`-based, file listener, no `App.config`, async writer, ring buffer |
| Configuration | dependency-free INI with typed accessors and defaults |
| Fault tolerance | per-action, per-component (back-off), per-process (watchdog) |
| Crypto | RSA-1024 login key, ISAAC, PBKDF2-HMAC-SHA256 (hand-written: 4.0 has SHA-1 only) |
| Protocol buffer | full 317 wire format: smart values, bit access, base-37 names, byte variants |
| Cache | file store, both archive layouts, headerless bzip2, definition tables |

### Server

| Area | State |
|---|---|
| TCP listener | accept loop, per-connection threads, idle timeout, world-full refusal |
| Login | RSA handshake, revision check, name validation, duplicate-login takeover |
| Accounts | created on first login, PBKDF2 passwords, privilege levels |
| Persistence | atomic per-character INI, full inventory/equipment/bank |
| World | 600 ms tick, queued input, players, NPCs, ground items, world events |
| Movement | waypoint walking, running with energy, direction encoding |
| Combat | melee both directions, authentic hit-chance roll, XP awards, drops |
| Skills | all 21, authentic XP curve, level-up dialogues, restore on respawn |
| Death | item drop (keeps three most valuable), respawn at start |
| NPCs | definition-driven spawns, wandering, aggression, respawn timers |
| Items | definitions from cache, containers, pickup, drop, eat, use-on-item |
| Commands | 18 commands with privilege gating and argument validation |
| Persistence safety | save on logout, save all on shutdown |

### Client

| Area | State |
|---|---|
| Renderer | software framebuffer, one blit per frame, no GPU dependency |
| Text | GDI+ rendered once per distinct string, cached, LRU eviction |
| World view | interpolated tiles, humanoid avatars, overhead chat, hit bars |
| Minimap | terrain from the same hash as the world, entity dots, compass |
| Interface | fixed 765x503, chat box, six sidebar tabs, skills, inventory |
| Input | keyboard, click-to-walk, entity targeting, right-drag camera |
| Login | typed credentials, RSA to the server's advertised modulus |
| Protocol | bit-level player and NPC update decoding |

### Web

| Area | State |
|---|---|
| HTTP | hand-written HTTP/1.1, keep-alive, chunked, `Expect: 100-continue` |
| Public site | status, players, events, news, hiscores, world list |
| Admin panel | overview, players, console, log tail, config view |
| Auth | bearer token, constant-time compare, per-IP lockout, LAN-only |
| Static | MIME whitelist, traversal guards, ETag/304, cache policy |
| Themes | light, dark, ice on both surfaces |

### Relay

TCP relay with session leases, token auth, tunnel quotas, per-direction byte
accounting, half-close handling, idle and lifetime expiry.

### Quality

103 tests covering protocol, crypto, cache, game model and infrastructure.
`tools/check-xp.ps1` verifies PE subsystem version, runtime declaration, P/Invoke
allowlist and absence of post-4.0 APIs. `tools/check-standalone.ps1` verifies no
third-party packages and no development leftovers.

---

## Missing, in build order

### 1. Collision and pathfinding — highest value

**Why first:** everything that moves currently walks through walls, and a world
where you can walk through a wall does not feel like a world.

What exists: `Position`, `Direction`, a naive straight-line `WalkTo`.

What is needed:

* Load the landscape from cache store 3 (map index → terrain + objects).
* Build a per-region `CollisionMap`: blocked tiles, blocked directions,
  object footprints by size.
* Replace `WalkTo` with A* over the collision map, with the 317 rule that a
  diagonal step requires both orthogonal neighbours to be free.
* Server-side validation of every client step against the collision map.

The client's `CollisionMap` port in `refs\Rs317.Library.Client\CollisionMap.cs`
is a working reference for the data layout.

### 2. Object interaction and spawns

**Why second:** without objects there is no woodcutting, mining, fishing,
firemaking, or anything else that makes the world interactive.

What exists: the object action opcode is decoded and acknowledged.

What is needed:

* Parse `locs` from the map data into placed objects per region.
* Object definitions from the config archive (`loc.dat` / `loc.idx`).
* Send object spawn/despawn packets on region change.
* Object action handlers: examine, and per-skill actions.
* Skill actions: axe on tree, pickaxe on rock, net on fishing spot, tinderbox
  on logs. Each needs a timing loop, animation, XP award and respawn.

### 3. Full interface system

**Why third:** the bank, shop, trade, quest and dialogue windows all depend on it.

What exists: a single `OpenInterface` id and a close packet.

What is needed:

* Interface definitions from the cache media archive.
* `RSInterface` tree parsing and the client-side renderer for it.
* Item-on-interface, button routing by interface id, scrollbars.
* Bank (352 slots, insert/swap/note), shop, trade (two-sided confirm), dialogue.

### 4. Ranged and magic

What exists: melee only, with the structure in place to add more.

What is needed:

* Projectile packet (already defined, never sent).
* Spell and ammunition definitions; rune consumption.
* Distance-based combat: the tick loop currently assumes adjacency.
* Prayer: drain, effects on the hit roll.

### 5. Audio

What exists: nothing.

What needs to happen: the cache carries MIDI and sound effects (store 4).
MIDI playback on .NET 4.0 is possible via `winmm.dll` (`midiOutOpen` family) —
that is a DllImport the XP check already allows. Sound effects need a WAV
decoder and `waveOut`. Both are small and self-contained; both are optional
polish.

### 6. Proper 3D scene

What exists: a top-down tile view. It is honest, fast and works everywhere, but
it is not the game people remember.

What is needed:

* Model loading from cache store 1 (`Model` format: vertices, faces, textures).
* The client's software rasteriser: `Rasterizer.cs` and
  `Rasterizer.Gouraud.cs` in `refs\Rs317.Library.Client` are a complete
  reference implementation and would port to this framebuffer almost directly.
* Region terrain geometry from the landscape data, textures from the media
  archive.
* A camera with the original's pitch/yaw controls.

This is the largest single piece of work and should come last, because
everything above works without it.

### 7. Hiscore persistence

What exists: hiscores rank online players only.

What is needed: a durable per-skill table updated on logout and on level-up,
read by `api/hiscore`. A flat file per skill is sufficient at this scale.

### 8. Content

What exists: a starter kit, a tiny item-combination table, and 60 NPCs spawned
around the start point.

What is needed: NPC spawn tables, shop stock, quest data, drop tables. All of it
is data, none of it is engine work — which is exactly why the engine had to come
first.

---

## Known limitations, stated plainly

* **Combat is melee only.** The formulas are authentic; the variety is not.
* **NPC stats are derived from combat level.** The cache does not carry
  hitpoints, attack or defence — those lived in the original server's spawn
  tables. `DefinitionLoader.ApplyDerivedNpcStats` invents plausible values.
* **The world has no map.** Terrain is a deterministic hash, not landscape data.
  The start point is the real Lumbridge coordinate and the NPCs are real
  definitions, but the ground under them is procedural.
* **Accounts have no recovery.** Lose the password, lose the account. Adding
  email would mean SMTP, which means a dependency and a failure mode; the honest
  answer is that this is a hobby server and the operator can reset a hash by hand.
* **No TLS in the game protocol.** Passwords cross the wire inside the RSA block,
  which protects them from a passive observer and not from anything else.
* **The relay is unencrypted and unauthenticated beyond its session id.** That is
  the same trust model as a public server address.

---

## Working on this

```powershell
dotnet build DartScape.slnx -c Release
tests\DartScape.Tests\bin\Release\net40\DartScape.Tests.exe --cache assets\cache317
tools\check-xp.ps1
tools\check-standalone.ps1
tools\package.ps1
```

The test suite is the fastest way to know a change is safe: it covers the wire
format, the crypto, the cache and the game model, and it runs in under two
seconds.

When adding a packet, change `UpdateEncoder` and `PacketDecoder` together, and
add a test that round-trips it. The two files are a matched pair for a reason.
