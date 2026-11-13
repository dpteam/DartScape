# DartScape — requested features

Requests recorded verbatim, in the order they arrived, with enough design thought
that the work can start without re-deriving it. Nothing here is implemented yet
except where noted.

---

## 1. Dark-humour content

> «Хочу чтобы квесты были чернюморные и в целом игра такая»

The world should read as grimly funny, not as a straight RuneScape clone with a
different name. That is a content and tone decision, not an engine feature, so it
touches four places:

### Quest content

Quests are the main delivery mechanism for tone. A quest in this world should
have a premise that is funny because it is bleak, and a resolution that does not
let the player feel heroic about it.

Rules that keep it from becoming noise:

* **The joke is in the situation, never in the prose.** A quest about a village
  that eats its own dead to survive a famine is dark; writing "lol so grim" into
  the dialogue is not.
* **No joke at the player's expense for failing.** Failure should be a consequence
  of the world, not a punchline about the player being bad.
* **The world does not care.** NPCs do not react to the player as a protagonist.
  They react to a person who is in the way.
* **One laugh per beat, not three.** Dark comedy dies when it is stacked.

Concrete candidates, each of which is a real quest with a real reward:

| Working title | Premise |
|---|---|
| *The Kindness of Crows* | A farmer asks you to find his missing son. The crows know where he is. They will tell you, for a price you would rather not pay. |
| *Honest Work* | A gravedigger is behind on his quota. The graves are already full. Nobody has complained yet. |
| *A Modest Feast* | The village's annual festival needs a main course. The village has been losing people all winter. Nobody has asked where they went. |
| *The Last Customer* | A shopkeeper will not close. His only customer has been dead for eleven years and keeps coming back to return things. |

### Tone elsewhere

* **Examine text** is where most players will meet the tone. Every item and NPC
  examine line is a one-liner; they are cheap to write and they are read far more
  than any quest dialogue.
* **Death messages** should be wry, not cruel: the player just lost their items.
* **NPC idle chatter** carries the ambient grimness. A guard who mentions, flatly,
  that the last person who asked that question is in the river.

### What this needs from the engine first

Quests need a dialogue system, which needs the interface system (pipeline item 3),
which needs object interaction (item 2). So tone lands *after* the interface work,
but the examine lines and NPC chatter can be written any time — they are data.

---

## 2. Russian language

> «Хочу русский язык в игре»

Two separate problems, and they must not be conflated.

### Client and launcher text

The client renders text through `FontRenderer`, which asks GDI+ for a font. GDI+
handles Cyrillic natively with any font that has the glyphs — Tahoma and Segoe UI
both do. So Russian in the client is a matter of:

1. A string table per language, keyed by an enum rather than by English text.
2. A `language` setting in `config.ini` and the launcher.
3. Layout tolerance: Russian is roughly 15–25% wider than English for the same
   meaning, so every label that is currently sized to fit English needs to be
   measured rather than hard-coded.

The fixed 765×503 canvas is the risk. The sidebar tabs are three letters wide;
«Ски» / «Инв» / «Эки» work, but the skill list on the right will need either a
smaller font or a wider sidebar. Measure, do not guess.

### The game protocol

This is the harder half. The 317 protocol has two string paths:

* **Chat and names** go through `TextEncoding`, which uses CP1252. CP1252 has no
  Cyrillic. Sending Russian chat over the current path would produce mojibake.
* **Cache definitions** (item names, NPC names, examine text) are read from the
  cache as CP1252 too.

Options, in order of how much they cost:

| Option | Cost | Result |
|---|---|---|
| Transliterate chat to Latin | low | «privet» — ugly, and defeats the point |
| Switch `TextEncoding` to CP1251 for chat | low | Russian works in chat; item names stay English |
| Add a UTF-8 flag to the chat packet | medium | Correct, needs a protocol version bump |
| Full UTF-8 everywhere, cache strings re-encoded at load | medium | Correct and complete |

The recommendation is the third option for chat (it is one bit in a byte we
already send) and the fourth for cache strings, done once at load time into an
in-memory translated table rather than by rewriting the cache.

**The server must not assume ASCII.** `ValidatePlayerName` already rejects
non-ASCII names; that stays, because a Cyrillic name in the player list breaks
every width calculation in the client.

### What gets translated

* Interface labels: full translation.
* Examine text and NPC chatter: full translation, and this is where the dark
  humour has to land twice — a joke that works in English often dies in Russian,
  so the two versions should be written independently rather than translated.
* Quest dialogue: same.

---

## 3. De-branding

> «Сделать бы дебрендирование чтобы изи было под себя переделать что нужно
> (посмотри пример в DPT Social)»

The repository should ship a neutral identity so a fork can rename the project
without a find-and-replace across 422 occurrences.

### The DPT_Social pattern, which is the model

`DPT_Social` does this correctly and is worth copying almost directly:

