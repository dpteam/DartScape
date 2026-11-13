# Porting from the reference sources

The movement and interaction layer is being rebuilt from the reference sources in
`refs/` rather than invented. This document records what was taken, what the exact
rules are, and what is still missing.

`refs/` is not in the repository (see `.gitignore`), so paths below are local
paths for whoever does the work.

**Read this first:** `refs/` contains **two incompatible dialects** of the same
client.

| Source | Status |
|---|---|
| `refs\cscape-client-master\client.java` + `Class11.java` | Modified fork ("cscape"). Stream helpers flattened to big-endian; `doWalkTo` sends 1-byte coordinates. |
| `refs\Rs317.Library.Client\Client.cs`, `CollisionMap.cs`, `Region.cs` | Faithful 1:1 port of the Jagex 317 client, keeps `putLEShortA`/`putShort`/`putByteC` distinctions. |
| `refs\Reference317\CScape.Core\...` | Server written against the cscape fork, with **no collision validation at all**. |

The port follows `Rs317.Library.Client`, because it is the faithful one.

---

## Done

| Area | State |
|---|---|
| Collision flags | Ported. Bit values, `reset()` semantics, `markWall`/`markObject` tables, diagonal rule. |
| Pathfinding | Ported BFS with the original's marker encoding, queue size and goal test. |
| Walk packet | Ported: path from the client, `LEShortA` anchor, signed-byte deltas, run byte. |
| Server validation | Every step re-checked against the server's own map. |
| Terrain → collision | Procedural stand-in until the landscape loader exists. |

---

## 1. Collision flags

The constants are named by the **movement direction they forbid**, not by the side
of the tile they sit on. That distinction is the one thing that is easy to get
backwards, and it cost a debugging session.

| Bit | Movement blocked |
|---|---|
| `0x01` | south-east |
| `0x02` | south |
| `0x04` | south-west |
| `0x08` | west |
| `0x10` | north-west |
| `0x20` | north |
| `0x40` | north-east |
| `0x80` | east |
| `0x100` | tile is solid (object footprint) |
| `0x20000` | impenetrable (blocks projectiles) |
| `0x80000` | reserved, present in every mask, never set |
| `0x200000` | whole tile blocked |
| `0x1000000` | **void — no floor** |

Two facts that are easy to miss:

* **`reset()` leaves every interior tile at `0x1000000`.** Nothing is walkable
  until the map loader clears that bit per tile. A collision map that only marks
  obstacles produces a world where nobody can move at all. This is why
  `WorldTerrain.BuildCollision` calls `MarkFloor` before it marks anything.
* **A movement test is a mask comparison, not a bit test.** The mask is
  `Void | Blocked | Reserved | Solid | <direction bit>`. Testing only `Solid`
  lets a character walk into unloaded ground.

### The diagonal rule

A diagonal step needs **three** masks clear: the destination's diagonal bit, and
both orthogonal neighbours' directional bits.

```
NE into (x+1, y+1):  dest & (base|0x40) == 0
                     and (x+1, y) & (base|0x80) == 0
                     and (x, y+1) & (base|0x20) == 0
```

That third and second clause is what stops a character cutting the corner
between two walls. Without it a diagonal move is legal through any gap, which is
the single most visible difference between a real pathfinder and a straight line.

---

## 2. Pathfinding

**Source:** `client.java` `doWalkTo` (line 6063) / `Client.cs` (line 3499).

Plain **BFS**, not A*:

* `visited[104][104]` stores the direction that reached each tile; `0` = unvisited,
  `99` = the origin.
* `distance[104][104]` is only read by the "walk as close as possible" fallback.
* Queue is a **ring buffer of 4000** ints per axis.
* Bounds are `1..102`; the border ring of the map is never entered.
* Expansion order is north, east, south, west, then NW, NE, SW, SE. The order is
  not arbitrary: it decides which of two equally short paths wins.

### The marker encoding

The value stored at a tile is the direction that leads **back to its parent**:

| Value | Bits | Way back |
|---|---|---|
| 1 | `0x01` | `(0, +1)` |
| 2 | `0x02` | `(+1, 0)` |
| 3 | `0x03` | `(+1, +1)` |
| 4 | `0x04` | `(0, -1)` |
| 6 | `0x06` | `(+1, -1)` |
| 8 | `0x08` | `(-1, 0)` |
| 9 | `0x09` | `(-1, +1)` |
| 12 | `0x0C` | `(-1, -1)` |

A step **east** therefore stores **8**, because the way back is west. Getting this
inverted produces a path that zig-zags into a wall — the symptom looks like a
broken collision map, which is why the test asserts that every consecutive pair of
waypoints is exactly one tile apart.

### Goal test

