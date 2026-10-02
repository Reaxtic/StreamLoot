# Changelog

All notable changes to **Stream Loot** are documented here.
This project follows [Semantic Versioning](https://semver.org/).

## [1.1.18] — 2026-10-02

- Twitch and Kick game lists can be resized independently by dragging the handle below each list. Heights are saved on drag completion and restored after restart.

### Informacje o wydaniu

Wersja 1.1.18 zawiera także zmiany z wersji 1.1.12–1.1.17, które nie były wcześniej opublikowane jako wydania GitHub:

- Kampanie Twitch pozostają widoczne i mogą zbierać postęp bez połączonego konta gry. Możliwość odebrania lub dostarczenia nagrody zależy od zasad danej kampanii.
- Wynik odbioru dropu jest ustalany na podstawie odpowiedzi Twitcha, niezależnie od flagi połączenia konta gry. Potwierdzone odbiory są zapisywane lokalnie i zachowywane po odświeżeniu oraz restarcie.
- Wspólne nagrody są rozpoznawane po identyfikatorze korzyści i okresie kampanii, aby nie blokować przyszłych edycji z podobnymi nagrodami.
- Sprawdzanie dostępności korzysta ze wspólnych zapytań dla tej samej gry, ma limity czasu i aktualizuje statusy kampanii stopniowo. Szybkie zmiany ustawień są łączone w jedną ponowną ocenę kopania.
- Białą listę zastąpiły uporządkowane priorytety gier, osobne dla Twitcha i Kicka. Niedostępna gra nie blokuje kolejnych pozycji ani gier zapasowych; aplikacja okresowo sprawdza możliwość powrotu do wyższego priorytetu.
- Zachowano wykluczenia gier i ręczne przypinanie kampanii. Wykluczenia mają pierwszeństwo, a dostępne przypięcia nadpisują automatyczne priorytety gier.
- Dodano wyszukiwanie gier, ukrywanie wykluczonych oraz strzałki do zmiany kolejności. Poprawiono sortowanie list także po opóźnionej przebudowie przez koparkę.
- Pod listami Twitch i Kick znajdują się uchwyty do niezależnej zmiany wysokości. Rozmiar jest zapisywany po zakończeniu przeciągania i przywracany po restarcie.
- Uruchomienie aplikacji do zasobnika systemowego jest sygnalizowane powiadomieniem.

Pełny opis rozwoju od wersji 1.1.0: [Historia zmian](https://github.com/Reaxtic/StreamLoot/blob/main/docs/HISTORIA_ZMIAN_PL.md).

Paczka Windows x64 jest samodzielna i nie wymaga instalacji .NET. Nie zawiera profili przeglądarki, kont, lokalnych ustawień ani logów użytkownika. Aktualizacja nie zmienia wymagań Twitcha i Kicka dotyczących kwalifikujących się transmisji lub połączenia kont gry.

## [1.1.17] — 2026-10-02

- Assign game priority ranks before inserting rebuilt rows, preventing the delayed miner refresh from reverting the visible list to alphabetical order.

## [1.1.16] — 2026-10-02

- The game priority list refreshes after model updates and scrolls to its first item when priorities change, without requiring a search/filter reset.

## [1.1.15] — 2026-10-02

- Replaced the game allow-list with ordered mining preferences, adjustable with up/down arrows.
- Lower-priority games and unlisted games remain available as fallback when preferred games have no eligible live channels.
- Game exclusions are preserved and always override preferences. Manual campaign pins remain overrides.
- Mining checks for returning higher-priority games every three minutes.

## [1.1.14] — 2026-10-02

- Twitch claim success now follows the server claim status, not the game-account linking flag.
- Server-confirmed claimed drops are saved permanently and displayed as completed after refresh and restart.
- Shared Twitch benefits are recognized within their award window so equivalent campaigns do not mine the same collected reward again.

## [1.1.13] — 2026-10-02

- Availability checks now reuse one Twitch directory request per game, time out safely, and update campaign badges progressively.
- Changing several game exclusions no longer starts overlapping mining re-evaluations or resets visible progress and availability.
- Minimized Windows startup now displays a notification confirming that Stream Loot is running in the system tray.

## [1.1.12] — 2026-09-29

- Twitch campaigns remain visible and mineable when the linked game account is missing; account linking is only required when Twitch requires it for claiming or delivery.

## [1.1.11] — 2026-09-27

Strict Twitch drops-enabled channel verification.

### Fixed
- **A channel streaming the correct game could be watched even without active
  drops** — channel selection, pin resumption, availability badges, health checks,
  and the channel picker now require membership in Twitch's server-side
  `DROPS_ENABLED` directory. A live category match alone is no longer accepted.

## [1.1.10] — 2026-09-27

Finish-progress priority and visible Kick category channels.

### Fixed
- **The miner could start or continue a longer campaign while another reward was
  closer to completion** — partially progressed rewards now take precedence, with
  the fewest real minutes remaining selected first regardless of campaign type.
- **General Kick campaigns only showed “Category drop”** — live channels discovered
  in the category directory are now counted, shown in the card tooltip, and exposed
  in the Dashboard streamer picker.

## [1.1.9] — 2026-09-27

Streamer rotation and Kick category verification fixes.

### Fixed
- **Delta Force could stop at a frozen percentage** — a stalled Twitch channel is
  now quarantined without blacklisting the entire campaign, allowing immediate
  rotation to another eligible streamer.
- **General Kick campaigns such as World of Warcraft: Forever were rejected** —
  the selected channel category is now verified using Kick's public channel API.
  The fragile page element check is retained only as a fallback.

## [1.1.8] — 2026-09-27

All pinned campaigns now have priority over automatic picks.

### Fixed
- **A normal Twitch campaign could be selected while another pinned campaign was
  available** — Stream Loot now walks every queued Twitch pin first. It falls back
  to a non-pinned campaign only after all active pins have been checked and none
  has a live eligible streamer.

## [1.1.7] — 2026-09-27

Pinned campaign fallback fix.

### Fixed
- **An unavailable pinned Twitch campaign could block all available campaigns** —
  selection now distinguishes a stream chosen during the current pass from the
  stale stream watched before the pin changed. Pins with no live streamer are
  suspended, an available fallback is mined, and the pin is resumed when one of
  its streamers comes back online.

## [1.1.6] — 2026-09-27

Pinned Twitch campaign selection reliability.

### Fixed
- **A pinned Twitch campaign could be ignored for several minutes** — when Twitch's
  lazily loaded player page does not expose its category link, Stream Loot now
  verifies the channel through Twitch GQL before rejecting it. A valid pinned
  channel starts immediately instead of waiting for a lucky later refresh.
- **Restart expectations are now diagnosable** — shutdown caused by a Windows
  session ending remains recorded separately from an application crash. Enabling
  “Start with Windows” restores mining automatically after the next sign-in.

## [1.1.5] — 2026-09-25

Twitch campaign loading and fallback authentication fixes, built from the stable
1.1.3 release.

### Fixed
- **Twitch campaigns sometimes disappeared after a restart** — the WebView fallback
  now waits until the matching dashboard response has fully downloaded before it
  reads the body, instead of intermittently failing too early.
- **A rejected browser integrity token stopped all Twitch mining** — Stream Loot
  retries safely and can fall back to Twitch's Smart TV device authorization. The
  one-time access token is encrypted for the current Windows user and never stored
  in the repository or logs.
- **False completed/account-link state caused by an empty dashboard response** —
  integrity-error responses are no longer treated as valid campaign data.

## [1.1.4] — 2026-09-25

Twitch progress accuracy and safer recovery.

### Fixed
- **Pinned Twitch drops no longer stop at 98%** — Inventory progress now comes
  only from Twitch's server. A successful watch heartbeat no longer adds a local
  minute that Twitch may not have credited, so a pinned campaign stays active
  until Twitch confirms the full requirement.
- **Accurate Twitch percentages** — partial progress uses the same whole-percent
  calculation as Twitch (`59/60` is shown as 98%, not 100%).
- **No false account-link warning** — a failed or pending claim is shown as
  waiting for collection. Stream Loot no longer assumes the game account is
  disconnected.
- **Failed claims correct stale progress immediately** — after Twitch rejects a
  claim, all Twitch rewards are refreshed even if the miner has already moved to
  another campaign.
- **Watchdog restart cannot close the only working instance** — the old process
  exits only after the replacement window confirms it is ready. Failed restarts
  leave the current process running.
- **Exit diagnostics** — logs now record intentional shutdown reasons and detect
  a previous run that ended without a clean exit.

## [1.1.3] — 2026-09-17

Kick claim and continuation reliability fixes.

### Fixed
- **Intermittent Kick claim failures** — claim requests now use uniquely correlated
  WebView2 messages, so a simultaneous progress or channel-status response can no
  longer be mistaken for the claim result. Empty/non-JSON responses are handled
  safely, and an idempotent "already claimed" response correctly unlocks the next
  reward.
- **Kick stopped after a completed drop** — when one participating channel stops
  crediting, the miner rotates to another live channel instead of blacklisting the
  whole campaign and going idle before later rewards can progress.

## [1.1.2] — 2026-07-17

Two Kick fixes that together stopped it from crediting at all.

### Fixed
- **Kick stopped crediting due to a re-selection loop** — the health check judged
  channel-bound campaigns (Football Drop, Stake, …) by stream *category*, which
  those campaigns don't require and the selection path rightly ignores. It kept
  declaring a perfectly good stream "wrong category" and forced a full
  re-selection every 30 seconds, re-navigating the player so watch time never
  accumulated.
- **A pin with an offline streamer blocked the pins behind it** — only the
  queue-front pin per platform was considered, so an offline pin at #1 kept a
  LIVE pin at #3 from being mined and the app fell back to automatic picks
  instead of your choice. Selection now walks the queue and mines the first pin
  that actually has a live channel; pins are only suspended when *no* pinned
  campaign has a live streamer.

## [1.1.1] — 2026-07-15

Stability release: the app now survives sleep, network drops and wedged UI
threads on its own, and stops abandoning drops that are one minute from done.

### Never gets stuck again
- **Hard-restart watchdog** — if the engine goes silent for 12 minutes (a wedged
  UI thread, a hung network call), the app relaunches itself. The old watchdog
  tried to recover through the UI thread — useless when that thread is the thing
  that's stuck. You stay signed in across the restart.
- **Sleep/resume aware** — the engine is paused before the machine sleeps (so no
  request hangs on a vanishing connection, which used to freeze the app until a
  manual restart) and forces a clean reload ~8s after resume.
- **The watchdog can no longer disable itself** — a stuck "paused"/"suspending"
  flag previously switched recovery off permanently; the hard-restart path now
  ignores those flags, because a pause that outlasts the threshold *is* the hang.

### Mining correctness
- **Doesn't abandon a drop at 99%** — a finished-but-unclaimed reward pauses
  server progress until the claim lands; that pause is no longer mistaken for
  "not crediting" (which used to drop the campaign one minute before the reward).
- **Finish-line priority** — a campaign within 30 minutes of its next drop is
  mined first, instead of losing to a freshly started one.
- **Per-platform pins** — the first pinned Twitch campaign and the first pinned
  Kick campaign are now mined *simultaneously*. Previously only queue position #1
  counted, so a Twitch pin behind two Kick pins was treated as unpinned — and got
  blacklisted outright when its channel stalled.
- **Queued pins are never blacklisted** — any campaign in the queue rotates
  channels instead of being excluded from mining.
- **Pins survive fetch hiccups** — a pin is only dropped from the queue on
  positive evidence (campaign present and finished), not when it's briefly
  missing from a failed fetch.

## [1.1.0] — 2026-07-03

A big functionality-and-polish release: self-healing, a working auto-updater,
statistics, a pin queue, and a friendlier UI.

### Reliability
- **WebView2 crash recovery** — a GPU-driver crash used to leave the app
  silently hung until a manual restart; the embedded browser now reloads a dead
  renderer in place and re-initializes itself after a browser/GPU process crash.
- **Engine watchdog** — if the mining engine produces no heartbeat for 10+
  minutes, the loop restarts automatically.
- **Working auto-updater** — updates now download the .zip asset of the latest
  GitHub Release and apply themselves via a swap script (the old updater pointed
  at a repo folder that never existed in git).
- **Software rendering option** (Settings → Advanced) — run WebView2 with GPU
  acceleration disabled on machines with unstable graphics drivers.

### New features
- **Statistics page** — watched minutes (today / 7 days / total) and a persisted
  history of every claimed drop.
- **Pin queue** — pin several campaigns; they are mined in order (#1 first) and
  the next one is promoted automatically when the previous finishes. The badge
  shows the queue position.
- **Drop ETA** — Inventory cards show the estimated watch time to the next drop.
- **All-done actions** — a notification when every campaign is mined and
  claimed, with an optional "put the computer to sleep" (great for overnight
  mining).
- **Tray status** — hovering the tray icon shows what is being mined and at
  what percentage.
- **"NOT CREDITING" badge** — campaigns whose server progress is frozen are
  flagged in the Inventory.

### Polish & UX
- **Smooth progress bars** — values glide instead of jumping, including server
  corrections.
- **Polish language option** (Settings → Advanced) for statuses and key labels.
- **First-run guide** — a short welcome that walks through logins and the
  game-account links (the #1 cause of "earned but cannot claim").

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

[1.1.4]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.1.4
[1.1.3]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.1.3
[1.1.2]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.1.2
[1.1.1]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.1.1
[1.1.0]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.1.0
[1.0.3]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.0.3
[1.0.2]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.0.2
[1.0.1]: https://github.com/Reaxtic/StreamLoot/releases/tag/v1.0.1
