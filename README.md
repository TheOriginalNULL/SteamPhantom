<div align="center">

# SteamPhantom

**Idle your Steam hours · Unlock achievements · Auto-farm trading cards.**
Everything you'd open three different tools for, in one app.

[![Discord](https://img.shields.io/badge/Discord-3x3-5865F2?logo=discord&logoColor=white)](https://discord.gg/3x3)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Status](https://img.shields.io/badge/Status-Active-brightgreen)

*brought to you by **~NULL** · discord.gg/3x3*

</div>

---

## What it does

SteamPhantom is a single Windows desktop app that combines three things power users
have historically needed three different tools for:

- **Idle hours** — Tell Steam you're "in-game" for any title you own, without
  actually launching it. The game shows up in your friends list, racks up
  playtime, and earns trading card drops, all while using ~0% CPU and a few
  MB of RAM (it's just the Steamworks heartbeat loop, no game code).
- **Unlock / edit achievements** — Like SAM (Steam Achievement Manager): pick
  a game, toggle achievements, save. Optional drip-feed mode spreads unlocks
  over time so your profile doesn't pop 200 achievements in four seconds.
- **Auto-farm trading cards** — Pick N games at random from your library
  (excluding anything you're already idling), rotate the active set on a
  schedule, scrape `steamcommunity.com/gamecards/<appid>` every couple of
  minutes to track drop progress live, and stop when each set is done.

Plus a **stats editor** (the other half of SAM, for games that expose
modifiable int/float stats) and a proper **Library** view with header art,
search, sort, and multi-select.

> [!IMPORTANT]
> SteamPhantom is for **your own** Steam account, on your own machine.
> See **[Is it safe?](#is-it-safe)** below for the security model and the
> honest take on Steam's Terms of Service.

---

## Screenshots

> Drop your screenshots in `docs/screenshots/` and link them here. Suggested:
> Library page, Idle tab, Cards tab with progress, Achievements window, Settings.

| Library | Idle | Cards |
|---------|------|-------|
| ![library](docs/screenshots/library.png) | ![idle](docs/screenshots/idle.png) | ![cards](docs/screenshots/cards.png) |

| Achievements | Stats | Settings |
|---|---|---|
| ![ach](docs/screenshots/achievements.png) | ![stats](docs/screenshots/stats.png) | ![set](docs/screenshots/settings.png) |

---

## Download

Grab the latest from the [Releases page](https://github.com/TheOriginalNULL/SteamPhantom/releases).
The release is a zip with **two files**:

```
SteamPhantom.exe         ← main app (self-contained, ~72 MB)
SteamPhantom.Worker.exe  ← per-game idle worker (steam_api64.dll embedded inside, ~34 MB)
```

Extract anywhere on your machine. No installer, no .NET runtime required, no
admin rights needed. The worker exe must live next to the main exe — the main
app spawns it by filename.

**Requirements**
- Windows 10 22H2+ or Windows 11
- Steam client installed and signed in (the worker piggybacks on its session)
- Either a free Steam Web API key **or** willingness to sign in via SteamKit2
  inside the app — both paths are supported, see below

---

## First-time setup

Open the app once. The Library tab is shown but it can't load your games
until it knows who you are. There are two paths:

### Option A — Web API key (recommended)

A Web API key is **read-only**: it can list your games but can't act on your
account. You don't type a password, no session token sits on disk, and you
can revoke it from Steam at any time. The only cost is ~30 seconds of setup.

1. Visit [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey)
   while signed into Steam in your browser.
2. Domain name: anything, e.g. `localhost`. Check the box, hit Register.
3. Copy the 32-character hex string.
4. SteamPhantom → **Settings** → paste into **Steam Web API Key** → **Save changes**.
5. SteamPhantom → **Library** → **Refresh**.

### Option B — Sign in with credentials

If you'd rather not deal with the key, the app can speak Steam's internal
protocol directly via SteamKit2 (the same library ArchiSteamFarm uses).

1. **Library** → **Sign in to Steam**
2. Enter your Steam username + password.
3. If Steam Guard is active, enter the code from your phone / email when
   prompted.
4. The window closes once the handshake succeeds.

Your password is sent **directly to Steam's servers** by SteamKit2 and is
**never stored**. Only the refresh token Steam hands back is saved, encrypted
with Windows DPAPI (see [Is it safe?](#is-it-safe)).

> Either path works on its own. Pick one. You can switch later in Settings.

---

## Usage

### Idle a game

- Library → hover any game card → **Start idling**.
- Steam now shows you as "Playing &lt;Game&gt;". Friends see it. Playtime accumulates.
- Idle tab shows active sessions with live elapsed timers.
- Click **Stop** on any session, or **Stop all idling** from the tray.
- Closing the app stops all idle sessions and **saves the list** — they're
  restarted automatically the next time you open the app (unless you stopped
  them manually first).

### Idle multiple games at once

- Hover cards → click the checkbox in the top-right corner of each → action
  bar appears at the bottom → **Start idling**. Steam allows up to **32**
  concurrent "in-game" apps per account.

### Auto-farm trading cards

- Cards tab → **Start auto-farming** when the queue is empty. Picks N random
  games from your library (default 30, configurable), skips anything already
  in the Idle tab, starts rotating.
- The rotation runs `Concurrent` games at a time, swaps them out every
  `Rotate min` minutes, and re-queries `steamcommunity.com/gamecards/<appid>`
  every ~2 minutes to track drops. A banner between Active and Queue shows
  total drops earned this session and total remaining across the queue.
- **Refill queue** appends more random eligible games when the queue runs low.
- Done games (no drops remaining) move into a "Done" section and stop being
  cycled.

### Unlock / clear an achievement

- Library → hover a game → **Achievements** opens a window.
- Toggle checkboxes. Dirty rows are outlined violet.
- **Drip-feed unlocks** (off by default): when on, applies one change at a
  time with a random 0.5–2.5s gap so popups don't pile up. When off, applies
  everything in one batch (SAM-fast).
- **Apply changes** saves. The banner reports what unlocked and what cleared.
- **Sort** + **Filter pills**: All / Unlocked / Locked / Hidden / Pending.
- **Unlock all** / **Lock all** operates on the **current filtered view**,
  not the whole list.

### Edit stats

- Same window, switch to the **Stats** tab. Loads automatically when the
  game's stats schema is cached locally by Steam (i.e. you've launched the
  game at least once on this PC). Type new values, hit **Apply changes**.

---

## Is it safe?

Short answer: **yes, with one footnote about Steam's ToS**.

### What runs locally vs over the network

Everything except two outbound HTTPS calls runs on your machine:

| Calls out                                       | Why                                       | Who hears it |
|-------------------------------------------------|-------------------------------------------|--------------|
| `api.steampowered.com` (Web API key path)       | Read your owned games                     | Steam        |
| `steamcommunity.com/profiles/.../gamecards/...` | Scrape card-drops-remaining count         | Steam        |
| SteamKit2 CM servers (SteamKit2 path)           | Real Steam-client protocol over TLS       | Steam        |
| `raw.githubusercontent.com/.../Version.txt`     | Update check (a few bytes, once on launch)| GitHub       |

No analytics, no telemetry, no crash reporters. The app does not phone home.
Read the source, the only `HttpClient` usage is in `Services/` and is exactly
the four above.

### Where credentials live on disk

```
%APPDATA%\SteamPhantom\
├── settings.json   ← Steam64 ID, Web API key, theme, etc. Plain text, user-readable.
└── auth.bin        ← SteamKit2 refresh token. DPAPI-encrypted.
```

- `settings.json` lives in your roaming AppData folder, **only readable by
  your Windows user account**. If the Web API key worries you, don't use the
  Web API path — the SteamKit2 sign-in stores nothing in this file.
- `auth.bin` is encrypted with **Windows Data Protection API** scoped to
  the *current Windows user on the current machine*. Copying the file to
  another PC or another Windows account won't decrypt — DPAPI uses the
  user's master key tied to their login. If your machine is compromised at
  the OS level all bets are off anyway.

### What about my Steam password?

The credentials sign-in path uses SteamKit2's `BeginAuthSessionViaCredentialsAsync`,
the same call the official Steam mobile app uses. Your password goes from your
keyboard to the in-memory `PasswordBox`, into the SteamKit handshake, to
Steam's auth servers over TLS — and is **never written to disk and never
sent anywhere else**. The only thing persisted is the long-lived **refresh
token** Steam issues, encrypted as above.

### Will this get me VAC banned?

**No.** VAC bans are exclusively for cheating in multiplayer games. They are
issued by the game's anti-cheat module when it detects memory tampering /
process injection / signature matches. SteamPhantom doesn't touch any game
process, doesn't inject anything, doesn't hook anything. The "in-game"
status that Steam shows comes from `SteamAPI_Init`, the **official entry
point Steamworks games themselves call**, not a workaround.

### Does this violate Steam's Terms of Service?

Strictly read, **yes** — section about not interfering with Steam services
covers idle-as-you're-not-actually-playing and unlocking-achievements-out-of-band.
Practically:

- **Idle Master** and **ArchiSteamFarm** have done idling on top of Steamworks
  for **over a decade**. Steam tolerates it. Accounts get restricted only in
  extreme cases like running thousands of accounts to flip cards for profit
  on the Market.
- **SAM (Steam Achievement Manager)** has done achievement unlocking openly
  for over a decade. Same — tolerated, ban-rare.
- **Use the drip-feed mode** when unlocking large batches; it's not just for
  aesthetics, it makes your activity feed look human rather than scripted.

Treat your account the way you'd treat any other valuable account: don't
do things at obvious-bot scale, don't run this on accounts you can't afford
to lose access to, and you'll be fine.

---

## How it works

The interesting part. Why two exes?

Steam's native SDK (`steam_api64.dll`) binds a **whole process** to **one
app id**, read once at startup from a `steam_appid.txt` file in the working
directory. There's no API to switch app ids mid-flight. So to run multiple
"in-game" sessions simultaneously, or to read/write any game's achievements
without contaminating the main app, SteamPhantom spawns a **separate
worker process per active game**:

```
┌─────────────────────────────────┐
│  SteamPhantom.exe (WPF, UI)     │
│  - Library, Idle, Cards, etc.   │
│  - Catalog read (API/SteamKit2) │
│  - Spawns workers as needed     │
└──────────┬──────────────────────┘
           │ stdin/stdout JSON
           │
   ┌───────┴────────┐        ┌────────────────┐
   │ Worker (730)   │        │ Worker (440)   │
   │ steam_appid:730│        │ steam_appid:440│
   │ SteamAPI_Init  │        │ SteamAPI_Init  │
   │ RunCallbacks…  │        │ RunCallbacks…  │
   └────────────────┘        └────────────────┘
```

The worker is a tiny ~34 MB self-contained console exe that:

1. Writes `steam_appid.txt` into its own working directory.
2. Calls `SteamAPI.Init()` — at this point Steam sees you as playing that game.
3. Reads JSON commands from stdin in a loop:
   - `idle` — no-op (already running callbacks in the background)
   - `list-achievements` — `RequestCurrentStats`, await `UserStatsReceived_t`, enumerate
   - `set` / `clear` / `store` — `SteamUserStats.SetAchievement` etc.
   - `apply-batch` — set+clear+store in one IPC round trip (the SAM-fast path)
   - `get-stats` / `apply-stats-batch` — same for `SteamUserStats.GetStat`/`SetStat`
   - `quit` — `SteamAPI.Shutdown()`, exit
4. Pumps `SteamAPI.RunCallbacks()` on a background thread the whole time.
5. Exits when stdin closes (parent app died), so workers can't outlive the UI.

The main app handles:

- **WPF UI** for everything the user sees.
- **Catalog reads** through `Services/SteamGameCatalog.cs` — prefers
  SteamKit2's `IPlayerService.GetOwnedGames` over the unified messages
  protocol when signed in, falls back to Web API otherwise.
- **Local schema parsing** for the Stats tab — Steam caches game schemas at
  `Steam\appcache\stats\UserGameStatsSchema_<appid>.bin` (binary VDF), which
  the app reads via `ValveKeyValue` to discover stat names without a
  publisher API key.
- **DPAPI auth store** at `%APPDATA%\SteamPhantom\auth.bin`.
- **System tray** via `H.NotifyIcon.Wpf` so closing the window doesn't
  kill your idle sessions.

---

## Tech stack

| | |
|---|---|
| UI | [WPF](https://learn.microsoft.com/dotnet/desktop/wpf/) on .NET 8, custom dark chrome with [Win11 native rounded corners](https://learn.microsoft.com/windows/win32/api/dwmapi/) |
| MVVM | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (source generators for `[ObservableProperty]` / `[RelayCommand]`) |
| Steam (writes) | [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) by Riley Labrecque (the official C# wrapper) |
| Steam (reads, alt path) | [SteamKit2](https://github.com/SteamRE/SteamKit) by the SteamRE team |
| Stats schema | [ValveKeyValue](https://github.com/SteamDatabase/ValveKeyValue) by SteamDatabase |
| Tray icon | [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) |
| Auth-token persistence | Windows DPAPI via `System.Security.Cryptography.ProtectedData` |
| Packaging | `PublishSingleFile` + `SelfContained` |

Massive credit to all of the above. SteamPhantom would not exist without them.

---

## Building from source

You need:

- Windows 10 22H2+ or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

```cmd
git clone https://github.com/TheOriginalNULL/SteamPhantom.git
cd SteamPhantom

REM Run from source (Debug build, framework-dependent worker)
dotnet run --project SteamPhantom

REM Or produce a single-file release:
publish.cmd
```

The release bundle lands at
`SteamPhantom\bin\Release\net8.0-windows\win-x64\publish\`.
Zip the two `.exe` files and ship.

### Project structure

```
SteamPhantom/                       solution root
├── publish.cmd                     publishes worker + main as single-file exes
├── README.md                       you are here
│
├── SteamPhantom/                   main WPF app
│   ├── Resources/                  app icon (icon.png / optional icon.ico)
│   ├── Models/                     OwnedGame, IdleSession, AchievementInfo, …
│   ├── Services/                   SteamGameCatalog, IdleManager, CardFarmManager,
│   │                               AchievementManager, SteamKitClient,
│   │                               SteamSchemaReader, CardDropChecker,
│   │                               TrayIconHost, WindowsStartup, UpdateChecker, …
│   ├── ViewModels/                 ShellViewModel, LibraryViewModel,
│   │                               AchievementsWindowViewModel, …
│   ├── Views/                      Library/Idle/Cards/Settings UserControls,
│   │                               AchievementsWindow, LoginWindow
│   ├── App.xaml + App.xaml.cs      composition root, persistence, silent re-login
│   ├── MainWindow.xaml + .cs       custom chrome, title bar, sidebar
│   └── AppLinks.cs                 external URLs (Discord, update manifest, …)
│
└── SteamPhantom.Worker/            tiny per-game console exe
    ├── Program.cs                  JSON command loop on top of Steamworks.NET
    └── steam_api64.dll             native Steamworks lib (Valve-redistributable)
```

---

## What I learned

This project started as "I want SAM and Idle Master in one window" and ended
up being **the deepest dive I've ever taken into Steam's internals**. A few
highlights of what fell out of the rabbit hole:

- **The Steamworks SDK is per-process, per-app id.** Once you grok that
  `steam_appid.txt` is read on `SteamAPI_Init` and never re-read, the whole
  shape of every multi-game Steam tool (Idle Master, SAM, ASF) suddenly
  makes sense — they all spawn child processes because there is no other
  way. SteamPhantom does the same with a JSON line-based stdin/stdout
  protocol so the parent UI can drive any number of workers concurrently.

- **SteamKit2 is reverse-engineered Steam.** The CM (Connection Manager)
  binary protocol that the official desktop client uses to talk to Steam —
  for login, owned games, badge progress, all of it — has been ported to
  C# by the SteamRE community. Getting it working in this project meant
  learning how `BeginAuthSessionViaCredentialsAsync` actually flows through
  multiple platform-typed token scopes (the AccessDenied debugging session
  was a good one — the fix was a single `PlatformType = SteamClient` field).

- **VDF binary KeyValues is everywhere.** Steam stores per-game stats and
  achievement schemas locally in `appcache/stats/UserGameStatsSchema_<appid>.bin`
  using Valve's binary KeyValues format. Reading those (via the
  `ValveKeyValue` library) gives you SAM-style stat enumeration without
  needing a publisher API key, which is otherwise paywalled. Learning the
  on-disk layout was a lesson in just how much *interesting state* Steam
  caches locally that nobody documents.

- **The Web API and the Community endpoints aren't the same.** The
  `gamecards/<appid>` HTML page is technically a community endpoint with
  no documented API, but its "X card drops remaining" string is the only
  source of card-drop progress accessible without a publisher key. The
  card-farm progress tracker scrapes it because Steam doesn't expose a
  proper API for it.

- **WPF still has teeth in 2026** if you respect it. Custom title bar with
  `WindowChrome` + DWM attributes for native Win11 rounded corners, source-
  generated MVVM via CommunityToolkit, system tray via H.NotifyIcon —
  none of it required ceremony, all of it feels modern, and the binary is
  smaller than an Electron equivalent by an order of magnitude.

- **Single-file publishing in .NET 8 is real and good**, but `PublishSingleFile`
  doesn't compose with project-reference build chains. Getting two
  separately-published exes (main + worker) into one publish folder took a
  pair of MSBuild targets and a publish script that runs them in the right
  order. Modest plumbing for a properly shippable artifact.

- **Steam's tolerance of "gray-area" tooling is a load-bearing design
  choice on their side.** Idle Master, SAM, ArchiSteamFarm have existed
  openly for over a decade. The fact that VAC explicitly *doesn't* fire on
  any of this — because VAC is a multiplayer anti-cheat and these tools
  don't touch game memory — is a clean separation that made building this
  app feel a lot less hostile than I expected going in.

I came out the other side with a real understanding of how Steam works from
the SDK layer down to the binary VDFs and CM protocol, and how Valve's
choices about what's documented vs left to community reverse-engineering
shape the entire ecosystem of third-party Steam tooling. Worth it.

---

## Credits

- **~NULL** — design, code, the whole thing.
- [Riley Labrecque](https://github.com/rlabrecque) for **Steamworks.NET** — the
  reason this is even possible in C#.
- The **[SteamRE](https://github.com/SteamRE) team** for **SteamKit2** and
  **ValveKeyValue** — community-reverse-engineered Steam, generously
  available.
- [HavenDV](https://github.com/HavenDV) for **H.NotifyIcon.Wpf** — the
  actively-maintained fork that makes the tray icon a non-issue.
- **CommunityToolkit.Mvvm** maintainers — source-generated MVVM is a quality
  of life upgrade.

Join the Discord at **[discord.gg/3x3](https://discord.gg/3x3)** for bug
reports, feature requests, or just to say hi.

---

## License

> Pick one and drop it here. MIT is the common choice for a project like
> this; GPL-3.0 if you want copyleft. Without a license file the default is
> "all rights reserved" which probably isn't what you want for an OSS app.

```
Copyright © ~NULL · discord.gg/3x3
```
