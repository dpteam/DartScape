<div align="center">

<img src="assets/icon/logo.png" width="128" alt="DartScape">

# DartScape

**A RuneScape-style world, built from scratch in C#, running on anything from Windows XP upwards.**

[![CI](https://github.com/DartPower/DartScape/actions/workflows/ci.yml/badge.svg)](https://github.com/DartPower/DartScape/actions/workflows/ci.yml)
[![.NET Framework 4.0](https://img.shields.io/badge/.NET%20Framework-4.0-512BD4)](https://dotnet.microsoft.com/)
[![No dependencies](https://img.shields.io/badge/dependencies-none-success)]()
[![Windows XP](https://img.shields.io/badge/Windows-XP%20SP3%2B-0078D6)]()

</div>

---

DartScape is a complete game: a server that simulates a world, a client that
renders it, and a launcher that gets you into it. It speaks a 317-derived
protocol, reads a real RuneScape cache, and runs on the oldest .NET Framework
that still exists.

No installer. No runtime downloads. No third-party libraries. No telemetry.
Drop the folder on a machine, run the exe, and you are in the world.

## What is in the box

| | |
|---|---|
| **Server** | 600 ms tick, players, NPCs, melee combat, 21 skills, items, ground items, commands, persistent characters |
| **Client** | Software renderer, real cache, interpolated movement, chat, sidebar interface, minimap |
| **Launcher** | MSHTML shell (works on XP), account form, server browser, three themes |
| **Web** | Public status site with live player list, plus a LAN-only admin panel |
| **Relay** | NAT-hairpin workaround for players behind the same router |

## Quick start

### Playing

```
client\DartScape.Launcher.exe
```

Type a name and a password — the account is created on first login — and press
**Play**.

### Hosting

```
server\DartScape.Server.exe
```

First start writes `config.ini`, generates the login key, and prints an admin
token. Then:

```
tools\firewall.cmd
```

which opens exactly the ports the server needs and tells you what to put in
Nginx Proxy Manager. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the full
walkthrough, including why the admin port must never be forwarded.

### Developing

```
build.cmd        # build everything, stage bin\, run the tests
run.cmd          # start the server from bin\
run.cmd client   # start the client
```

`build.cmd` produces a **ready-to-run instance in `bin\`**: assemblies, web
assets, the 317 cache, `config.ini`, and the `data\`, `logs\` and `keys\`
folders. That folder *is* the development world — start it, play in it, keep the
characters between builds.

A rebuild never touches `config.ini`, `data\`, `keys\` or `logs\`, so the local
world survives. The whole `bin\` tree is gitignored: binaries, the cache copy and
the live world never enter the repository. Only `config.example.ini` is
committed, as the documented template.

```powershell
build.cmd -Clean        # wipe bin\ build output, keep the world
build.cmd -DebugBuild   # debug build
build.cmd -SkipTests    # build only
```

## Architecture

```
DartScape.Core      protocol · crypto · cache · logging · config · supervision
DartScape.Game      world · entities · skills · combat · definitions · storage
DartScape.Web       HTTP server · public API · admin API · static assets
DartScape.Relay     NAT-hairpin TCP relay
DartScape.Server    entry point · socket · tick loop
DartScape.Client    renderer · cache reader · protocol client
DartScape.Launcher  MSHTML shell · server browser
```

One writer for world state, three kinds of thread, and a queue between them. That
single rule is why the game logic contains no locks.
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains it, along with the update
protocol, the persistence model and the security posture.

## Design decisions worth knowing

**No third-party libraries.** Not as a purity exercise — because every dependency
is a thing that can fail to load on a fifteen-year-old machine, and because a
game server should be something you can read end to end.

**Logging through `Trace`, configured in code.** No `App.config`. On an XP box
the config file is the first thing to get lost or hand-edited into an
unparseable state, and then you have no logs *and* no idea why.

**Software rendering.** No OpenGL, no Direct3D, no shader compilation. One
`int[]`, one blit per frame, works on any driver ever shipped.

**A hand-written HTTP server.** `HttpListener` needs an administrator URL
reservation and behaves differently behind a proxy on different Windows
versions. A few hundred lines over `TcpListener` behaves identically everywhere.

**Boring persistence.** One INI file per character, written to a temporary file
then renamed. A power cut leaves the previous good file intact.

**Explicit packet lengths.** Stock 317 uses a fixed size table, which
desynchronises a connection the moment one side adds a packet — and the failure
looks like "every entity is in the wrong place", not like an error. Two length
bytes cost nothing.

## Windows XP compatibility

The project targets .NET Framework 4.0, the last runtime that supports XP SP3,
and enforces the constraints in CI:

```
tools\check-xp.ps1
```

It parses PE headers to confirm every executable declares subsystem 5.01, checks
each `.config` names v4.0, audits every `DllImport` against an allowlist of APIs
that exist on XP, and fails the build if any code uses something from a later
framework (`System.Text.Json`, `HttpClient`, `Span<T>`, `nameof`, string
interpolation, and so on).

The launcher uses MSHTML, because WebView2 has no XP build and bundling Chromium
would multiply the download by thirty. Its interface is ES3/ES5 CSS 2.1, which
is what the engine that ships with Windows actually understands.

## Testing

```
dotnet build DartScape.slnx -c Release
tests\DartScape.Tests\bin\Release\net40\DartScape.Tests.exe --cache assets\cache317
```

103 tests, no test framework — the harness is a hundred lines and runs on 4.0.
They cover the wire format, the crypto, the cache, the game model and the
infrastructure. The cache tests run against the real 11 MB cache in the
repository, which is what caught the length-table index format, the
newline-terminated definition strings and the headerless bzip2 framing.

## The pipeline

What is done, what is missing, and in what order it should be built:
**[docs/PIPELINE.md](docs/PIPELINE.md)**.

The short version: collision and pathfinding next, then object interaction, then
the interface system, then the 3D scene. Combat is melee only, NPC stats are
derived from combat level because the cache does not carry them, and the terrain
is procedural rather than real map data. All of that is written down there
instead of being left for you to discover.

## Repository layout

```
bin/            the live development instance (gitignored)
src/            the seven assemblies
tests/          the test runner
tools/          build checks, packaging, firewall, icon generation
docs/           architecture, deployment, pipeline, web API
assets/         logo, icons, and the 317 cache
config.example.ini   documented config template
build.cmd       build everything and stage bin\
run.cmd         start the server, client or launcher from bin\
```

`refs/` holds the upstream reference implementations that were studied to build
this; it is excluded from version control and from every package.

`bin/` is the working world. It holds binaries, a cache copy, `config.ini` with
the admin token, the RSA key, and every account and character. None of it is in
git — which is the point: a `git clean -xdf` cannot delete your test world,
because it was never tracked in the first place.

## Licence and attribution

DartScape is an independent hobby project. It is **not affiliated with Jagex
Ltd.** RuneScape is a trademark of Jagex Ltd.

The protocol shape and the cache format are the work of Jagex; this is a
clean-room reimplementation of a client and server that speak them. No Jagex
code, artwork or audio is redistributed — the cache is read from the copy you
supply.
