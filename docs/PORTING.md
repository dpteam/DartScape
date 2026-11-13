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
| Local movement | The client moves itself and interpolates; the server validates and reconciles. |
| Landscape data | Ported. Regions, terrain, objects and floor definitions read from the cache. |
| Colour palette | Ported. The original's HSL table with its gamma, which is what makes it look right. |

---

## 0. Movement, end to end

This is the part that felt broken for a long time, so it is worth writing down as a
pipeline rather than a set of rules.

**On the click.** The client converts the pixel to a tile, pathfinds locally, and
**takes the first step on that same frame**. It does not wait for a tick boundary
and it does not wait for the server. It then sends the whole path, so the server
knows the intent.

**Every frame after that.** `ClientWorld.AdvanceLocal` drains whole ticks off its
own clock and returns a fraction in `0..1` for the current step. That fraction is
used for three things, and they must all come from the *same* value or the motion
tears:

* the local player's drawn position, `LastX + (X - LastX) * fraction`;
* the camera position, via `MySmoothX` / `MySmoothY`;
* the click-to-tile projection, which must reconstruct the identical camera.

**On the server.** It receives a destination, re-paths from its own collision map,
and applies one tile per tick, two when running. It never trusts the client's
tiles, only its intent.

**When they disagree.** The client tracks a *shadow* position from the server's
movement bits but does not render it. A correction is applied only when the client
is standing still **and** the server has stopped moving, which is the one moment a
snap is invisible. Correcting while either side is still walking is what produced
the rubber-banding.

### The six things that caused the stutter

Each of these alone is enough to make movement look like it is lagging.

| Cause | Effect |
|---|---|
| The client waited for the server's step | Stand, then jump one tile, every tile |
| The camera snapped to whole tiles | The whole world lurched once per tick |
| Height sampled per tile | A hop on every slope, once per tile |
| The interpolation phase came from a free-running clock | The render phase drifted against the tick and occasionally rendered backwards |
| The server's step was applied on top of the client's | Two tiles of movement per tile |
| A long path was truncated at the near end | A silent gap between the player and the first waypoint, i.e. a teleport |

The last one is worth spelling out because the tests only caught it by asserting
that **every consecutive pair of waypoints is exactly one tile apart** вЂ” the gap was
at the *start* of the path, where a "does it reach the destination" test never
looks.

### Timestamps

Anything that subtracts two timestamps uses `TimeUtil.MonotonicMs()`, not
`Environment.TickCount`. The latter is 32-bit and wraps every 24.8 days; a wrap
during a subtraction produces a delta of about в€’2ВіВ№ ms, which freezes or teleports
every interpolated entity at once.

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
| `0x1000000` | **void вЂ” no floor** |

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
inverted produces a path that zig-zags into a wall вЂ” the symptom looks like a
broken collision map, which is why the test asserts that every consecutive pair of
waypoints is exactly one tile apart.

### Goal test

The goal is not always "this tile". For an interaction it is "adjacent to this
footprint", via `ReachedFacingObject`. That is why a click on an NPC stops you
next to it rather than on it, and why the search does not run to the target and
fail.

### Fallback

When the target is unreachable and the caller allows it, the search scans the 3Г—3
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
* `LEShortA` is `(byte)(v + 128), (byte)(v >> 8)` вЂ” little-endian with the *low*
  byte offset. `ShortA` is the same offset with the bytes the other way round.
  Confusing them yields a coordinate a few hundred tiles off, with no error.
* At most **25** waypoints; the rest is dropped and the client re-paths on the next
  click.
* One trailing byte: `putByteC(run)` = `(byte)(-run)`.

### Deviation in this project

The framing is `[opcode][length:2][payload]` with the opcode XORed against ISAAC,
instead of the original's `[opcode][length:1][payload]`. That means the original's
leading size byte is **not** written вЂ” the length already lives in the frame. This
is documented in `docs/ARCHITECTURE.md`.

### What the server does with it

The server **re-paths from its own collision map**. The client's path only says
where the player wants to end up; it never decides how they get there. A modified
client that sends a path through a wall gets walked around it.

---

## 4. Landscape data

**Source:** `Region.cs` (`loadTerrainBlock`, `loadTerrainTile`, `loadObjectBlock`),
`Flo.java`, `ObjectDef.java`, and the `map_index` table.

This is now ported. The world the player walks on is the real one from the cache, not
the procedural heightfield that stood in for it. Both the client and the server load
it, each from its own copy of the cache, so they agree about where the walls are
without exchanging any map data.

### 4.1 Where things live

The cache's layout is not obvious and differs from what the reference sources imply,
so it is worth writing down:

| Data | Where |
|---|---|
| Landscape (terrain + objects) | store **4**, one slot per region file |
| `map_index` (region to slots) | store 0, inside a named archive |
| `flo.dat` (floor colours) | store 0, same archive |
| `loc.dat` + `loc.idx` (objects) | store 0, same archive |

