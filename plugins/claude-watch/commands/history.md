---
description: Open the Claude Watch history panel for this session
allowed-tools: Bash(powershell.exe:*)
---

!`powershell.exe -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/scripts/history.ps1" -SessionId "${CLAUDE_SESSION_ID}"`

Relay the status line above to the user verbatim. Do nothing else.
