<div align="center">

# AI Rate Limits

A small Windows tray utility that shows your current **AI assistant usage limits** at a glance —
for **Codex**, **Claude Code**, **GitHub Copilot**, and **JetBrains AI**.

It runs quietly in the system tray, polls each provider, and turns its icon green / orange / red
so you always know whether you're safe, close to a limit, or blocked.

</div>

---

## Features

- **Auto-discovery** – every provider that finds data is shown automatically; nothing to enable by hand.
- **Four providers** – Codex, Claude Code, GitHub Copilot, JetBrains AI.
- **Tray status** – the tray icon colour reflects the worst limit across all found providers.
- **Compact dashboard** – per-provider tabs with real brand icons, capacity bars (remaining %),
  window durations, and reset countdowns.
- **Notifications** – a tray balloon when overall health changes (e.g. *"Codex 5 hour window:
  12% available. Resets in 14m."*).
- **Reset-aware polling** – polls every 5 s around a reported reset, otherwise on the normal interval.
- **Codex cost estimate** – an informational monthly spend estimate from your local Codex session
  logs, priced via [models.dev](https://models.dev).
- **Settings** – warning/critical thresholds, refresh interval, start-with-Windows, Copilot
  enterprise host, JetBrains IDE path.
- **Single instance & autostart**, file-based settings, and rotating logs.

## Status colours

| Colour | Meaning |
|---|---|
| 🟢 Green | All known usage is below the warning threshold |
| 🟠 Orange | A bucket is at/above the warning threshold (default 85% used) |
| 🔴 Red | A bucket is at/above the critical threshold (default 100% used) |
| ⚪ Gray | Status unknown (no data yet) |

## Requirements

- **Windows 10/11** (this is a WPF app — Windows only for now).
- The **self-contained** release bundles the .NET runtime; nothing else to install.
- Building from source needs the **.NET 10 SDK**.

## Install

1. Download the latest `AiRateLimits-*-win-x64.zip` from the
   [Releases](https://github.com/linusniederer/ai-rate-limits/releases) page.
2. Unzip and run `AiRateLimits.exe`.

> **SmartScreen note:** the executable is currently **unsigned**, so Windows SmartScreen may warn
> on first launch. Choose *More info → Run anyway*. (Code signing requires a certificate and may be
> added later.)

## Providers — how each gets its data

Each provider reads **local** state or a **personal** token; the app never modifies your accounts.

### Codex
- Reads the access token from `%USERPROFILE%\.codex\auth.json` (or `CODEX_HOME`) and calls the
  live usage endpoint; falls back to the cached `codex.rate_limits` event in `logs_2.sqlite`.
- Buckets: **5 hour window** and **Weekly window**.
- The monthly **cost estimate** is scanned from `~/.codex/sessions` and priced via models.dev
  (cached 24 h, with a built-in fallback). It is an estimate from local logs, not an official bill.

### Claude Code
Claude Code only exposes Claude.ai (Pro/Max) usage to a **running interactive session**, so:
- If the OAuth token in `~/.claude/.credentials.json` is still valid, the app queries the usage
  API directly (read-only — it never refreshes or rewrites the token).
- Otherwise it reads a file written by an optional **statusline helper**.

To enable the statusline fallback, add to `~/.claude/settings.json`:

```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell -NoProfile -File \"C:\\path\\to\\ai-rate-limits\\tools\\claude-statusline-capture.ps1\""
  }
}
```

Then open an interactive `claude` session and send one message. Buckets: **5 hour** and **Weekly**.
See [docs/claude-code-provider.md](docs/claude-code-provider.md) for details.

### GitHub Copilot
- Sign in from **Settings → GitHub Copilot → Sign in** (GitHub device-flow login). The token is
  stored in the **Windows Credential Manager**, never on disk in plain text.
- Reads the internal `copilot_internal/user` endpoint. Buckets: **Premium requests**, **Chat**, and
  **top-up** when overage is permitted. Enterprise hosts are supported via the host setting.

### JetBrains AI
- Reads the local Rider quota file `…\options\AIAssistantQuotaManager2.xml` (auto-discovered, or set
  a manual IDE path in Settings). Buckets: **Monthly credits** and **Top-up credits**.

## Settings

Stored at `%APPDATA%\AiRateLimits\settings.json`:

| Setting | Default | Notes |
|---|---|---|
| Warning threshold | 85% | Used → turns orange |
| Critical threshold | 100% | Used → turns red |
| Refresh interval | 5 min | Clamped 1–240 |
| Start with Windows | off | Written to the `HKCU\…\Run` registry key |
| Copilot enterprise host | – | For GitHub Enterprise |
| JetBrains IDE path | – | Optional manual override |

Provider tokens are **not** stored here (Credential Manager / local files are used instead).

## Build from source

```powershell
dotnet build .\AiRateLimits\AiRateLimits.csproj -c Release
```

Publish a self-contained single-file build:

```powershell
dotnet publish .\AiRateLimits\AiRateLimits.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
```

> The build automatically closes a running `AiRateLimits.exe` first (it would otherwise lock the
> output file).

## Logging

Rotating logs (1 MB × 2 files) at `%LOCALAPPDATA%\AiRateLimits\logs\airatelimits.log`. Provider
secrets and tokens are never logged.

## Limitations

- **Windows only** (WPF). A cross-platform port (Avalonia) is possible but not done.
- Several providers rely on **local state or internal/undocumented endpoints** that can change.
- Claude Code subscription usage is only fresh while you use Claude Code; API and subscription
  usage are different systems.
- Cost estimates depend on local logs and public model prices and can differ from official billing.

## Credits

Provider brand icons are derived from [CodexBar](https://github.com/steipete/CodexBar); see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Provider names and logos are trademarks of their
respective owners.

## License

[MIT](LICENSE)
