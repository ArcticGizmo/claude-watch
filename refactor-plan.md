# Claude Watch — Maintainability Refactor Plan

> Status: **AGREED.** The four scope decisions (§6) are settled and reflected throughout:
> full refactor **including** the form splits (splits land last); **obvious bugs may be
> fixed** as the duplication is consolidated (called out per commit); files reorganised into
> **folders + sub-namespaces** (`ClaudeWatch.Data / .Ui / .App`); a **`ClaudeWatch.Tests`
> project** is added to lock down the pure logic before Phase 2.

## 1. Goal

The app has grown a broad, genuinely useful feature set (overlay, dense mode, usage bars,
context pressure, stats, history viewer, notifications local + external, quick links, plugin
control, QR/remote-control, auto start/close). The logic is sound and the code is unusually
well-commented. What it lacks is a **shared foundation**: the same handful of ideas are
re-implemented file-by-file. This refactor introduces the missing abstractions so that:

- a change to one concept (a colour, a path rule, the transcript cache strategy) is made in **one** place;
- new features compose existing building blocks instead of copy-pasting them;
- the two "god files" (`OverlayForm` 1,966 lines, `OverlayApplicationContext` 770 lines, `SettingsForm` 1,616 lines) shrink into focused units;
- behaviour is **preserved** — this is a refactor, not a rewrite.

Guiding constraints (from `CLAUDE.md`): owner-drawn text sizes from `font.Height`; measure-or-paint
stays in one routine; IO off the UI thread then marshalled back; **one** shared `Theme` palette;
`~/.claude` files read best-effort with `FileShare.ReadWrite` and never throwing out of a scan;
single reused window instances wired into Exit/Dispose/update.

## 2. The duplication inventory (what the refactor targets)

### A. Filesystem / `~/.claude` path logic — *no central owner*
`Path.Combine(UserProfile, ".claude", …)` is recomputed in **7** places: `SessionMonitor`
(`_sessionsDir`), `TranscriptReader` (`_projectsDir` *and* the static `FindTranscript`),
`SubAgentReader` (`_projectsDir`), `SessionStatsService` (`ProjectsDir`), `SessionHistory`
(`ProjectsDir`), `UsageMonitor` (`_claudeDir`), `PluginManager` (`UserHome/.claude`).

The **cwd → encoded-project-dir** rule (`Regex.Replace(cwd, "[^A-Za-z0-9]", "-")`) plus the
"try direct path, else scan every project dir for `{sessionId}.jsonl`" fallback is duplicated
**verbatim** three times: `TranscriptReader.ResolveTranscript`, `TranscriptReader.FindTranscript`
(a static near-clone of the instance method), and `SubAgentReader.ResolveTranscript`.

### B. Transcript "cache parse by (length, mtime)" — *the same dance, 7×*
`TranscriptReader` carries **five** parallel caches (activity, title, context-fill, bare-command,
artifacts), each with its own `…CacheEntry` record and the identical
`FileInfo → compare Length+LastWriteTimeUtc → return cached or parse+store` block.
`SubAgentReader` has **two** more (`_cache`, `_agentCache`). That's seven copies of one idea.

Alongside it, every parser repeats: open `FileStream` shared `ReadWrite`; (sometimes) seek to a
`TailBytes` tail and drop the partial first line; loop lines with a cheap `string.Contains`
pre-filter, then `JsonNode.Parse` inside a `try { … } catch { }` that swallows partial trailing lines.

### C. Transcript JSON shape parsing — *re-discovered per consumer*
There is no model of a transcript record. Each reader hand-walks `node["message"]["content"]`
arrays for `tool_use` / `tool_result` / `text` / `thinking` blocks. Helpers are duplicated:
`TokenLong` (`TranscriptReader`) ≡ `LongOf` (`SessionStatsService`); `ParseTimestamp`
(`TranscriptParser`) ≈ `ParseTimestamp`/inline parse (`SessionStatsService`). `ToolSummary` is the
one good shared piece here and shows the pattern to follow.

### D. UI drawing primitives — *copied everywhere*
- **Rounded-rectangle path** (`RoundedRect` / `RoundedPath` / `Pill` / `FillRoundedBar` / `FillRound`):
  ≈ 8 copies — `OverlayForm`, `PopoverMenu`, `UsageTooltipForm`, `QrCodeForm`, `StatsForm`, and three
  controls in `SettingsControls`.
