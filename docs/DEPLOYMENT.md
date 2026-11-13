# DartScape — deployment

Everything here assumes one Windows machine hosting the world, with Nginx Proxy
Manager (NPM) in front of the public site. Nothing else is required: no database,
no container runtime, no cloud service.

## Ports

| Port | Protocol | Service | Forward on the router? | Expose via NPM? |
|---|---|---|---|---|
| 43594 | TCP | Game protocol | **Yes** | No |
| 43595 | TCP/HTTP | Public status site | Only if NPM is on another machine | **Yes** |
| 43596 | TCP/HTTP | Admin panel | **Never** | Never |
| 43597 | TCP | NAT-hairpin relay | Yes, if anyone needs it | No |

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

The relay on port 43597 exists for this. Both the client and the "outside" peer
connect *outward* to the relay, and it joins the two streams. It copies bytes and
knows nothing about the game, so the world stays authoritative on the server.

The relay is on by default. Disable it with:

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
