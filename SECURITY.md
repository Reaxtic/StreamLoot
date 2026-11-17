# Privacy and safe reporting

Stream Loot uses local WebView2 browser sessions to communicate with Twitch and Kick. Settings, logs and other local state are stored under `%APPDATA%\Stream Loot`; WebView2 profiles may also exist beside the app or under its configured data directory. Updates contact GitHub. Embedded platform pages load their own service and media domains.

## Never upload these files

- WebView2 browser profiles, cookies, browser storage or saved sessions.
- Saved token files such as `TwitchDeviceToken.bin`.
- Unredacted request headers, authorization codes or URLs containing credentials.
- Your complete app data directory or diagnostic logs without reviewing them first.

A release ZIP should contain build output only, never an existing user's installation directory or profile. Public screenshots should use illustrative data or be reviewed for private information.

## Reporting a problem

For ordinary bugs, use [Issues](https://github.com/Reaxtic/StreamLoot/issues/new/choose). Include the version, platform and steps to reproduce. Remove personal data, tokens and session identifiers from attachments.

If you find an exposed secret, do not quote it in an issue. Revoke or rotate it through the affected service. If GitHub's private vulnerability reporting is available for this repository, use **Security → Report a vulnerability** for a sensitive report; otherwise open only a non-sensitive request for a private reporting channel. Do not publish exploit details or credentials while arranging disclosure.

The MIT License does not guarantee security, successful rewards or compliance with Twitch/Kick campaign rules.
