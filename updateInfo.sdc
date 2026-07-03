{
  "version": "1.1.0",
  "type": "Feature",
  "changelog": "Self-healing after GPU/WebView2 crashes + engine watchdog, working auto-updater (GitHub Releases), Statistics page with claim history, drop ETA on campaigns, pin QUEUE (mine favourites in order), smooth animated progress bars, all-done notification with optional PC sleep, tray status tooltip, Polish language option, and a first-run guide.",
  "historic_versions": [
    {
      "version": "1.0.3",
      "type": "Feature",
      "changelog": "Quiet background re-evaluations (no more card blinking or stream reloads), smart pin lifecycle (suspends when streamers are offline, returns when live, never abandons an unclaimed drop on a progress desync), ready-to-claim badge for unlinked game accounts, faster claim retries, and gentler Twitch polling."
    },
    {
      "version": "1.0.2",
      "type": "Feature",
      "changelog": "Twitch integrity resilience: retry-with-backoff + native WebView dashboard fallback so campaigns load even when token-replay hits Twitch's integrity wall. Never mines ended campaigns, and auto-reloads a stale list after PC sleep. Fixed a bogus auto-update loop."
    },
    {
      "version": "1.0.1",
      "type": "Release",
      "changelog": "Stream Loot rebrand: live channel picker, channel rotation when a stream stops crediting, transient integrity-failure retry/self-healing, per-platform resets, and Inventory filters."
    },
    {
      "version": "1.0.17",
      "type": "Patch",
      "changelog": "Fixed an issue with Kick deserialization of JSON data & an several minor issues related to progress tracking & automatic claiming of rewards."
    },
    {
      "version": "1.0.16",
      "type": "Patch",
      "changelog": "Fixed startup settings-save race that could overwrite whitelist values, kept inactive whitelisted slugs visible in settings, and removed inactive placeholders immediately when unchecked or cleared."
    },
    {
      "version": "1.0.15",
      "type": "Patch",
      "changelog": "Added persistent GQL hash caching with retry-aware fallback, immediate claimed-badge inventory updates after auto-claim, remembered streamer selection, and verbose-gated debug/cache logging."
    },
    {
      "version": "1.0.14",
      "type": "Patch",
      "changelog": "Improved WebView waiting reliability and dispatcher async handling."
    },
    {
      "version": "1.0.13",
      "type": "Patch",
      "changelog": "Added verbose debug toggle in Settings, improved Twitch/Kick selection diagnostics, and fixed reward progress percentage tracking while keeping campaign progress tracking intact."
    },
    {
      "version": "1.0.12",
      "type": "Patch",
      "changelog": "Added comprehensive diagnostic logging and a new 'Open Logs Folder' option in Settings"
    },
    {
      "version": "1.0.11",
      "type": "Patch",
      "changelog": "Added comprehensive diagnostic logging and a new 'Open Logs Folder' option in Settings"
    },
    {
      "version": "1.0.10",
      "type": "Patch",
      "changelog": "Fixed WebView contention during drops refresh and stream watching"
    },
    {
      "version": "1.0.9",
      "type": "Patch",
      "changelog": "Fixed a minor issue where start minimized didn't take effect"
    },
    {
      "version": "1.0.8",
      "type": "Patch",
      "changelog": "Highlight current campaign/reward with \"WATCHING\" badges"
    },
    {
      "version": "1.0.7",
      "type": "Patch",
      "changelog": "Improved stream selection and monitoring to verify that the watched Twitch or Kick stream matches the required game/category for each drops campaign"
    },
    {
      "version": "1.0.6",
      "type": "Patch",
      "changelog": "Fixed a bug in prioritizing streamers to watch before general drops"
    },
    {
      "version": "1.0.5",
      "type": "Patch",
      "changelog": "fixed counting error (%) and bug where after claiming all campaigns it would take an hour to idle again.."
    },
    {
      "version": "1.0.4",
      "type": "Patch",
      "changelog": "Fixed a few minor issues with claim status for twitch rewards."
    },
    {
      "version": "1.0.3",
      "type": "Patch",
      "changelog": "Added drop progress to the dashboard."
    },
    {
      "version": "1.0.2",
      "type": "Patch",
      "changelog": "Github Directory Downloader module updated."
    },
    {
      "version": "1.0.1",
      "type": "Bugfix",
      "changelog": "Added Kick bearer token for claim request."
    },
    {
      "version": "1.0.0",
      "type": "Release",
      "changelog": "Initial Release."
    }
  ]
}