* A `[branding]` section in `config.example.ini`, empty by default.
* `Core/Branding.cs` reads it and renders `/brand.css` at request time — the
  accent trio per theme (`accent`, `accentHi`, `accentLo`) plus optional surface
  overrides (`lightBg`, `darkBg`, `darkPanel`, …).
* The repository ships a **neutral palette**; the deployment's own palette lives
  in `bin\config.ini`, which is gitignored. So the source stays unbranded and the
  live instance is branded, from the same code.
* Nothing in `public/` is edited to rebrand.

`DartScape.Web/Branding.cs` already does the accent-colour half of this. What is
missing is everything that is not a colour.

### What has to become configurable

| Layer | Currently | Should be |
|---|---|---|
| Product name | `DartScape` literal in 422 places | `[branding] name` |
| Logo mark | gold "D" drawn in three renderers | `[branding] mark` (1–2 chars) |
| Window titles | `"DartScape"` in client and launcher | same setting |
| Log file prefix | `server_`, `client_` | `<slug>_server_` etc. |
| Config file name | `config.ini` | keep (it is a convention, not branding) |
| Data folders | `data/`, `keys/` | keep |
| Default server host | `rune.dartpower.xyz` | `[branding] host` |
| Web page title and footer | `DartScape — World Status` | same setting |
| Launcher brand block | hard-coded in `ui.html` | substituted at assembly time |
| Favicon | committed SVG | generated from `mark` + `accent` |
| Assembly names | `DartScape.*` | leave — renaming assemblies breaks the build for no user-visible gain |

### The mechanism

One `Branding` class in `DartScape.Core` (not in `Web`, so the client and
launcher can use it), loaded from the same `config.ini`, exposing:

```
Branding.Name        display name
Branding.Slug        filesystem-safe form, used in log and data file names
Branding.Mark        the logo glyph(s)
Branding.Tagline     one line under the name
Branding.Host        default server address
Branding.Accent      + AccentHi / AccentLo
Branding.Surfaces    optional per-theme overrides
```

Every hard-coded occurrence becomes `Branding.Name`. The launcher's HTML already
does placeholder substitution for its CSS and JS (`/*__STYLE__*/`), so the brand
block can use the same mechanism.

A `tools/check-branding.ps1` check, in the same spirit as `check-xp.ps1`: fail the
build if the literal product name appears outside `Branding.cs` and the docs. That
is the only way this stays true a year from now.

### Why not do it now

It is a mechanical change across the whole tree and it conflicts with everything
else in flight. It should be one focused commit after the renderer work settles,
not interleaved.

---

## Order

1. **Isometric renderer** — in progress. Without it the game does not look like
   the game.
2. **De-branding** — right after, while the file layout is fresh and before more
   code hard-codes the name.
3. **Russian** — needs the interface system for the labels, so it lands with
   pipeline item 3. Chat can come earlier.
4. **Dark-humour content** — needs quests, which need the interface system. The
   examine lines and NPC chatter can be written any time.

---

## 4. Full account management from the admin panel

> «Полное управление аккаунтами из админки (Удаление/бан/редактирование
> инвентаря, статов и тд и тп) и многое другое такое»

Today the admin panel can look and can kick. It cannot actually administer.

### Account actions

| Action | Notes |
|---|---|
| View an account | Everything: created date, last seen, privilege, play time, all characters |
| Edit a character | Skills, experience, position, inventory, bank, equipment |
| Delete an account | Must require typing the account name; must save a backup first |
| Reset a password | Generates a one-time password the operator reads out |
| Change privilege | Already exists as `::privilege`; needs a UI |
| Rename | Renaming breaks nothing if `NameKey` is rewritten, but it must also move the files |

### Bans and moderation

| Action | Notes |
|---|---|
| Ban an account | With a reason and an expiry (permanent, or a duration) |
| IP ban | Stored as a CIDR or exact address; must respect `trustedProxies` |
| Mute | Chat-only, with a duration |
| Kick with a reason | Already exists; needs the reason in the UI |
| Ban list | View, edit, lift |
| Notes on an account | Free text, for "this one has been trouble before" |

Bans need to be checked at login, before the world is joined, and they need to
survive a restart — so they live in `data/bans.json`, not in memory.

### Live editing

The panel already shows the player list. It should also let the operator act on
a player without a console command: give items, set a level, teleport, restore
hitpoints, force a save. Every one of those is an existing command in
`CommandDispatcher`; the work is exposing them as typed form fields rather than
a text box, and validating on the server side (never trust the form).

### The design constraint

Every mutation goes through `World.Enqueue`, because the HTTP thread must never
touch world state. And every destructive action needs a confirm step that names
what is being destroyed — "delete account Dartpower" is recoverable from a
backup; "delete account" on the wrong row is not.

---

## 5. Maintenance mode with a whitelist

> «"Сервер временно закрыт на доработку" где по вайтлист аккам тока и типа
> статусы сервера и тд и тп»

