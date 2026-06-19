# claude-watch — TODO

Planned features for the system-tray Claude Code monitor. Three were chosen from a brainstorm.
Features 2 and 3 both depend on parsing the **transcript JSONL files**
(`~/.claude/projects/{encoded-cwd}/{sessionId}.jsonl`) — which the app does not read today.
This is the biggest new capability: the session files give status only; the transcripts give
activity and token usage.

## Key facts (verified against the real machine)

- Session file `~/.claude/sessions/{pid}.json` → `pid`, `sessionId`, `cwd`, `status`, `updatedAt`.
  Already parsed in `SessionMonitor.ReadSession` (`src/SessionMonitor.cs:117`).
- Transcript path: `~/.claude/projects/{encoded-cwd}/{sessionId}.jsonl`. `sessionId` maps directly
  to the filename. Robust lookup: glob `~/.claude/projects/*/{sessionId}.jsonl` (UUID → unique).
  Fast path: derive encoded dir from `cwd` (every non-alphanumeric char → `-`).
- Transcript is JSON-lines. `"type":"assistant"` records carry `message.model`,
  `message.usage` = `{ input_tokens, output_tokens, cache_creation_input_tokens,
  cache_read_input_tokens }`, and `message.content[]` `tool_use` blocks with `name` + `input`.
- Pricing per **MTok** (claude-api ref, 2026-06): Opus 4.8/4.7/4.6 $5 in / $25 out
  (cache write $6.25, cache read $0.50); Sonnet 4.6 $3 / $15; Haiku 4.5 $1 / $5.
  (cache write ≈ 1.25× input, cache read ≈ 0.1× input.)
- No new NuGet packages needed — `System.Text.Json` + `System.Net.Http` cover it. SDK-style
  `.csproj` globs `src/*.cs`, so new files need no `<Compile>` entries.

---

## Feature 1 — Phone push notifications (provider: ntfy)

Mirror "needs attention" / "awaiting input" alerts to a phone. ntfy = single HTTP POST to a topic
URL, free, no keys, has iOS/Android apps.

- [ ] `src/AppSettings.cs` — add `PushEnabled`, `NtfyServer` (default `https://ntfy.sh`),
      `NtfyTopic`, `PushNeedsAttention`, `PushAwaitingInput`.
- [ ] New `src/PushNotifier.cs` — static `HttpClient`; `Notify(title, body, urgent)` fire-and-forget
      POST to `{server}/{topic}` with `Title`/`Priority` headers; no-op on empty topic; never throws.
- [ ] `src/OverlayApplicationContext.cs` — call `Notify(...)` from `OnNeedsAttention` (`:226`) and
      `OnAwaitingInput` (`:237`), honoring per-event toggles.
- [ ] Tray menu (`:51`) — add **"Phone Alerts…"** opening new `src/PushSettingsForm.cs`
      (enable checkbox, server, topic, **Send test** button); saves via `_settings.Save()`.

## Feature 2 — Live activity in the overlay

Show each running session's most recent tool call ("Reading X", "Running: npm test", "Editing Y").

- [ ] New `src/TranscriptReader.cs` — `GetActivity(sessionId, cwd)`: locate transcript, read only the
      **tail** (~32 KB seek, `FileShare.ReadWrite`), parse from the end for the latest `tool_use`,
      map `(toolName, input)` → friendly phrase; return `null` on any failure; never read whole file.
- [ ] `src/ClaudeSession.cs` — add optional `string? Activity = null` (last param; keeps call sites
      compiling).
- [ ] `src/SessionMonitor.cs` `ReadSession` — only when `status == Running`, populate `Activity`.
- [ ] `src/OverlayForm.cs` `DrawRow` (`:222`) — dim second line under project name; bump `RowHeight`
      or truncate via existing `TruncateString` (`:313`).
- [ ] `src/IndicatorTooltipForm.cs` — add activity line to hover tooltip.

## Feature 3 — Token & cost tracking

Read-only stats window: per-session / per-project / per-day tokens and estimated $.

- [ ] New `src/UsageStats.cs` — `ModelPricing` table keyed by model id (unknown → Opus rate, flagged);
      `Aggregate()` scans `~/.claude/projects/**/*.jsonl` summing `usage` per (project, model, day);
      cost = Σ token-class × rate. Run on a background thread (`Task.Run`) with a "Calculating…" state.
      Optional later: per-file `(lastWriteTime, length, summary)` cache for fast re-opens.
- [ ] New `src/UsageStatsForm.cs` — dark WinForms window (match overlay palette); table
      Project | Tokens (in/out/cache) | Est. cost; today + grand-total rows; **Refresh** button.
- [ ] `src/OverlayApplicationContext.cs` — tray item **"Usage & Cost…"** (`:51`) opens/focuses one
      `UsageStatsForm` instance.
- [ ] (Optional) `src/IndicatorTooltipForm.cs` — show active session's running cost in tooltip.

---

## Verification

- [ ] `dotnet build src/ClaudeWatch.csproj` compiles clean.
- [ ] Push: subscribe phone to topic, **Send test** arrives; real finish/permission → push + balloon;
      toggle off → no push.
- [ ] Live activity: running session shows updating phrase in row + tooltip; idle shows none.
- [ ] Usage: totals populate after "Calculating…"; spot-check one project's token sum and confirm
      cost = tokens × rate table.
- [ ] UI thread never blocks — pushes and aggregation run off-thread; overlay stays draggable.
