# Contributing to Stream Loot

Bug reports, documentation fixes and focused pull requests are welcome. Read the [README](README.md) and [privacy guidance](SECURITY.md) before sharing diagnostics.

## Before opening an issue

Search existing issues, check the latest release and compare progress with the platform's own inventory. Include the app version, Windows version, Twitch/Kick platform and reproducible steps. Do not attach tokens, authorization codes, browser profiles or unredacted logs.

## Development checks

On Windows with the .NET 10 SDK:

```powershell
dotnet run --project tests/StreamLoot.RetryTests
dotnet run --project tests/StreamLoot.IntegrationTests
dotnet publish UI/UI.csproj -c Release -r win-x64 --self-contained true
```

Integration tests include WPF and WebView2 behavior; an installed WebView2 Runtime is needed. Tests use isolated profiles rather than real accounts.

Keep pull requests focused, explain behavior changes and add regression coverage where possible. Preserve user settings and privacy. Do not report successful reward crediting solely because a video played or a watch request returned HTTP 204.

## Sharing the project

Use the [promotion kit](docs/PROMOTION.md), disclose your relationship to the project and respect community rules. No spam, bought stars, star exchanges or rewards conditioned on starring the repository.

Contributions are made under the existing [MIT License](LICENSE). Preserve upstream copyright and attribution.
