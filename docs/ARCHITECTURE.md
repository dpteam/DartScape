# DartScape — architecture

## Assembly layout

```
DartScape.Core      protocol, crypto, cache, logging, config, runtime supervision
DartScape.Game      world simulation, entities, skills, combat, definitions, storage
DartScape.Web       HTTP server, public API, admin API, static assets
DartScape.Relay     NAT-hairpin TCP relay
DartScape.Server    entry point: wires the above together, owns the socket and tick loop
DartScape.Client    software renderer, cache reader, protocol client
DartScape.Launcher  MSHTML shell, server browser, settings
```

The dependency graph is a strict DAG. `Game` depends on `Core`; `Web` and
`Server` depend on `Game`; `Client` depends only on `Core`. The server hosts the
web service and the relay in-process, which is why they can read live world state
without an IPC protocol.

`Core.Protocol.Protocol` holds the opcode table and is referenced by both the
server and the client, so an opcode cannot be changed on one side only.

## Threading

There are exactly three kinds of thread:

| Thread | Owns | Never touches |
|---|---|---|
| Tick loop | all world state | sockets |
| Connection threads | one socket each | world state |
| HTTP worker threads | one request each | world state |

World state has **one writer**. Connection and HTTP threads hand work to the tick
loop through `World.Enqueue`, which is a `ConcurrentQueue<Action>` drained at the
top of each tick with a per-tick budget. That is the entire concurrency model,
and it is why nothing in the game logic needs a lock.

The only shared mutable structure outside the queue is the ground item table,
which is guarded by a lock because the web layer reads it for statistics.

## The tick

600 ms, matching the original game. Per tick:

1. drain the input queue (bounded, so a flood cannot stall the world)
2. players: death, regeneration, movement, combat, level-up dialogues
3. NPCs: respawn, combat, wandering
4. ground items: decay
5. send player and NPC update packets

A fault in any of those steps is caught and logged; the tick still completes.
Only a fault that repeats does the supervisor give up.

## Update protocol

The client is a strict bit-stream reader, so the encoders mirror the client's
decoders exactly. Two rules keep that tractable:

* **The local entity list only ever appends.** The protocol can remove entities
  from the tail only; removing one from the middle shifts every subsequent index
  and the client attributes movement to the wrong entity. When that happens the
  whole list is resynchronised instead of patched.
* **An update block is written only for an entity the client was told to expect.**
  Writing a block for anything else desynchronises the packet.

`UpdateEncoder` and `PacketDecoder` are deliberately written as a matched pair
with mirrored comments; a change to one without the other is a bug.

### Deviation from stock 317

Framing is `[opcode][length:2][payload]`, with the opcode XORed against the
session's ISAAC stream. Stock 317 uses a fixed size table instead. The reason is
that a size table silently desynchronises a connection the moment one side adds a
packet, and the failure mode is "the client misplaces every entity", not an
error. An explicit length costs two bytes and cannot desync.

The login handshake keeps the stock shape — 2-byte preamble, server seed, RSA
block carrying the ISAAC seed — with one addition: the server's public modulus
travels in the greeting, so the login key can be rotated without shipping a new
client.

## Rendering

The client is a software renderer: everything is drawn into an `int[]` and
blitted once per frame with a single `LockBits` copy. No OpenGL, no Direct3D, no
shader compilation, nothing that can fail on an XP-era driver.

The world view is a top-down tile grid with billboarded humanoids. That is a
deliberate simplification of the original's 3D scene and it is honest about it:
terrain colour comes from a deterministic hash of the tile coordinate (so it is
stable and the minimap agrees with the world), and avatars are stylised
silhouettes carrying the information a player actually needs — who, friend or
foe, what they are wearing.

## Persistence

One INI file per account and one per character, written atomically: temporary
file, then rename. A power cut during a save leaves the previous good file
intact. Passwords are PBKDF2-HMAC-SHA256 with a per-account salt.

A database would be better at ten thousand accounts and considerably worse at
ten, and this project is one world on one machine.

## Fault tolerance

Three layers, each with a different scope:

1. **Inside a tick** — a faulting queued action is logged and skipped.
2. **Inside a component** — `ComponentSupervisor` restarts a failed long-running
   unit with exponential back-off, resetting the counter after a period of stable
   uptime so a rare crash never escalates.
3. **Inside a process** — `ProcessWatchdog` relaunches a dead child, with a
   rate limit so a permanently broken build cannot spin forever.

Nothing degrades silently: every layer logs, and the log file is the first thing
to read when something is wrong.

## Logging

`Log` is a front end over `System.Diagnostics.Trace`, with a `FileTraceListener`
registered **in code** — there is no `App.config`, because on an XP box the
config file is the first thing to get lost or hand-edited into an unparseable
state.

Files are `logs\<role>_<yyyyMMdd_HHmmss>.log`. Writing happens on a background
thread fed by a bounded queue, so a stalled disk drops entries instead of
stalling a tick. A ring buffer of recent records backs the admin panel's log
tail, so it does not need to read files.

## Web

`HttpServer` is a hand-written HTTP/1.1 server over `TcpListener`.
`HttpListener` was rejected for two concrete reasons: it needs a URL reservation
(administrator rights) for anything but localhost, and its behaviour behind a
proxy differs between Windows versions.

Two ports, one process:

* **43595** — public status site and public JSON API.
* **43596** — admin panel and admin API, LAN only.

Admin routes answer `404 unknown-endpoint` on the public port — not `403`. A 403
confirms the surface exists; a 404 makes it indistinguishable from a typo.

## Security posture

The threat model is a hobby server on a home connection, and the measures are
sized to it:

* passwords: PBKDF2-HMAC-SHA256, 12000 iterations, per-account salt
* admin: bearer token, constant-time comparison, per-address lockout after 10
  failures, private-address-only gating
* proxy headers: honoured only from configured trusted proxies, walked right to
  left for the first untrusted hop
* paths: normalised, percent-decoded, and re-verified against the document root
* responses: CSP pinning scripts to `self`, `nosniff`, `SAMEORIGIN`

Not implemented, and deliberately: TLS (the reverse proxy terminates it), account
recovery (out of scope), and any form of telemetry.
