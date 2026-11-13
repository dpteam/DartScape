# DartScape — Web API contract

This document is the contract between the DartScape services and the browser
assets in `src/DartScape.Web/public`. Everything here is implemented by
`DartScape.Web`; the front-end must not depend on anything that is not listed.

## Services and ports

| Service | Default port | Bind | Exposed to the internet | Purpose |
|---|---|---|---|---|
| `DartScape.Server` | 43594 | `0.0.0.0` | yes (TCP) | Game protocol |
| `DartScape.Web` | 43595 | `0.0.0.0` | yes, via Nginx Proxy Manager | Public status site + JSON API |
| `DartScape.Web --admin` | 43596 | `0.0.0.0` | **no** — LAN only | Admin panel + admin API |
| `DartScape.Relay` | 43597 | `0.0.0.0` | yes (TCP) | NAT-hairpin byte relay |

The public site is served at `https://rune.dartpower.xyz/`. Nginx Proxy Manager
terminates TLS and forwards to `127.0.0.1:43595`.

Because the site may be mounted at a sub-path, **every URL in the front-end must
be relative** (`api/status`, not `/api/status`).

## Conventions

* All responses are `application/json; charset=utf-8`.
* All timestamps are Unix seconds (integers).
* Errors use `{"ok":false,"error":"code","message":"human text"}` with a non-200
  status. Error codes are lowercase-hyphenated: `unknown-endpoint`,
  `bad-request`, `unauthorized`, `forbidden`, `rate-limited`, `not-found`.
* Success responses always include `"ok":true`.
* The API never returns a player's IP address, session id or password hash.

## Public API (port 43595)

### `GET api/health`

Cheap liveness probe for Nginx Proxy Manager / uptime monitors.

```json
{ "ok": true, "name": "DartScape", "version": "0.1.0", "role": "public", "uptimeSec": 12873 }
```

### `GET api/status`

Everything the status page renders in one call. Polled every 2–15 s.

```json
{
  "ok": true,
  "name": "DartScape",
  "version": "0.1.0",
  "revision": 317,
  "startedAt": 1763000000,
  "uptimeSec": 12873,
  "world": {
    "name": "DartScape",
    "region": "EU",
    "members": false,
    "playersOnline": 12,
    "playersMax": 2000,
    "accountsTotal": 481,
    "ticksPerSecond": 1.666,
    "averageTickMs": 41.2
  },
  "server": {
    "online": true,
    "gamePort": 43594,
    "webPort": 43595,
    "relayPort": 43597,
    "relayOnline": true,
    "publicAddress": "rune.dartpower.xyz",
    "lastRestartReason": "clean start"
  },
  "players": [
    { "name": "Dart", "combat": 42, "totalLevel": 613, "world": 1, "privilege": 2 }
  ],
  "skillTotals": [
    { "skill": "Attack", "total": 812 }
  ],
  "events": [
    { "seq": 41, "at": 1763012345, "kind": "levelup", "text": "Dart reached level 40 Woodcutting" }
  ],
  "logins24h": 87
}
```

`players` is capped at 50 entries, ordered by combat level descending. When
nobody is online it is an empty array, never `null`.

`events` is a rolling list of the last 20 world events, oldest first. `seq` is
monotonic — the front-end appends only entries with `seq` greater than the last
one it rendered.

### `GET api/worlds`

World list for the launcher's server picker.

```json
{
  "ok": true,
  "worlds": [
    { "id": 1, "name": "DartScape", "address": "rune.dartpower.xyz", "port": 43594,
      "players": 12, "max": 2000, "members": false, "region": "EU",
      "online": true, "pingMs": 42 }
  ]
}
```

### `GET api/hiscore?skill=woodcutting&limit=25`

Top players for one skill. `skill` is a lowercase skill name; `limit` is clamped
to 1..100. Unknown skill returns `bad-request`.

```json
{
  "ok": true,
  "skill": "woodcutting",
  "entries": [
    { "rank": 1, "name": "Dart", "level": 99, "xp": 13034431 }
  ]
}
```

### `GET api/news`

Static-ish news items from `data/news.json` on the server, newest first.

