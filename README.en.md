# WinCodexBar

[简体中文](./README.md) · [Download](https://github.com/windycn/WinCodexBar/releases/latest)

A Windows tray workspace for Codex accounts, quotas, model settings, and local usage insights.

Version **0.2.1** applies DPI scaling before the first frame, reuses dashboard account rows, and limits tray animation redraws to the refresh icon. Background refresh follows the macOS app: the active account is checked every minute by default (configurable), and all other accounts every five minutes. Keep Awake is off by default and can be toggled from the tray. Away Mode can wait 5, 15, or 30 seconds before showing a black overlay without locking Windows or changing power settings; mouse movement, a key, or a click wakes it while Codex keeps running.

Automatic update checks run shortly after startup and every six hours. Installation requires confirmation: the app downloads the matching architecture package, verifies SHA256, backs up account/settings files, then replaces program files and restarts. Replacement failures trigger a rollback attempt with retained backups and logs.

Download the x64, x86, or arm64 ZIP, extract it to a separate writable folder, and launch `WinCodexBar.exe`. The .NET runtime is included. For the first upgrade from 0.1.x, exit the old version and replace its files manually; subsequent releases can use the new updater.

Account/settings files remain in `%USERPROFILE%\.codexbar` (or beneath `CODEXBAR_HOME`). Version upgrades create backups under `.codexbar\backups`. Update staging, program backups and `result.txt` are under `%LOCALAPPDATA%\WinCodexBar\updates`. Session history is not deleted. Backups and account exports contain credentials and should remain private.

Single-click the tray icon for quick actions; double-click for the dashboard. Appearance settings offer six icon styles: quota ring, dual quota, remaining percentage, status light, bars, and classic. Settings also provide manual update checks. Model availability depends on the account and Codex version; existing selections are preserved.

Token costs are estimates, not billing records. The public reset-window summary is attributed to [Codex Radar](https://codexradar.com/); it is not evidence that an individual account has reset. Codex Radar’s protected full API is not queried.

Build using .NET 8 SDK:

```powershell
dotnet build windows/CodexBarWin/CodexBarWin.csproj -c Release
dotnet run --project tests/WinCodexBar.CoreTests -c Release
dotnet run --project tests/WinCodexBar.WindowsTests -c Release
```

Window tests require Windows and use isolated temporary data. Mixed-monitor hot-plug behavior and hardware-specific rendering still need device acceptance testing.

[MIT License](./LICENSE) · [Third-party notices](./THIRD_PARTY_NOTICES.md)

Account quota windows are shown only when supplied by the service. Weekly-only accounts have no five-hour placeholder, and the tray indicator uses weekly usage. Reset-credit counts and individual expiration times are read-only; account details show exact reset dates, seconds, and local UTC offsets. There is no redemption action or endpoint.
