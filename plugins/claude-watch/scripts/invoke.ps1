# Thin launcher for the claude-watch plugin hooks. Locates the claude-watch executable and forwards
# this hook's stdin payload to `claude-watch handle <args>`, relaying its stdout back to Claude Code.
# All command logic lives in the tray app itself (ClaudeWatch.exe handle ...); this script only finds
# the binary and forwards. It no-ops gracefully when claude-watch isn't installed, so a missing app
# never breaks the session.
#
# The app is a WinExe (GUI subsystem): PowerShell's `|`/`&` operators detach such processes and drop
# their stdio, so we drive it through System.Diagnostics.Process with explicit redirection instead.
# Everything is moved as raw UTF-8 (no BOM) to survive the console code page on both ends.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$HandleArgs)
$ErrorActionPreference = 'SilentlyContinue'

# Read this hook's own stdin as UTF-8, independent of the console input encoding.
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
$payload = $reader.ReadToEnd()
$reader.Dispose()

# Resolve the exe: explicit override (handy for dev), then PATH, then the stable Velopack install dir.
$exe = $null
if ($env:CLAUDE_WATCH_EXE -and (Test-Path $env:CLAUDE_WATCH_EXE)) {
  $exe = $env:CLAUDE_WATCH_EXE
}
if (-not $exe) {
  $exe = (Get-Command 'claude-watch' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
}
if (-not $exe) {
  $fallback = Join-Path $env:LOCALAPPDATA 'ClaudeWatch\current\ClaudeWatch.exe'
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
