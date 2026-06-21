# Toggles this session's opt-in to claude-watch external (ntfy) notifications by creating or removing
# ~/.claude/sessions/{session_id}.afk — a presence marker the tray app reads each scan. Prints a single
# human-readable status line (the slash command relays it to the user). Always exits 0.
[CmdletBinding()]
param([string]$SessionId)

$ErrorActionPreference = 'SilentlyContinue'

# Resolve the session id: prefer the value the slash command substitutes in, fall back to the
# environment variable, then give up gracefully rather than touch the wrong session.
if ([string]::IsNullOrWhiteSpace($SessionId) -or $SessionId -like '*CLAUDE_SESSION_ID*') {
  $SessionId = $env:CLAUDE_SESSION_ID
}
if ([string]::IsNullOrWhiteSpace($SessionId)) {
  Write-Output 'Could not determine the current session id, so external notifications were left unchanged.'
  exit 0
}

# Fail gracefully when the tray app isn't running — nothing would consume the marker.
if (-not (Get-Process -Name 'ClaudeWatch' -ErrorAction SilentlyContinue)) {
  Write-Output "Claude Watch isn't running, so external notifications can't be toggled right now."
  exit 0
}

$dir = Join-Path $env:USERPROFILE '.claude\sessions'
if (-not (Test-Path $dir)) {
  Write-Output 'Claude Watch session directory not found; external notifications were left unchanged.'
  exit 0
}

$marker = Join-Path $dir "$SessionId.afk"
if (Test-Path $marker) {
  Remove-Item -Force $marker
  Write-Output 'External (ntfy) notifications are now OFF for this session.'
} else {
  Set-Content -Path $marker -Value $SessionId -NoNewline -Encoding ASCII
  Write-Output 'External (ntfy) notifications are now ON for this session (requires external notifications enabled in Claude Watch settings).'
}
exit 0
