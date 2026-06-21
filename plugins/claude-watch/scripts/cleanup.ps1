# On SessionEnd, removes this session's watch-control sidecar files (.afk opt-in marker and any
# stale .history trigger) so claude-watch doesn't carry state for a session that has ended.
# SessionEnd carries session_id on stdin. Always exits 0.
$ErrorActionPreference = 'SilentlyContinue'
$j = [Console]::In.ReadToEnd() | ConvertFrom-Json
if ($j.session_id) {
  $dir = Join-Path $env:USERPROFILE '.claude\sessions'
  Remove-Item -Force (Join-Path $dir "$($j.session_id).afk")
  Remove-Item -Force (Join-Path $dir "$($j.session_id).history")
}
exit 0
