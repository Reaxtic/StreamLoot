# Changelog

All notable changes to **Stream Loot** are documented here.
This project follows [Semantic Versioning](https://semver.org/).

## [1.0.3] — 2026-07-03

Focus: a calm dashboard (background re-evaluations), a smart pin lifecycle, and
never abandoning an earned drop.

### Quiet re-evaluations
- **No more card blinking** — re-evaluations run in the background; the previous
  selection stays visible (and watched) until the outcome actually differs. Cards
  are cleared only when a platform genuinely ends with nothing to watch.
- **Keep-current actually works again** — the upfront reset used to null the
  current channel, silently disabling the keep-current fast-path, so every
  re-evaluation reloaded the stream (~10s of lost watching each time). Fixed for
  Twitch, and Kick now also skips the reload when the same campaign + channel is
  re-selected.
- **Stall triggers throttled** — when the only live channel of a campaign isn't
  crediting, the re-selection retries once per 5 minutes instead of every 30s.
- Internal checks no longer flip the status to "Evaluating".

### Pin (Mine this) lifecycle
- **Offline pin falls back instead of idling** — when the pinned campaign has no
  live streamers, the app temporarily mines the best other campaign and polls
  cheaply (~3 min) until a pinned channel goes live, then returns to the pin.
- **Auto-unpin only on hard evidence** — the pin clears when every reward is
  claimed (or the campaign ends), NOT when the local counter merely shows 100%.
  Previously a local/server desync (local 120/120 vs server 117/120) unpinned the
  campaign and abandoned the drop at 98%.
- **Desync self-heal** — a failed claim forces an immediate server reconcile, and
  a pin-drift check returns mining to the pinned campaign so the remaining
  minutes get watched and the drop actually claimed.

### Claims & filters
- **"READY — connect account to claim" badge** — fully watched but unclaimed
  rewards (game account not linked) are flagged in the Inventory, and the miner
  moves on instead of parking on a campaign with nothing left to watch.
- **Failed claims retry every 10 minutes** (was: at the hourly refresh) — after
  linking the game account the drop is collected within minutes.
- **Inventory survives fetch failures** — a platform whose campaign fetch fails
  keeps its previously loaded campaigns instead of blanking out.

### Polling
- **Gentler on Twitch** — the live/category eligibility probe is throttled to
  ~2 min (was every 30s), reducing GQL traffic ~4×.

## [1.0.2] — 2026-06-23

Focus: surviving Twitch's tightened drops-dashboard integrity checks, and never
wasting time on dead campaigns.

### Fixed — Twitch integrity / loading
- **Retry on integrity failure** — when Twitch rejects the drops-dashboard query
  ("failed integrity check"), the Twitch campaign load now retries with a short
  backoff (90s → 3m → 6m) instead of leaving Twitch empty until the next hourly
  refresh.
- **Native WebView dashboard fallback** — if token-replay keeps failing integrity,
  the app reads the dashboard the way the real site does: it navigates the (paused)
  WebView to `/drops/campaigns` and captures the browser's own post-challenge
  response. The browser solves the Kasada challenge natively, so this works where
  the replay can't. Scoped to the campaign-list load so it never disrupts a
  watched stream.

### Fixed — stale campaigns
- **Never mines ended campaigns** — campaigns outside their active window
  (start…end) are skipped at selection time, so a cached list that went stale
  (app left running for days across a PC sleep / fetch outage) can't keep mining a
  campaign whose drops are no longer available.
- **Auto-reload after sleep** — when the cached list is detected stale (contains
  ended campaigns), it's reloaded automatically so finished campaigns drop off and
  fresh ones come in.

### Fixed — updater
- **No more bogus auto-update loop** — the version check now matches the app
  version and points at the correct branch, so the updater stops trying (and
  failing) to "update" to a non-existent build.

## [1.0.1] — 2026-06-16

Major reliability overhaul for drop mining, plus a new channel picker.
Rebrand of "Stream Drop Collector" → **Stream Loot** (MIT fork; original author: Marcus Jensen).

### Added
- **Live channel picker** — per-campaign streamer list with online/offline status and
  **viewer counts** (Twitch & Kick). Click to choose a channel.
- **Pin / Unpin a campaign** (📌 "Mine this") — pin one campaign so the app mines only it
  and remembers the choice across restarts. Click again to unpin.
- **Inventory filters** — "Show only available" (has live streamers) and
  "Hide claimed" (drops already earned).
- **Campaign availability indicator** in Inventory, plus a refresh (⟳) button.

### Fixed — mining
- **Channel rotation on stall** — if the watched channel stops crediting on the server
  (e.g. a 24/7 rerun, or a channel that ended its drops), it is dropped and the app moves to
  **another live streamer of the same campaign**. Works for **pinned** campaigns too
  (a pin fixes the campaign, not the channel). No more getting stuck on a dead remembered channel.
- **Auto-skip non-crediting campaigns** (non-pinned) — when server progress is frozen, the
  campaign is deprioritised and the app moves to one that actually credits. Retried hourly.
- **No more fake progress** — the local counter only advances while the stream is genuinely
  online; progress is reconciled to the real server value every ~3 minutes.
- **Kick card shows "Waiting — no live channel"** instead of a stale campaign ticking up a
  fake percentage when no streamer is live.

### Fixed — stability
- **Per-platform resets** — a Twitch issue no longer resets Kick, and vice versa.
- **Self-healing** after PC sleep and network loss — mining resumes automatically.
- **Login & campaign loading** refined (Twitch and Kick independently); login detected via
  session cookies.
- Full campaign-list refresh runs less often (every 60 min) — fewer visible blips, since
  progress is reconciled separately every 3 min.

### Removed
- Redundant manual controls (Set / Switch / Check) — the channel picker replaces them.

### Credits
- Drop-crediting approach and channel-picker UX inspired by
  [TwitchDropsMiner by DevilXD](https://github.com/DevilXD/TwitchDropsMiner) (MIT).
  No source code was copied; both projects are MIT-licensed.

[1.0.3]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.0.3
[1.0.2]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.0.2
[1.0.1]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.0.1
