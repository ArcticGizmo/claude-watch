# Launcher for the claude-watch plugin hooks. Two responsibilities:
#
#   1. Permission mode (every hook): extract ONLY session_id + permission_mode from the payload and
#      write ~/.claude/sessions/{session_id}.mode, the sidecar the running tray app watches. Tool-call
#      data carried by PreToolUse/PostToolUse is never read beyond those two fields and is never logged
#      or persisted anywhere.
#   2. Commands (prompt) and cleanup: forwarded to the tray app itself via `claude-watch handle <action>`,
#      which owns that logic. The app is a WinExe, so we drive it through System.Diagnostics.Process
#      with explicit redirection (PowerShell's `|`/`&` detach GUI-subsystem processes and drop stdio).
#
# `mode` events never spawn the exe, keeping the per-tool-call hot path cheap. Everything no-ops
# gracefully when claude-watch isn't installed, so a missing app never breaks the session.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$HandleArgs)
$ErrorActionPreference = 'SilentlyContinue'

$action = if ($HandleArgs.Count -ge 1) { $HandleArgs[0] } else { '' }

# Read this hook's own stdin as UTF-8, independent of the console input encoding.
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
$payload = $reader.ReadToEnd()
$reader.Dispose()

# Permission mode: read just the two fields we need and write the sidecar. Nothing else from the
# payload is touched, so tool-call inputs/outputs are never recorded.
try {
  $j = $payload | ConvertFrom-Json
  $sid = $j.session_id
  $mode = $j.permission_mode
  if ($sid -and $mode) {
    $dir = Join-Path $env:USERPROFILE '.claude\sessions'
    if (Test-Path $dir) {
      Set-Content -Path (Join-Path $dir "$sid.mode") -Value $mode -NoNewline -Encoding ASCII
    }
  }
} catch { }

# Mode-only events (PreToolUse / PostToolUse / Stop) are done — no exe needed.
if ($action -eq 'mode') { exit 0 }

# prompt / cleanup are handled first-class by the tray app. Locate the exe:
#   explicit override (handy for dev), then PATH, then the stable Velopack install dir.
$exe = $null
if ($env:CLAUDE_WATCH_EXE -and (Test-Path $env:CLAUDE_WATCH_EXE)) {
  $exe = $env:CLAUDE_WATCH_EXE
}
if (-not $exe) {
  $exe = (Get-Command 'claude-watch' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
}
if (-not $exe) {
  $fallback = Join-Path $env:LOCALAPPDATA 'ClaudeWatch\current\claude-watch.exe'
  if (Test-Path $fallback) { $exe = $fallback }
}
if (-not $exe) { exit 0 }  # not installed — let the prompt through / no-op

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = 'handle ' + ($HandleArgs -join ' ')
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8

$proc = [System.Diagnostics.Process]::Start($psi)
# Write the payload as UTF-8 bytes (no BOM) straight to the child's stdin.
$bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
$proc.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
$proc.StandardInput.BaseStream.Flush()
$proc.StandardInput.Close()
$out = $proc.StandardOutput.ReadToEnd()
$proc.WaitForExit()

if ($out) { [Console]::Out.Write($out) }
exit 0