The world needs a state between "open" and "off" — closed for maintenance, but
reachable by the people doing the work.

### Server states

A single enum, surfaced everywhere:

| State | Who can log in | Public site | Launcher |
|---|---|---|---|
| `Online` | everyone | "Online" | Play enabled |
| `Maintenance` | whitelist only | "Closed for maintenance" + countdown if known | Play disabled, message shown |
| `Whitelist` | whitelist only | "Private test" | Play disabled |
| `Offline` | nobody | "Offline" | Play disabled |

The state belongs in `config.ini` (`[server] state`) so it survives a restart,
and is changed from the admin panel, not by editing the file.

### The whitelist

`data/whitelist.txt`, one name key per line, or a section in `config.ini`. Checked
in `LoginService` after the password check and before `World.AddPlayer`, so a
whitelisted player still needs a correct password.

Privilege 2 (admin) is always allowed regardless of the list — otherwise locking
yourself out is a matter of one typo.

### What the player sees

Rejecting a login is not enough; the client shows a bare status code today. The
rejection reason has to travel:

* The login response already carries a status byte. Add a distinct code for
  "closed for maintenance" and one for "not on the whitelist".
* The client maps each code to a sentence, and the launcher shows the same text
  before it even tries to connect, by asking `api/status`.

### Countdown

An optional `[server] reopenAt` timestamp. The public site shows "back in 40
minutes" instead of a flat refusal, and the launcher mirrors it. A maintenance
window with a time on it is a completely different experience from an unexplained
outage.

### What this needs from the engine

Nothing structural: `LoginResult` already has room for the codes, `api/status`
already exists, and the launcher already reads it. This is one of the cheapest
items on the list and one of the most useful for actually running a server.

---

## 6. Other things worth doing (backlog, unsorted)

Collected here so they are not lost, with no commitment to order.

* **Player reports** — a `/report` command that writes to a file the panel reads.
* **Live map viewer** — the admin panel draws the region around a player, using
  the same terrain function the client uses, so an operator can see where someone
  actually is instead of a pair of numbers.
* **Economy view** — total coins in circulation, item counts by id, so inflation
  is visible before it is a problem.
* **Chat log** — every public message, searchable, with a retention window.
* **Scheduled restarts** — "restart at 04:00 with a five-minute warning", because
  a long-running .NET process on Windows XP will eventually want one.
* **Backup button** — zip `data/` and `keys/` on demand, from the panel.
* **Graceful shutdown from the panel** — with a warning broadcast first.
* **Player count graph** — a small sparkline on the status page, from a rolling
  hourly count.

---

## 7. Handheld port (640×480, Anbernic muOS / Mono)

> «Я просто потом планирую порт на 640x480 Anbernic muOS (там Mono есть)»

Not a request to start now, but it constrains decisions made today, so it belongs
here.

### What the code already gets right

* **No Direct3D, no OpenGL, no shaders.** The renderer writes into an `int[]` and
  blits it. On a handheld that blit becomes an SDL surface or a framebuffer
  write, and nothing above it changes.
* **No third-party libraries.** Mono on a device is already a compromise; adding
  packages to it is how a port dies.
* **`DartScape.Core` and `DartScape.Game` have no UI dependency at all.** The
  server and the world model can run headless on the device, or on a PC while the
  handheld is only a client.
* **Terrain is procedural.** No map assets to ship, no cache reads needed for the
  ground, which matters on a device with 512 MB of storage.

### What will need work

| Concern | Detail |
|---|---|
| Canvas size | 765×503 is the design resolution. At 640×480 it must scale down by 0.836, which makes 11px text unreadable. The interface needs a compact layout, not a scale. |
| Interface layout | The sidebar is 190px of a 765px canvas. At 640 it has to shrink to ~140 and the chat box to ~110 high, or the world view becomes a letterbox. |
| Input | No mouse. Everything needs a gamepad mapping: stick to walk, buttons for tabs, a virtual keyboard for chat and login. |
| Text entry | Login needs an on-screen keyboard. That is a whole screen, and it has to be usable at 640×480. |
| GDI+ | `FontRenderer` uses `System.Drawing` for text. Mono on ARM has a `System.Drawing` shim, but its font metrics differ — measure, do not assume. |
| Frame budget | The terrain pass is roughly 1000 tiles per frame. On a handheld CPU that may be 10 fps; a smaller view radius and a cached static layer are the obvious answers. |

### The decision this implies today

**Keep the interface layout as data, not as literal coordinates.** Every panel
position currently comes from `Hud`, and the fixed 765×503 assumption is spread
through it. Before the port, `Hud` should read a layout table so a compact
640×480 variant is a second table rather than a second renderer.

Also worth doing early: make the render scale independent of the canvas size
already (it is — the framebuffer is always 765×503 and the window scales), so the
handheld path is "draw at 640×480 instead", not "change every coordinate".

