# On SessionEnd, removes ~/.claude/sessions/{session_id}.mode so claude-watch doesn't
# show a stale mode for a session that has ended. SessionEnd carries session_id but not
# permission_mode, so this is cleanup-only. Always exits 0.
$ErrorActionPreference = 'SilentlyContinue'
$j = [Console]::In.ReadToEnd() | ConvertFrom-Json
if ($j.session_id) {
  Remove-Item -Force (Join-Path $env:USERPROFILE ".claude\sessions\$($j.session_id).mode")
}
exit 0
