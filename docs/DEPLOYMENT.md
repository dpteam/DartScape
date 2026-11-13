# DartScape — deployment

Everything here assumes one Windows machine hosting the world, with Nginx Proxy
Manager (NPM) in front of the public site. Nothing else is required: no database,
no container runtime, no cloud service.

## Ports

| Port | Protocol | Service | Forward on the router? | Expose via NPM? |
|---|---|---|---|---|
| 43594 | TCP | Game protocol | **Yes — the only one** | No |
| 43595 | TCP/HTTP | Public status site | No, when NPM is on this machine | **Yes** |
| 43596 | TCP/HTTP | Admin panel | **Never** | Never |
| 43597 | TCP | NAT-hairpin relay | **No** | No |

**Forward exactly one port: 43594.**

43595 is reached by Nginx Proxy Manager over the local network, so it does not
need a router rule at all. Add `-WebAny` only if NPM runs on a different machine.

43596 (admin) and 43597 (relay) are local-network services by design. Forwarding
either one exposes a surface that gains nothing from being public.

### About the relay

The relay solves NAT hairpin: a player on the same network as the server cannot
reach it by the public hostname, because the router will not loop the connection
back inside.

For this deployment there is a simpler answer, and the launcher already uses it:
**fall back to the loopback address.** If the launcher runs on the server
machine, `127.0.0.1:43594` always works. If it runs elsewhere on the LAN, the
public name usually works anyway (some routers do support hairpin), and the
operator can type the LAN address into the launcher's Server field.

The relay itself is built, tested and listening, but it is **not yet wired into
the login path** — nothing registers a session and no client attaches to it. It
exists for the future case of player-hosted worlds, where the host genuinely is
behind an unfriendly router. Until then it does nothing, which is why its port
stays on the local network.

The admin port binds to `0.0.0.0` so that any device on the home network can
reach it. That is deliberate, and it is the only reason it binds broadly: the
protection is that it is never forwarded, plus `[admin] privateOnly = true`
refuses any address outside the private ranges before authentication is even
attempted.

## First run

```
cd server
DartScape.Server.exe
```

The first start:
1. writes `config.ini` with defaults,
2. generates `keys\server.key` (the RSA key used by the login handshake),
3. generates an admin token and **prints it to the console only** — it is never
   written to the log file,
4. opens the 317 cache and loads item and NPC definitions,
5. starts four listeners.

Copy the admin token somewhere safe. If it is lost, delete the `token` line from
`config.ini` and restart; a new one is generated.

## Windows Firewall

```
tools\firewall.cmd
```

The script reads the ports from `config.ini` (so the rules cannot drift from the
configuration), elevates itself, deletes and recreates its own rules so it is
idempotent, then reports which ports are actually listening and which local
addresses NPM should point at.

If NPM runs on this same machine, the default is correct. If it runs elsewhere on
the network, use `-WebAny`.

## Nginx Proxy Manager

Create one Proxy Host:

| Field | Value |
|---|---|
| Domain Names | `rune.dartpower.xyz` |
| Scheme | `http` |
| Forward Hostname / IP | the machine's LAN address (the firewall script prints it) |
| Forward Port | `43595` |
| Block Common Exploits | on |
| Websockets Support | not needed |
| Access List | none (the site is public by design) |

Then a **new Let's Encrypt certificate** for the domain, with *Force SSL*,
*HTTP/2 Support* and *HSTS Enabled* on.

No special headers are required: the server already understands
`X-Forwarded-For` and `X-Real-IP`, but only from an address in
`[web] trustedProxies`. A request arriving directly from the internet with a
spoofed header is ignored, because the socket address is not a trusted proxy.

The site works at a sub-path as well, because every URL in the front-end is
relative — no extra configuration.

## NAT hairpin

Two players on the same home network cannot connect to the server through the
router's public address: the router will not loop the connection back inside.
The symptom is a login that hangs even though the port is forwarded and the
firewall is open.

The launcher handles the common case automatically: it tries the configured
address, and if that does not answer it retries `127.0.0.1` on the same port. So
on the machine that hosts the server, everything just works regardless of
hairpin. On another LAN machine, put the server's LAN address (the firewall
script prints it) into the launcher's Server field.

The relay on port 43597 exists for the harder case — a host genuinely behind an
unfriendly router or CGNAT — where both sides dial *outward* and the relay joins
the streams. It is built and listening but **not yet wired into the login path**,
so it does nothing today. Leave its port closed to the internet.

Disable it entirely with:

```ini
[relay]
enabled = false
```

## Backups

Everything that matters is under `server\data`:

```
data/
  accounts/      one .ini per account: name, password hash, privilege
  characters/    one .ini per character: position, skills, inventory, bank
```

Both are plain text, written atomically (temporary file then rename), so copying
the directory while the server runs is safe. Back up `keys\server.key` as well:
losing it does not lose data, but it does invalidate every client's cached
handshake.

A daily copy is enough:

```bat
robocopy "C:\DartScape\server\data" "D:\Backups\DartScape\data" /MIR /R:1 /W:1
```

## Development instance

`build.cmd` stages a complete, runnable server into the top-level `bin\` folder:

```
bin\
  DartScape.Server.exe      and the assemblies it needs
  public\  admin\           web assets
  cache\                    the 317 cache
  config.ini                written once, never overwritten
  data\  logs\  keys\       runtime state
```

That folder is the development world. Start it with `run.cmd`, play in it, keep
the characters between builds: a rebuild replaces binaries and web assets but
leaves `config.ini`, `data\`, `keys\` and `logs\` alone. `build.cmd -Clean` wipes
the build output and keeps those four as well.

The whole `bin\` tree is gitignored, so binaries, the cache copy and the live
world never enter the repository. Only `config.example.ini` is committed, as the
documented template — copy it to `config.ini` to see every available key.

The admin token is generated on first start into `bin\config.ini` and printed to
the console. To use the same token as production, copy `config.ini` from the
deployed server; to reset it, delete the `token` line and restart.

## Updating

1. Stop the server (Ctrl+C in its console, or `exit`).
2. Replace the binaries; leave `config.ini`, `keys` and `data` alone.
3. Start it again.

The supervisor restarts the world automatically if a component faults, and the
process is designed to exit cleanly on Ctrl+C so that characters are saved.

## Running as a service

The server is a console application with no service wrapper, on purpose: it
writes to stdout and reads commands from stdin, and a service would hide both.
Use the Task Scheduler with *Run whether user is logged on or not* and *Start in*
set to the server directory.

Do not use the "restart on failure" option to paper over a crash: the log will
tell you what happened, and a silent restart loop makes the next bug harder to
find.
