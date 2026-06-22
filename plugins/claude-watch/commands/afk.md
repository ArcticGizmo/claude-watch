---
description: Toggle Claude Watch external (ntfy) notifications for this session
disable-model-invocation: true
---

Handled directly by the claude-watch plugin's UserPromptSubmit hook, which runs
`scripts/afk.ps1` and reports the result to you. This body never reaches the model.
