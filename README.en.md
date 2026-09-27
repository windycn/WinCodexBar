# WinCodexBar

**Codex accounts, usage, and creative tools in your Windows tray.**

[简体中文](README.md) · [Download v1.0.0](https://github.com/windycn/WinCodexBar/releases/tag/v1.0.0) · [Changelog](CHANGELOG.md)

WinCodexBar is a WinUI 3 workspace for Windows. The tray panel gives you quick access to accounts and quota; the main window adds token activity, session analysis, galleries, and settings. Image generation, editable SVG conversion, and model quality checks are separate features that start disabled.

## Screenshots

These nine screenshots use demo accounts. No real credentials or private gallery files are included.

| Overview and feature switches | Tray panel |
| --- | --- |
| ![Overview](docs/screenshots/overview.png) | ![Tray panel](docs/screenshots/tray-panel.png) |

| Image studio | Editable SVG |
| --- | --- |
| ![Image studio](docs/screenshots/image-studio.png) | ![Editable SVG](docs/screenshots/editable-svg.png) |

| Token activity | Session analysis |
| --- | --- |
| ![Token activity](docs/screenshots/token-activity.png) | ![Session analysis](docs/screenshots/session-analysis.png) |

| Model quality check | Tray styles |
| --- | --- |
| ![Model quality check](docs/screenshots/quality-check.png) | ![Tray styles](docs/screenshots/tray-styles.png) |

![System integration and update settings](docs/screenshots/system-settings.png)

## Install

Download the x64, x86, or ARM64 ZIP from [Releases](https://github.com/windycn/WinCodexBar/releases), extract it into a writable folder, and run `WinCodexBar.exe`. Do not launch it from inside the ZIP. The launcher installs the bundled, Microsoft-signed Windows App Runtime when needed. Click the tray icon for the quick panel or double-click it for the main window.

Add an account through OpenAI's official authorization page. You can copy the authorization URL to another device, then paste the callback URL into WinCodexBar. **Restart Codex after switching accounts** for the new account to take effect.

Account data and settings live in `%USERPROFILE%\.codexbar`, or under `CODEXBAR_HOME\.codexbar` when that environment variable is set. Account exports contain usable credentials; store them privately and never publish them.

## Features

- **Accounts and quota:** Save multiple Codex accounts; switch or aggregate them; view five-hour and seven-day quota, reset times, account health, and reset-card expiry when available. Low quota and key reset-window events can trigger deduplicated Windows notifications.
- **Tray and desktop:** The default tray style is a circular number. Other icon styles, UI scaling, startup shortcuts, notifications, keep-awake behavior, and black-screen delay are configurable.
- **Image studio:** Enable it from the overview, then select one saved Codex account for each request. It runs in a separate WinUI window, with no web server, local reverse proxy, or account pool. Edit or paste prompts, choose models, aspect ratio, resolution, quality, and reasoning level, and request up to ten images. Upload or paste multiple reference images. A shared queue runs up to six image/SVG jobs at once. Local thumbnail cards preserve prompts, model and time; preview full screen, copy images into other apps, or send an image to SVG conversion. Choices persist between launches. Availability depends on the selected account and upstream service.
- **Editable SVG:** A separate overview switch and window. Convert a newly generated image, one from the gallery, or a locally uploaded/pasted image. Choose the account, text model, reasoning level, and extra instructions. Stronger models can better reproduce complex shapes and details; manual touch-up may still be needed. Results have their own SVG gallery, preview, file-open, and code-copy actions.
- **Token and sessions:** Daily activity, streaks, peak usage, local session counts, model distribution, recent sessions, and estimated cost. Image, SVG, and quality-check usage is tracked too. Estimates are not an OpenAI invoice.
- **Model quality checks:** Select an account, model, reasoning level, and preset or custom prompt. Render the resulting HTML in the app and retain the history. A single run is not proof that a model has degraded, and it uses the selected account's quota.
- **Import and export:** Import full JSON backups, single-account authorization JSON, account arrays, common wrappers, JSON Lines, CSV, and TSV. Nested `tokens` / `credentials` and common field aliases are recognized. Export as full JSON (default, including account settings), generic JSON arrays, CSV, TSV, or JSON Lines. Imports require complete OAuth credentials and validate account identity against the tokens. Exported files contain usable credentials; keep them private.

| Account file | Intended use |
| --- | --- |
| Full JSON | WinCodexBar backup and restore, including the active account and extra account settings |
| Generic JSON | A single account, an array, or records under `accounts`, `items`, `profiles`, `records`, or `data` |
| JSON Lines | One JSON account per line for script processing |
| CSV / TSV | Header-based tables with common column aliases; email and account ID can be read from the tokens when omitted |

Every format needs `access_token`, `refresh_token`, and `id_token`. A file containing only one token cannot restore a complete account.
- **Updates:** Check manually or enable periodic checks and silent updates. Packages are SHA256-verified, with account/settings backup and a full-package fallback when a matching delta is unavailable. Silent installation waits for all windows and creative jobs to be idle. The settings page also links to the GitHub project.

Image and SVG gallery folders can be changed independently. Metadata indexes and trash are kept separately, and incomplete entries can be recovered or cleaned by age.

### Black-screen mode

Black-screen mode covers every display with an opaque black window while the app keeps running. Mouse or keyboard input restores the desktop. The delay defaults to zero seconds and can be set to 5, 15, 30, 60, or a custom number of seconds. Keep-awake is enabled temporarily and returns to its previous setting on exit.

## Build

Windows and the .NET 8 SDK are required.

```powershell
dotnet build windows/CodexBarWin.WinUI/CodexBarWin.WinUI.csproj -c Release -p:Platform=x64
dotnet run --project tests/WinCodexBar.CoreTests -c Release
dotnet run --project tests/GalleryStorageSmoke -c Release
dotnet run --project tests/TrayIconSmoke -c Release
```

The release workflow builds x64, x86, and ARM64 ZIP packages, verifies the Microsoft signature on Windows App Runtime, and publishes `SHA256SUMS.txt`.

## License

[MIT](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
