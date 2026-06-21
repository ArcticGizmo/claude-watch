# UserPromptSubmit hook for the claude-watch plugin.
#
# When the submitted prompt is /afk or /history (optionally namespaced, e.g. /claude-watch:afk),
# this runs the matching action script and returns a "block" decision so the prompt never reaches
# the model — the script's status line is shown to the user via the block reason. Any other prompt
# produces no output and passes through untouched.
#
# session_id arrives on stdin (reliable for hooks), so we hand it straight to the action script.
# The hook never throws and always exits 0: a UserPromptSubmit hook must not break the session.
$ErrorActionPreference = 'SilentlyContinue'

try {
  $j = [Console]::In.ReadToEnd() | ConvertFrom-Json
} catch {
  exit 0
}

$prompt = "$($j.prompt)".Trim()
$sid = "$($j.session_id)"

# Match only when the prompt *is* one of our commands (start-anchored), so a prompt that merely
# mentions "/afk" in prose is left alone.
if ($prompt -notmatch '^/(?:[\w-]+:)?(?<cmd>afk|history)\b') {
  exit 0
}
$cmd = $Matches.cmd.ToLowerInvariant()

$path = Join-Path $PSScriptRoot ($(if ($cmd -eq 'afk') { 'afk.ps1' } else { 'history.ps1' }))

# Run the action script in its own process and capture its single status line. Isolating it keeps
# its `exit 0` from terminating this hook.
$reason = ''
try {
  $reason = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $path -SessionId $sid | Out-String).Trim()
} catch {
  $reason = "Claude Watch: the /$cmd command failed to run."
}
if ([string]::IsNullOrWhiteSpace($reason)) {
  $reason = "Claude Watch: /$cmd completed."
}

# Block the prompt (erased before the model sees it); the reason is surfaced to the user only.
[Console]::Out.Write((@{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress))
exit 0