The goal is not always "this tile". For an interaction it is "adjacent to this
footprint", via `ReachedFacingObject`. That is why a click on an NPC stops you
next to it rather than on it, and why the search does not run to the target and
fail.

### Fallback

When the target is unreachable and the caller allows it, the search scans the 3×3
block around the target for the visited tile with the smallest distance, and paths
there. That is what makes clicking the far side of a wall walk you up to the wall.

---

## 3. The walk packet

**Source:** `Client.cs` `SendWalkPacket` (line 3736).

```
[opcode][x: LEShortA][(dx, dy) signed-byte pairs][y: LEShort][run: putByteC]
```

* **The anchor is the first step**, closest to the player.
* Every delta is relative to **the anchor**, not to the previous waypoint.
* The trailing short is the **anchor's** y, not the destination's. This is
  genuinely strange and it is what the original does.
* `LEShortA` is `(byte)(v + 128), (byte)(v >> 8)` — little-endian with the *low*
  byte offset. `ShortA` is the same offset with the bytes the other way round.
  Confusing them yields a coordinate a few hundred tiles off, with no error.
* At most **25** waypoints; the rest is dropped and the client re-paths on the next
  click.
* One trailing byte: `putByteC(run)` = `(byte)(-run)`.

### Deviation in this project

The framing is `[opcode][length:2][payload]` with the opcode XORed against ISAAC,
instead of the original's `[opcode][length:1][payload]`. That means the original's
leading size byte is **not** written — the length already lives in the frame. This
is documented in `docs/ARCHITECTURE.md`.

### What the server does with it

The server **re-paths from its own collision map**. The client's path only says
where the player wants to end up; it never decides how they get there. A modified
client that sends a path through a wall gets walked around it.

---

## 4. Still to port

### 4.1 Landscape data (the big one)

Collision is currently generated from the procedural terrain. The real thing comes
from the cache's landscape archive:

* A **map region file** is 64×64 tiles, stored as 8×8 chunks of 8×8.
* The client's **scene** is 104×104, i.e. 13×13 chunks, so it spans up to 3×3
  region files.
* `baseX = (regionX - 6) * 8`, `baseY = (regionY - 6) * 8` — equivalently
  `playerWorldX - 48`, because the player is always at scene tile 48,48.
* Region packet **73**: `[x: LEShortA][y: LEShort]`, no length byte.
* Region packet **241**: generated region, 13×13×4 bits of 26-bit tile hashes.
* Client replies with opcode **121** once the region is built.

Terrain block format, per tile: a loop of setting bytes — `0` ends, `1` = explicit
height, `2..49` = overlay floor, `50..81` = render rule flags, `82..255` = underlay
floor.

Until this lands, the world has no real geometry: only the procedural heightfield
the renderer invents and the collision built from it.

### 4.2 Interaction sequencing

The client must remember what was clicked and re-issue the action on arrival.
The original holds `crossX`/`crossY`/`crossType`/`crossIndex` (the little marker)
and, for ground items, does a plain walk first and an adjacency walk if that fails.

Menu action ids, for reference:

| Action | id |
|---|---|
| Walk here | 516 |
| Object option 1..5 | 502, 900, 113, 872, 1062 |
| Ground item 1..5 | 652, 567, 234, 244, 213 |
| NPC option 1..5 | 20, 412, 225, 965, 478 |
| Player option 1..5 | 561, 779, 27, 577, 729 |
| Use item on object / ground / NPC / player | 62 / 511 / 582 / 491 |

`if (menuAction >= 2000) menuAction -= 2000;` — the `+2000` marker means "attack a
higher-level target". Missing it puts every option index off by one.

### 4.3 Menu priority

Which action a click produces depends on what is under the cursor at that exact
pixel, ordered by distance from the cursor, with NPCs beating ground items beating
objects beating walk. The current implementation checks tile membership, which is
why clicking the corner of a diagonal NPC walks instead of attacking.

### 4.4 Run

`RunEnabled` is read from the walk packet's trailing byte, but energy drain and the
per-step accounting are not yet correct: the original only allows two steps when
the path is long enough, and drains per step.

---

## 5. What was wrong before this port

Kept here because the same mistakes are easy to repeat.

| Symptom | Cause |
|---|---|
| Walked through walls | Straight-line `WalkTo` with no collision at all |
| Path with gaps | Marker bits applied with the wrong sign in the backtrack |
| East/west walls blocked one way | Constants named by entry side instead of movement direction |
| Everything un-walkable | Collision map reset to void and never given floors |
| `129` skill levels | `WriteByteA` wrote the plain value while the reader subtracted 128 |
| Client died on login | `NetworkStream.ReadTimeout = 0` throws; needs `Timeout.Infinite` |
| Mouse dead on login screen | `OnMouseDown` returned early in login mode |