- **Usage-bar rendering** (`DrawUsageBar`/`DrawBar` + `ElapsedPercent` + `UsageColor` + `Blend`):
  duplicated between `OverlayForm` and `SettingsControls.UsageBarsControl`.
- **Mode badge** (the fast-forward double-chevron): `OverlayForm.DrawModeBadge` and
  `ModeLegend.DrawModeBadge`; `ModeColor` exists in both `OverlayForm` and `Theme`.
- **Embedded-resource loaders** (`LoadEmbeddedBitmap`): 4 copies (`OverlayForm`, `StatsForm`,
  `SettingsForm`, `HistoryViewerForm`); `LoadEmbeddedText` once more.
- **Borderless dark-popup chrome** (rounded bg+border paint, `Esc`-to-close, `OnDeactivate`-to-close,
  `WS_EX_TOOLWINDOW`/`WS_EX_NOACTIVATE` `CreateParams`): re-implemented in `PopoverMenu`,
  `UsageTooltipForm`, `QrCodeForm` (and partially `DenseDropZoneForm`, `OverlayForm`).

### E. Colour palette — *fragmented, contradicting CLAUDE.md*
`Theme` exists and `CLAUDE.md` says to use it, yet the overlay and every popup hard-code their own
`Color.FromArgb`. The same ARGBs recur: `15,15,20` (bg), `225,225,235` (fg), `45,45,60` (border),
`34,197,94` (green/running), `251,146,60` (orange/attention), `250,204,21` (yellow/awaiting),
`96,165,250` (accent/remote). At least six separate private palettes (`OverlayForm`, `StatsForm`,
`HistoryViewerForm`, `PopoverMenu`, `UsageTooltipForm`, `QrCodeForm`).

### F. Off-UI-thread "load then marshal back" — *the pattern CLAUDE.md describes by hand*
`Task.Run(…).ContinueWith(t => BeginInvoke(…))` guarded by `IsHandleCreated && !IsDisposed` and
swallowing `ObjectDisposedException`/`InvalidOperationException` appears in
`OverlayApplicationContext` (`RefreshUsage`, `RefreshTodayStats`), `StatsForm.RefreshStats`,
`HistoryViewerForm.RefreshEntries`. When the docs spell out a pattern, it wants to be a helper.

### G. Orchestration concerns concentrated in `OverlayApplicationContext`
One 770-line class owns: tray menu, three window lifecycles, usage polling, today-stats, auto-close
state machine, **notification dispatch** (Windows balloon + chime + external ntfy + AFK/lock gating),
update checking, and first-run plugin install. The reused-window idiom (lazily create / re-focus if
open / null on close) is written out three times (`OpenSettings`, `OpenHistoryViewer`, `OpenStats`).

### H. `SettingsForm` UI toolkit + fluid layout
~15 control factories (`SectionTitle`, `BodyText`, `MakeButton`, `MakeToggle`, `TitleRow`, …), a
hand-rolled fluid-width system (`_fluidWidth`/`_fluidWrap`/`ApplyFluidWidth`), and a right-align
`Position()` closure copied into ~5 row builders. `FlatButton`/`StyleToggle` are re-implemented again
in `HistoryViewerForm` and `StatsForm`.

## 3. Target architecture

Introduce a thin, well-named foundation in three layers. The existing domain logic stays where it is;
it just stops re-implementing primitives. The three layers map onto the agreed namespaces/folders —
`ClaudeWatch.Data` (pure, testable), `ClaudeWatch.Ui` (WinForms primitives + windows),
`ClaudeWatch.App` (orchestration). **New foundation files are created in their final namespace from
the start**; the bulk move of the 31 existing files into folders/namespaces is the last phase (§4
Phase 6) so the logic refactors don't have to re-touch files.

