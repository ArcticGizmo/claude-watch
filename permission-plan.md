# Permission-mode detection via a self-hosted Claude Code plugin

## Context

claude-watch needs each running session's **interactive** permission mode (the Shift+Tab cycle:
`normal` / `acceptEdits` / `plan` / `bypassPermissions`). Investigation confirmed this mode is **not**
available in `~/.claude/sessions/*.json`, **not** in the transcript JSONL (its `{"type":"mode"}`
record is always `normal`, even for a live plan-mode session), and **not** in the statusline input.
The **only** reliable source is the hook stdin payload, where every hook event carries a
`permission_mode` field (`default` | `plan` | `acceptEdits` | `bypassPermissions`).

`SessionMonitor.ReadPermissionMode` (`src/SessionMonitor.cs:220`) already reads
`~/.claude/sessions/{sessionId}.mode` and maps its contents to the `PermissionMode` enum. So the goal
is simply: **get something to write that `.mode` file from the live hook payload** — without the user
hand-editing `settings.json`.

Decision: ship a **Claude Code plugin** that does the writing, and make **this repo double as the
plugin marketplace**. claude-watch then assists the user with adding the marketplace, installing the
plugin, and reporting health — so the only thing the user installs is claude-watch + a couple of
guided clicks.

> Several Claude Code CLI flags / settings keys below (e.g. `claude plugin …`, `enabledPlugins`,
> `known_marketplaces.json`) come from the docs but **must be verified against the user's installed
> Claude Code version (2.1.183) during implementation** — treat them as the expected mechanism, not
> gospel, and confirm the exact strings before shipping the verification logic.

---

## Part A — Repo as marketplace + plugin

Add to the repo root (alongside the existing .NET `src/`):

```
claude-watch/
├── .claude-plugin/
│   └── marketplace.json                      # this repo IS the marketplace
├── plugins/
│   └── permission-monitor/
│       ├── .claude-plugin/
│       │   └── plugin.json
│       ├── hooks/
│       │   └── hooks.json
│       └── scripts/
│           ├── write-mode.ps1                # writes {session_id}.mode from hook stdin
│           └── cleanup-mode.ps1              # deletes {session_id}.mode on SessionEnd
└── src/ ...                                  # existing app
```

### `.claude-plugin/marketplace.json`

```json
{
  "name": "claude-watch",
  "owner": { "name": "Jonathan Howell", "email": "" },
  "plugins": [
    {
      "name": "permission-monitor",
      "source": "./plugins/permission-monitor",
      "description": "Writes each session's live permission mode to ~/.claude/sessions/{id}.mode for claude-watch."
    }
  ]
}
```

`source: "./plugins/permission-monitor"` is resolved relative to the repo (marketplace) root, so the
plugin lives in the same repo.

### `plugins/permission-monitor/.claude-plugin/plugin.json`

```json
{
  "name": "permission-monitor",
  "version": "0.1.0",
  "description": "Writes live permission_mode to ~/.claude/sessions/{session_id}.mode; removes it on SessionEnd.",
  "author": { "name": "Jonathan Howell" },
  "hooks": "./hooks/hooks.json"
}
```

### `plugins/permission-monitor/hooks/hooks.json`

There is **no dedicated mode-change event**, so register the write on every event that carries
`permission_mode` and fires often, and the delete on `SessionEnd`. Use the exec form (`command` +
`args`) to dodge Windows/Git-Bash quoting issues.

```json
{
  "hooks": {
    "UserPromptSubmit": [{ "matcher": "", "hooks": [{ "type": "command", "command": "powershell.exe",
      "args": ["-NoProfile","-ExecutionPolicy","Bypass","-File","${CLAUDE_PLUGIN_ROOT}/scripts/write-mode.ps1"] }] }],
    "PreToolUse":       [{ "matcher": "", "hooks": [{ "type": "command", "command": "powershell.exe",
      "args": ["-NoProfile","-ExecutionPolicy","Bypass","-File","${CLAUDE_PLUGIN_ROOT}/scripts/write-mode.ps1"] }] }],
    "PostToolUse":      [{ "matcher": "", "hooks": [{ "type": "command", "command": "powershell.exe",
      "args": ["-NoProfile","-ExecutionPolicy","Bypass","-File","${CLAUDE_PLUGIN_ROOT}/scripts/write-mode.ps1"] }] }],
    "Stop":             [{ "matcher": "", "hooks": [{ "type": "command", "command": "powershell.exe",
      "args": ["-NoProfile","-ExecutionPolicy","Bypass","-File","${CLAUDE_PLUGIN_ROOT}/scripts/write-mode.ps1"] }] }],
    "SessionEnd":       [{ "matcher": "", "hooks": [{ "type": "command", "command": "powershell.exe",
      "args": ["-NoProfile","-ExecutionPolicy","Bypass","-File","${CLAUDE_PLUGIN_ROOT}/scripts/cleanup-mode.ps1"] }] }]
  }
}
```

- `${CLAUDE_PLUGIN_ROOT}` expands to the installed plugin directory — the portable way to reference
  bundled scripts. (Confirm exec-form `args` is honored on the installed CC version; if not, fall
  back to a single quoted `command` string.)
- PowerShell is chosen because the product is Windows-only and `powershell.exe` is always present
  (no Node/Bash dependency). If cross-platform is ever wanted, swap to a Node script.

### `scripts/write-mode.ps1`