The reference server reads landscape from store 3. That is wrong for this cache: store
3 holds MIDI, and every landscape slot there is empty. Probing by slot rather than
trusting the store number is what found it.

### 4.2 The numbers

* A region file is **64x64** tiles, four planes.
* The client's scene is **104x104**, so it spans up to 3x3 region files.
* `baseX = (regionX - 6) * 8` вЂ” equivalently `playerWorldX - 48`, because the player
  always stands at scene tile 48,48.
* World tile `x` is in region `x >> 6`; the region's corner is `regionX * 64`.

### 4.3 Terrain block

Per tile, a loop of setting bytes terminated by zero:

| Byte | Meaning |
|---|---|
| `0` | end; height comes from the noise function (plane 0) or is 30 above the plane below |
| `1` | end; next byte is the explicit height (`1` means 0) |
| `2..49` | overlay floor: id follows, the low bits carry path and rotation |
| `50..81` | render rule flags, `value - 49` |
| `82..255` | underlay floor, `value - 81` |

Heights are stored negated and multiplied by eight in the original (`-elevation * 8`)
because its projection wants an offset. The conversion happens once, at load, so
nothing downstream has to remember to flip it.

### 4.4 Object block

Two nested delta streams: one for the object id, one for the position. A zero in either
ends the current level. Both use `getSmart` (one byte below 128, otherwise a big-endian
short minus `0x8000`) вЂ” **not** the model-delta variant, which is offset by 64 and
`0xC000` and walks the ids into negative numbers if used here.

Collision is applied by the object's config type, because that is what decides the rule:

| Type | Rule |
|---|---|
| 0, 1, 2, 3 | wall: blocks one edge (or two, for type 2) |
| 4..8 | wall decoration: no collision |
| 22 | ground decoration: blocks only if it has actions (a trapdoor does, flowers do not) |
| anything else | solid object: blocks its whole footprint; rotations 1 and 3 swap it |

### 4.5 The three mistakes that cost the most time

1. **Strings end at `10`, not `0`.** The zero that follows belongs to the enclosing
   record. Stopping at zero swallows that terminator, walks into the next definition,
   and produces one enormous entry built from the tail of the file. `flo.dat` parsed
   "successfully" as two definitions instead of 128.
2. **Floor ids are one-based.** The client looks them up as `Flo.cache[id - 1]`, and
   `0` means "no floor". Reading them zero-based shifts every surface by one entry,
   which is invisible until the grey road turns into the black dungeon stone вЂ” the two
   happen to be adjacent in the table.
3. **Colours are not RGB.** A floor's `0xRRGGBB` is converted to HSL, its lightness is
   scaled to three-quarters, and the result indexes a 65536-entry palette with a gamma
   of 0.8 applied. Drawing the raw RGB gives a flat, muddy world; skipping the gamma
   makes everything about a third too dark.

### 4.6 Still not ported

* **Object models.** Objects are placeholders: walls as short stone slabs, trees and
  rocks as simple shapes classified by name. The cache has the real models.
* **Textures.** Floors with a texture id and no colour get an approximation. The
  original paints them from the texture archive.
* **Other planes.** Only plane 0 is rendered and only plane 0 has collision, which is
  right for a surface world and wrong the moment a staircase exists.
* **Region packets.** The client loads regions straight from disk instead of being
  sent them, which works because both sides have the cache. A client without one
  cannot play, where the original could stream the map.

## 5. Interaction sequencing

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

`if (menuAction >= 2000) menuAction -= 2000;` вЂ” the `+2000` marker means "attack a
higher-level target". Missing it puts every option index off by one.

## 6. Menu priority

Which action a click produces depends on what is under the cursor at that exact
pixel, ordered by distance from the cursor, with NPCs beating ground items beating
objects beating walk. The current implementation checks tile membership, which is
why clicking the corner of a diagonal NPC walks instead of attacking.

## 7. Run

`RunEnabled` is read from the walk packet's trailing byte and the server applies two
tiles per tick while energy lasts, draining 100 per step. What is still missing is
the original's guard: it only takes the second step when the remaining path is long
enough, and the client's own prediction does not run at two tiles per tick yet вЂ” so
running currently makes the client one step behind per tick rather than visibly
faster.

## 8. Path continuation

The server remembers the requested destination (`Player.WalkDestination`) and
re-paths when the 25-tile queue empties, so a long click actually arrives instead of
stopping a tile short. The client still caps at 25 and stops, which is correct for
the original but means a click further than 25 tiles needs a second click.

## What was wrong before this port

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
| Floor table parsed as 2 entries instead of 128 | Strings end at `10`, not `0`; stopping at zero ate the record terminator |
| Grey road rendered as black stone | Floor ids are one-based into `flo.dat` |
| Objects with negative ids at the region corner | Object blocks use `getSmart`, not the model-delta `getSmartB` |
| Everything white in a saved PNG | Palette returned `0x00RRGGBB`; a zero alpha byte renders as white |
| World flat and muddy | Cache colours are HSL indices, not RGB; the gamma of 0.8 is what lifts them |