```json
{
  "ok": true,
  "items": [
    { "id": "2026-11-13-launch", "at": 1763000000, "title": "DartScape is live",
      "body": "The world is open. Have fun.", "author": "DartPower" }
  ]
}
```

## Admin API (port 43596)

The admin surface exists **only** on the admin port. On the public port every
`api/admin/*` path answers `404 unknown-endpoint`, so its shape is not
discoverable.

### Authentication

The admin token lives in `config.ini` as `[admin] token` and is printed to the
console (never to the log file) on first start. The browser sends it as:

```
Authorization: Bearer <token>
```

A missing or wrong token returns `401 unauthorized`. More than 10 failures from
one address within 5 minutes returns `429 rate-limited` for 5 minutes.

Additionally, if `[admin] privateOnly = true`, requests from non-private IPv4
addresses are refused with `403 forbidden` before authentication is even
attempted.

### `GET api/admin/overview`

```json
{
  "ok": true,
  "server": { "online": true, "uptimeSec": 12873, "tickRate": 1.666, "averageTickMs": 41.2,
              "memoryMb": 84.3, "threads": 19, "gcCollections": 41 },
  "world":  { "playersOnline": 12, "playersMax": 2000, "npcs": 3801, "groundItems": 214,
              "accountsTotal": 481 },
  "process": { "pid": 5312, "startedAt": 1763000000, "restarts": 0,
               "lastError": null, "logFile": "logs/server_20261113_101500.log" }
}
```

### `GET api/admin/players`

```json
{
  "ok": true,
  "players": [
    { "name": "Dart", "index": 1, "combat": 42, "totalLevel": 613,
      "x": 3222, "y": 3218, "plane": 0, "sessionSec": 812,
      "address": "203.0.113.7", "privilege": 2, "idleSec": 4 }
  ]
}
```

`address` is included here (admin only) and is the effective client address
after proxy-header resolution.

### `POST api/admin/kick`

Body: `{"name":"Dart","reason":"AFK"}`. Returns `{"ok":true}` or
`404 not-found` when the player is not online.

### `POST api/admin/broadcast`

Body: `{"text":"Server restarting in 5 minutes"}`. Sends a system message to
every online player. Returns `{"ok":true,"delivered":12}`.

### `POST api/admin/command`

Body: `{"text":"::item 995 1000 Dart"}`. Runs a server console command through
the same dispatcher the console uses. Returns
`{"ok":true,"output":["Gave 1000 coins to Dart."]}`.

### `GET api/admin/logs?since=0&limit=200`

Tails the in-memory log ring buffer.

```json
{
  "ok": true,
  "nextSince": 812,
  "lines": [
    { "seq": 811, "at": 1763012345, "level": "info", "category": "Login", "text": "Dart logged in" }
  ]
}
```

### `GET api/admin/config`

Returns the effective configuration with secrets redacted.

```json
{
  "ok": true,
  "sections": [
    { "name": "server", "entries": [ { "key": "port", "value": "43594" } ] }
  ]
}
```

## Static assets

Served from `src/DartScape.Web/public` on the public port and from
`src/DartScape.Web/admin` on the admin port.

| Path | Behaviour |
|---|---|
| `/` | `index.html` |
| `/assets/*` | Static files, `Cache-Control: public, max-age=300` |
| `/brand.css` | Generated from `config.ini [branding]`, `no-cache` |
| anything else | `404.html` with status 404 |

HTML/CSS/JS are served with `Cache-Control: no-cache, must-revalidate` so a
deploy can never leave a stale shell behind.

## Front-end constraints

The status site targets the browsers people actually have, which includes old
ones, and it must work when the page is served from a sub-path.

* ES5 only: no arrow functions, `let`/`const`, template literals, `Promise`,
  `fetch`, `classList.toggle(x, force)`. Use `XMLHttpRequest`.
* No build step, no bundler, no framework. Scripts load in dependency order with
  a `?v=` cache-buster.
* Icons: Font Awesome 6 from cdnjs is acceptable; the CSP allows exactly that
  origin. Everything must still render without it.
* CSS custom properties are fine (the *server* is the XP constraint, not the
  browser), but every theme must also degrade to a readable page.
* Themes: `light`, `dark`, `ice`. Selected via `<html data-theme="...">`,
  persisted in `localStorage` under `dartscape.theme`.
