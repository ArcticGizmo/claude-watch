# Reads the hook JSON from stdin and writes the session's live permission mode to
# ~/.claude/sessions/{session_id}.mode, which claude-watch's SessionMonitor reads.
# Always exits 0 so a hook failure never blocks Claude Code.
$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
$j = $raw | ConvertFrom-Json
$id = $j.session_id
$mode = $j.permission_mode
if ($id -and $mode) {
  $dir = Join-Path $env:USERPROFILE '.claude\sessions'
  if (Test-Path $dir) {
    Set-Content -Path (Join-Path $dir "$id.mode") -Value $mode -NoNewline -Encoding ASCII
  }
}
exit 0
