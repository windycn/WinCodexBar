# WinCodexBar

WinCodexBar is a Windows tray app for Codex users who work with multiple OpenAI OAuth accounts. It helps you switch accounts, inspect 5-hour and 7-day usage, review local token activity, import or export accounts, and keep Windows awake during long coding sessions.

The goal is simple: keep the account and usage workflow close to the system tray, so you can stay in Codex without repeatedly editing config files or guessing which account still has quota.

## What It Solves

When you use several OpenAI accounts with Codex, the friction usually comes from:

- Not knowing how much 5-hour or 7-day quota remains.
- Editing local account configuration by hand.
- Moving or backing up multiple OAuth accounts.
- Windows sleeping or locking the screen during a long session.
- Token activity and local session history being hard to inspect.

WinCodexBar puts these controls into a tray menu, a dashboard, and a settings window designed for day-to-day Windows use.

## Features

### Tray Menu

- Open a compact account panel from the system tray.
- Use a ring tray icon to show recent 5-hour quota status.
- Hover over the tray icon to inspect 5-hour and 7-day usage.
- Open the dashboard from the tray menu.
- Dismiss the tray panel by clicking outside it.

### Account Management

- Add OpenAI OAuth accounts.
- Capture browser OAuth callbacks automatically when possible.
- Paste the returned browser URL manually when automatic capture is unavailable.
- Import and export multiple accounts for backup or migration.
- Delete accounts with a confirmation step.
- Switch the active account and write the change to Codex configuration.

### Usage And Quota

- Show each account's subscription, health state, 5-hour quota, and 7-day quota.
- Choose between used quota and remaining quota display modes.
- Choose token number units: Chinese-style units or K/M/B.
- Use green, orange, and red status colors as remaining quota crosses warning thresholds.
- Show reset countdowns together with exact reset dates and times.

### Manual And Aggregate Modes

- Manual mode writes the selected account to local Codex configuration. Already-running Codex instances usually need to be restarted or reopened to use the new account.
- Aggregate mode starts a local account gateway so new Codex instances can route through a local endpoint.
- Codex instances that were already open before aggregate mode is enabled usually need to be restarted or reopened before they use the local gateway.

### Dashboard

- View account counts, current account, quota state, and health summary.
- Refresh or switch accounts from the account list.
- Inspect token activity with daily, weekly, monthly, and all-time views.
- Use bar charts and a calendar-style heatmap for token trends.
- Review local Codex sessions, including recent sessions and highest-token sessions.
- Estimate token cost in USD and CNY using editable model price presets.

### Keep Awake

- Keep Awake prevents Windows from sleeping or turning off the display.
- Advanced Keep Awake can gently move the mouse after an idle period.
- Configure idle threshold, trigger interval, jitter duration, movement strategy, and fullscreen pause behavior.
- Enable launch at Windows startup.

### Settings

- Account settings: manual mode and aggregate mode.
- Usage settings: quota display mode, token units, auto refresh, health thresholds, and pricing presets.
- Wake strategy: keep awake, advanced anti-sleep behavior, and startup launch.
- Model parameters: default model, review model, reasoning effort, and service tier.

## Install

Choose the package that matches your Windows device:

- `WinCodexBar-0.1.0-win-x64.zip`: most 64-bit Intel / AMD Windows devices.
- `WinCodexBar-0.1.0-win-x86.zip`: older 32-bit Windows devices.
- `WinCodexBar-0.1.0-win-arm64.zip`: Windows on ARM devices.

Extract the archive and run `WinCodexBar.exe`. After launch, the app appears in the system tray.

## Build From Source

Requires the .NET 8 SDK.

```powershell
dotnet restore windows\CodexBarWin\CodexBarWin.csproj
dotnet build windows\CodexBarWin\CodexBarWin.csproj -c Release
```

Publish a self-contained single-file build:

```powershell
dotnet publish windows\CodexBarWin\CodexBarWin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Data And Privacy

- Account data is stored locally by default.
- The app does not display access tokens, refresh tokens, or ID tokens in logs, UI text, or summaries.
- Exported account files are sensitive. Keep them private and do not upload them publicly.
- Token activity and session analysis are based on local Codex session files and are used only for local display and estimation.

## Notes

- After switching accounts, already-running Codex instances may still use the old configuration. Restart Codex or open a new instance to ensure the new account is used.
- Aggregate mode requires Codex to use the local gateway endpoint. Codex instances opened before the mode switch usually do not join automatically.
- Cost statistics are local estimates based on token counts and price presets. They are not official billing data.
- Advanced Keep Awake simulates tiny mouse movement. Enable it only when it fits your workflow.
- If Windows security software blocks the single-file executable, verify the file source before allowing it.

## Version

Current version: `0.1.0`

See [CHANGELOG.md](./CHANGELOG.md) for release notes.

## License

WinCodexBar is licensed under the MIT License. See [LICENSE](./LICENSE).
