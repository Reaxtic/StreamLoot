# Changelog

All notable changes to **Stream Loot** are documented here.
This project follows [Semantic Versioning](https://semver.org/).

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

[1.0.1]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.0.1
