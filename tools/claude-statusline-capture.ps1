#requires -Version 5.1
<#
.SYNOPSIS
  Claude Code statusline helper for AI Rate Limits.

.DESCRIPTION
  Claude Code only exposes Claude.ai (Pro/Max) rate-limit usage to its statusline command,
  via the `rate_limits` field on the stdin JSON. This script captures that field to a local
  file that the AiRateLimits ClaudeCodeRateLimitProvider reads, then prints a short statusline
  so you still get a useful status bar.

  Output file: %LOCALAPPDATA%\AiRateLimits\claude\rate_limits.json

.NOTES
  Configure it in your Claude Code settings.json:

    {
      "statusLine": {
        "type": "command",
        "command": "pwsh -NoProfile -File \"C:\\Development\\ai-rate-limits\\tools\\claude-statusline-capture.ps1\""
      }
    }

  Use `pwsh` (PowerShell 7+) if available; otherwise `powershell`. rate_limits is only present
  for Claude.ai Pro/Max subscribers, after the first API response in a session.
#>

$ErrorActionPreference = 'Stop'

# Read the full statusline JSON from stdin.
$raw = [Console]::In.ReadToEnd()

try {
    $data = $raw | ConvertFrom-Json
} catch {
    # If parsing fails, emit nothing useful but do not break the status bar.
    Write-Output ''
    return
}

# Persist rate_limits (when present) for the AiRateLimits provider.
if ($data.PSObject.Properties.Name -contains 'rate_limits' -and $null -ne $data.rate_limits) {
    $outDir = Join-Path $env:LOCALAPPDATA 'AiRateLimits\claude'
    if (-not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    $outFile = Join-Path $outDir 'rate_limits.json'

    $payload = [ordered]@{
        captured_at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        rate_limits = $data.rate_limits
    }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -Path $outFile -Encoding utf8
}

# Print a compact status line (model + 5h/7d usage when available).
$model = if ($data.model -and $data.model.display_name) { $data.model.display_name } else { 'Claude' }
$parts = @()
if ($data.rate_limits) {
    if ($null -ne $data.rate_limits.five_hour -and $null -ne $data.rate_limits.five_hour.used_percentage) {
        $parts += ('5h: {0:N0}%' -f [double]$data.rate_limits.five_hour.used_percentage)
    }
    if ($null -ne $data.rate_limits.seven_day -and $null -ne $data.rate_limits.seven_day.used_percentage) {
        $parts += ('7d: {0:N0}%' -f [double]$data.rate_limits.seven_day.used_percentage)
    }
}

if ($parts.Count -gt 0) {
    Write-Output ("[{0}] | {1}" -f $model, ($parts -join ' '))
} else {
    Write-Output ("[{0}]" -f $model)
}