```
Data layer (pure, testable, no WinForms):
  ClaudePaths           one owner of every ~/.claude location (sessions, projects, credentials, settings)
  TranscriptLocator     resolve transcript by (sessionId, cwd); enumerate transcripts; the cwd-encoding rule
  MtimeCache<T>         "compute-unless-(length,mtime)-unchanged" — replaces the 7 ad-hoc caches
  TranscriptJson        shared JSON helpers: token coercion, timestamp parse, content-block iteration
  (TranscriptReader, SubAgentReader, SessionStatsService, SessionHistory, TranscriptParser keep their
   domain logic but sit on the four primitives above)

UI foundation (WinForms, no app logic):
  Theme                 THE palette — extended to cover overlay status, body bg, popup chrome, glyph colours
  PaintKit              RoundedRect / FillRoundedBar / RoundedPill / UsageColor / Blend (Graphics extensions)
  Glyphs                mode badge, remote, mail, artifact, thermometer, side-collapse, drop-pin
  UsageBarRenderer      one routine drawn by both the overlay and the settings UsageBarsControl
  EmbeddedResources     LoadBitmap / LoadText
  ToolWindow            base Form for borderless dark popups (chrome, Esc, deactivate-close, ex-styles)
  ThemedControls        shared button/toggle/label factories + the fluid-layout helper

App / orchestration:
  UiDispatch            RunThenPost(work, apply) — the off-thread→marshal-back pattern, guarded
  WindowHost            ShowOrFocus<TForm>(ref field, factory) — the reused-window idiom
  NotificationService   decide+dispatch balloon/chime/ntfy for (NotificationKind, session); owns AFK/lock gating
```

## 4. Phased plan

Ordered foundation-first, lowest-risk-first. **Each phase is independently shippable** (compiles,
behaviour unchanged) so the refactor can stop or pause at any phase boundary. Phases 1–4 are
mechanical and low-risk; Phase 5 (form splits) is the invasive one and is **in scope**, scheduled
last so it can still be deferred without blocking the rest.

**Behaviour policy:** the refactor is behaviour-preserving by default, but **obvious latent bugs and
inconsistencies surfaced by consolidating the duplicated logic may be fixed in the same work** — each
such fix is isolated in its own commit and called out in the message, so it reviews separately from
the pure mechanical move.

