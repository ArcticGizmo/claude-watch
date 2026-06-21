# Asks claude-watch to open (or focus) its history panel on this session by dropping a one-shot
# ~/.claude/sessions/{session_id}.history trigger file, which the tray app consumes and deletes.
# Prints a single human-readable status line (the slash command relays it). Always exits 0.
[CmdletBinding()]
param([string]$SessionId)

$ErrorActionPreference = 'SilentlyContinue'

# Resolve the session id: prefer the value the slash command substitutes in, fall back to the
# environment variable, then give up gracefully.
if ([string]::IsNullOrWhiteSpace($SessionId) -or $SessionId -like '*CLAUDE_SESSION_ID*') {
  $SessionId = $env:CLAUDE_SESSION_ID
}
if ([string]::IsNullOrWhiteSpace($SessionId)) {
  Write-Output 'Could not determine the current session id, so the history panel was not opened.'
  exit 0
}

# Fail gracefully when the tray app isn't running — nothing would consume the trigger.
if (-not (Get-Process -Name 'ClaudeWatch' -ErrorAction SilentlyContinue)) {
  Write-Output "Claude Watch isn't running, so the history panel can't be opened."
  exit 0
}

$dir = Join-Path $env:USERPROFILE '.claude\sessions'
if (-not (Test-Path $dir)) {
  Write-Output 'Claude Watch session directory not found; the history panel was not opened.'
  exit 0
}

Set-Content -Path (Join-Path $dir "$SessionId.history") -Value $SessionId -NoNewline -Encoding ASCII
Write-Output 'Opening the Claude Watch history panel for this session.'
exit 0
