# Claude Code provider

The Claude Code provider surfaces your **Claude.ai (Pro/Max) subscription** rate-limit usage —
the 5-hour rolling window and the weekly (7-day) window.

## How it works

Claude Code does **not** expose subscription rate limits through a stable API or any persistent
file. It only passes them to a configured **statusline command**, on the stdin JSON, as a
`rate_limits` field:

```json
"rate_limits": {
  "five_hour": { "used_percentage": 23.5, "resets_at": 1738425600 },
  "seven_day": { "used_percentage": 41.2, "resets_at": 1738857600 }
}
```

So the integration has two parts:

1. A **statusline helper** ([`tools/claude-statusline-capture.ps1`](../tools/claude-statusline-capture.ps1))
   that Claude Code runs on every status refresh. It writes `rate_limits` to a local file and
   still prints a compact status bar.
2. The **`ClaudeCodeRateLimitProvider`**, which reads that file:

   ```
   %LOCALAPPDATA%\AiRateLimits\claude\rate_limits.json
   ```

The provider and the helper agree on this path.

## Setup

1. Add the helper to your Claude Code `settings.json` (`~/.claude/settings.json`):

   ```json
   {
     "statusLine": {
       "type": "command",
       "command": "pwsh -NoProfile -File \"C:\\Development\\ai-rate-limits\\tools\\claude-statusline-capture.ps1\""
     }
   }
   ```

   Use `pwsh` (PowerShell 7+) if installed, otherwise `powershell`. If you already have a
   statusline script, merge the file-writing block from the helper into it instead of replacing it.

2. Open a Claude Code session so the statusline runs at least once and populates the file.

That's it — no manual enabling. Every provider that finds data is displayed automatically, so
the Claude Code panel appears as soon as the file has rate-limit windows.

## Behavior and limitations

- `rate_limits` is only present for **Claude.ai Pro/Max** subscribers, and only **after the first
  API response** in a session. Each window can be independently absent.
- The data is only as fresh as the **last Claude Code statusline refresh**. The provider adds a
  note when the data is older than an hour.
- If a window's `resets_at` has already passed, the stored percentage is stale (the window rolled
  over). The provider reports that window as `0%` with an explanatory note rather than a false high.
- Claude Code **subscription** usage and **Anthropic API-key** usage are different systems; this
  provider only covers the subscription side.