### Phase 0 — Test project & golden baselines
Add `ClaudeWatch.Tests` (xUnit) and register it in `claude-watch.slnx`. Seed it with fixtures captured
from real `~/.claude` transcripts (a few representative `.jsonl` files + session/settings sidecars),
and pin the *current* outputs of the pure logic — `SessionStatsService`, `ModelContext`, `ToolSummary`,
`TranscriptReader`, `SubAgentReader`, `TranscriptParser` — as a golden baseline. This is the safety net
Phase 2 leans on. (The test project references the existing `ClaudeWatch.csproj`; the WinForms types it
can't drive are exercised manually per `CLAUDE.md`.)

### Phase 1 — Path & transcript-location foundation *(low risk, pure)* — ✅ DONE
1. Added `ClaudeWatch.Data.ClaudePaths` (`src/Data/ClaudePaths.cs`) with the canonical roots; routed
   the 7 path-constant sites through it (`SessionMonitor`, `TranscriptReader`, `SubAgentReader`,
   `SessionStatsService`, `SessionHistory`, `UsageMonitor`, `PluginManager`).
2. Added `ClaudeWatch.Data.TranscriptLocator` (`src/Data/TranscriptLocator.cs`) owning the
   cwd-encoding rule + direct-then-scan `Resolve` and `EnumerateTranscripts`/`EnumerateProjectDirectories`.
   Removed the 3 `ResolveTranscript`/`FindTranscript` copies and folded the
   `SessionStatsService.EnumerateCandidateTranscripts` / `SessionHistory.ListAll` directory walks onto it.
3. Drive-by (policy §6.2): aligned a pre-existing nullable warning in `SubAgentReader.Parse`
   (`block` → `block!`, matching the line above) so the build is warning-clean.
4. Verified: clean build (0 warnings/errors); real-data check confirms the projects dir and this
   repo's cwd-encoding resolve to the same files as before. No behaviour change.

### Phase 2 — Transcript reading primitives *(medium risk — the riskiest data work)*
1. Add `MtimeCache<T>`; collapse the 5 `TranscriptReader` caches and 2 `SubAgentReader` caches onto it.
2. Add `TranscriptJson` helpers: unify `TokenLong`/`LongOf`, `ParseTimestamp`, the shared
   tail-seek/drop-partial-line read, and content-block iteration (`tool_use`/`tool_result`/`text`).
3. Refactor the readers to use them; each reader keeps its own pre-filter strings and domain mapping.
4. Verify against the Phase 0 baseline — this is where subtle regressions (artifact de-dup,
   bare-command detection, context-fill) could hide, so verify hard.

### Phase 3 — UI foundation *(low risk, high consistency payoff)*
1. **Extend `Theme`** into the single palette: add overlay status colours, body/card backgrounds,
   popup chrome, and glyph colours. Replace hard-coded `Color.FromArgb` across `OverlayForm`,
   `StatsForm`, `HistoryViewerForm`, `PopoverMenu`, `UsageTooltipForm`, `QrCodeForm`,
   `DenseDropZoneForm`. (Directly satisfies the CLAUDE.md palette rule.)
2. Add `PaintKit` (rounded-rect family + `UsageColor`/`Blend`); replace the ~8 copies.
3. Add `EmbeddedResources`; replace the 4 `LoadEmbeddedBitmap` + `LoadEmbeddedText`.
4. Add `Glyphs`; move the mode-badge/remote/mail/artifact/thermometer/side-collapse painters in.
5. Add `UsageBarRenderer`; have the overlay and `UsageBarsControl` call it.
6. Add `ToolWindow` base form; re-base `PopoverMenu`, `UsageTooltipForm`, `QrCodeForm` on it.

### Phase 4 — App orchestration *(medium risk)*
1. Add `UiDispatch.RunThenPost`; apply at the 4 off-thread sites.
2. Add `WindowHost.ShowOrFocus`; collapse the three reused-window methods (keep Exit/Dispose/update wiring).
3. Extract `NotificationService` from `OverlayApplicationContext` (balloon + chime + ntfy + AFK/lock
   gating from `OnNeedsAttention`/`OnAwaitingInput`/`MaybeSendExternal`). The context becomes a thin
   wiring shell.

### Phase 5 — Large-form decomposition *(in scope; high churn; scheduled last)*
1. **`OverlayForm`**: extract (a) quick-link icon load/launch + the icon cache into `QuickLink`/a
   `QuickLinkLauncher`; (b) the dense-mode layout/drop-zone/monitor-docking into a `DenseModeController`;
   (c) painting onto `PaintKit`/`Glyphs`/`UsageBarRenderer`. Target: the form coordinates; helpers paint.
2. **`SettingsForm`**: lift the control factories + fluid-layout into `ThemedControls`; collapse the
   duplicated right-align `Position()` closures; share `FlatButton`/`StyleToggle` with the other forms.

### Phase 6 — Namespace / folder migration
Move the existing files into `Data/` `Ui/` `App/` folders under `src/` and convert them to the
matching `ClaudeWatch.Data` / `ClaudeWatch.Ui` / `ClaudeWatch.App` namespaces, fixing up
using-directives. (New files added in Phases 0–5 are already in their final namespace, so this phase
only moves the pre-existing 31.) Done last so the logic refactors never touch a file twice. Verify the
build and a full app run after the move.

## 5. Risks & mitigations
- **No existing tests + live append-only files.** Phase 0 exists precisely to baseline behaviour
  before Phase 2. The transcript parsers are the only genuinely risky area.
- **Behaviour drift in defensive parsing.** Keep each reader's exact pre-filter strings and
  fall-through/`null` semantics when moving onto shared primitives.
- **UI regressions are eyeball-only.** Phase 3/5 changes are verified by running the tray app
  (per CLAUDE.md). Theme/PaintKit swaps are value-for-value, so visual output should be identical.
- **Big diffs.** Phases are sequenced so each compiles and ships on its own; Phase 5 (the churny one)
  can be deferred indefinitely without blocking 1–4.

## 6. Decisions (settled)
1. **Appetite / scope → full restructure.** All phases, including splitting `OverlayForm` and
   `SettingsForm` (Phase 5), scheduled last so it can still be deferred without blocking 1–4.
2. **Behaviour policy → preserve by default, fix obvious bugs too.** Latent bugs/inconsistencies
   exposed while consolidating duplication may be fixed in the same work, each in its own commit and
   called out, so the fix reviews separately from the mechanical move.
3. **Namespaces/folders → folders + sub-namespaces.** `ClaudeWatch.Data` / `ClaudeWatch.Ui` /
   `ClaudeWatch.App` with matching `src/` folders. New files born in their final namespace; the
   existing 31 are migrated in Phase 6 (last) to avoid touching files twice.
4. **Tests → add `ClaudeWatch.Tests` (xUnit).** Golden-baseline tests over the pure logic land in
   Phase 0, before the risky transcript-parsing consolidation in Phase 2.