Reads the hook JSON from stdin, extracts `session_id` + `permission_mode`, writes the mode token to
`~/.claude/sessions/{session_id}.mode`.

```powershell
$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
$j = $raw | ConvertFrom-Json
$id = $j.session_id; $mode = $j.permission_mode
if ($id -and $mode) {
  $dir = Join-Path $env:USERPROFILE '.claude\sessions'
  if (Test-Path $dir) { Set-Content -Path (Join-Path $dir "$id.mode") -Value $mode -NoNewline -Encoding ASCII }
}
exit 0
```

**Mode mapping is already handled** — `ReadPermissionMode` lowercases the file contents and matches:
`acceptedits`→AcceptEdits, `plan`→Plan, `bypasspermissions`→Bypass, anything else (incl. `default`)
→Normal. So writing the raw `permission_mode` string works without changing the C# reader. (Note the
hook has no `auto` value, so the enum's `Auto` simply won't be produced by this source.)

### `scripts/cleanup-mode.ps1`

```powershell
$ErrorActionPreference = 'SilentlyContinue'
$j = [Console]::In.ReadToEnd() | ConvertFrom-Json
if ($j.session_id) {
  Remove-Item -Force (Join-Path $env:USERPROFILE ".claude\sessions\$($j.session_id).mode")
}
exit 0
```

SessionEnd carries `session_id` (and a `reason`) but **not** `permission_mode` — fine, cleanup only.

---

## Part B — claude-watch assists install + health

New class `src/PluginManager.cs` + a tray submenu **"Permission detection"**.

1. **Detect Claude Code CLI** — locate `claude` on `PATH` (e.g. `where claude`). If absent, fall back
   to showing copyable slash commands for the user to paste into a session.
2. **Add marketplace** — shell out to the non-interactive CLI (verify exact form on 2.1.183):
   `claude plugin marketplace add ArcticGizmo/claude-watch`
   (the repo already used for Velopack updates — see `OverlayApplicationContext.cs:286`).
3. **Install plugin** — `claude plugin install permission-monitor@claude-watch`.
4. **Verify / health** — read `~/.claude/settings.json` and check
   `enabledPlugins["permission-monitor@claude-watch"] == true`, and that the marketplace is known
   (per-version: `~/.claude/settings.json` `extraKnownMarketplaces` and/or
   `~/.claude/known_marketplaces.json`). Prefer `claude plugin list --json` if that flag exists on
   the installed version; otherwise parse settings directly. Surface a tray status: green = installed
   + enabled, amber = marketplace added but plugin disabled/not installed, red = not present / CLI
   missing.
5. **Live confirmation** — claude-watch already watches `~/.claude/sessions/`; once a `.mode` file
   appears for an active session and the overlay shows the correct badge, detection is confirmed
   end-to-end. The tray health item can show "last mode write: <session> <mode> <time>".
6. **Uninstall** — a menu item that runs `claude plugin uninstall …` / `marketplace remove …` (verify
   exact verbs) so the user can cleanly back out.

Fallback (no CLI): a dialog with the two `/plugin …` commands and a "Copy" button, plus the same
health readout from settings.json once they've run them.

---

## What does NOT change

- `SessionMonitor.ReadPermissionMode` (`src/SessionMonitor.cs:220`) and the existing
  `FileSystemWatcher` on `~/.claude/sessions/` — the plugin writes exactly the `{sessionId}.mode`
  file the watcher already reacts to and the reader already parses. No reader change required.
- The `PermissionMode` enum and the overlay/tooltip mode badges.

---

## Caveats

- **Staleness**: with no mode-change hook event, a Shift+Tab toggle made while a session is *idle*
  isn't written until the next prompt/tool/stop. Registering on the five events above bounds the lag
  to "next interaction." This matches the old hand-rolled hook's behavior.
- **Verify CC surface on 2.1.183 before shipping**: the `claude plugin …` subcommands/flags, the
  `enabledPlugins` / `extraKnownMarketplaces` keys, `known_marketplaces.json`, and exec-form `args`
  in plugin hooks are all from the docs and need a quick live confirmation; adjust the install/verify
  code to whatever the installed version actually exposes.
- **PowerShell dependency** is acceptable (Windows-only product). `-ExecutionPolicy Bypass -File`
  avoids policy prompts; scripts exit 0 always so a hook failure never blocks Claude Code.
- **settings.json edits**: claude-watch only *reads* settings for health; the CLI owns the writes, so
  the app won't clobber the user's existing hooks/plugins.

---

## Verification

1. Commit the marketplace + plugin files; from a Claude Code session run
   `/plugin marketplace add ArcticGizmo/claude-watch` then `/plugin install permission-monitor@claude-watch`.
2. In that session, Shift+Tab through `acceptEdits` / `plan` / `bypassPermissions`, submit a prompt or
   run a tool, and confirm `~/.claude/sessions/{session_id}.mode` appears with the matching token.
3. End the session → confirm the `.mode` file is deleted.
4. Confirm claude-watch's overlay/tooltip shows the correct mode badge live (it already reads the file).
5. In claude-watch, run the assisted **install** flow on a clean machine (no marketplace added) and
   confirm it adds the marketplace, installs+enables the plugin, and the health indicator goes green;
   then run **uninstall** and confirm it returns to red/clean.
6. `dotnet build src/ClaudeWatch.csproj` still compiles (PluginManager + menu additions only).
