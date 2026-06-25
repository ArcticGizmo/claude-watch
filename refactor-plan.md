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

### Phase 0 — Test project & golden baselines — ✅ DONE
1. Added `tests/ClaudeWatch.Tests/` (xUnit, `net10.0-windows`), registered in `claude-watch.slnx`,
   referencing `src/ClaudeWatch.csproj`. Packages (`Microsoft.NET.Test.Sdk`, `xunit`,
   `xunit.runner.visualstudio`) are all in the local NuGet cache, so it restores offline.
2. **60 golden/characterization tests** pinning the current behaviour of every pure-logic target of
   Phase 2: `ModelContext`, `ToolSummary`, `TranscriptLocator`, `TranscriptParser`, `TranscriptReader`,
   `SubAgentReader`, `SessionStatsService` (per-file `ParseSession`, `ActiveSpan`, `CostOf`, and the
   public `ReportAllTime` pipeline).
3. Fixtures are **synthetic**, PII-free `.jsonl` transcripts authored to the real Claude Code schema
   (tool_use/result stitching, usage, `/model` switch, custom-title, artifact publish, legacy + 2.1
   sub-agent layouts, bare-command, scan-fallback). Captured-from-real transcripts were avoided
   deliberately (PII policy); synthetic fixtures are also deterministic and tz-independent.
4. Test seams: `InternalsVisibleTo("ClaudeWatch.Tests")` on the main project; a handful of `internal`
   widenings in `SessionStatsService`; and a module initializer that points the data layer at the
   fixture tree via `CLAUDE_CONFIG_DIR`.
5. Drive-by (policy §6.2): `ClaudePaths.ClaudeDir` now honours the **`CLAUDE_CONFIG_DIR`** environment
   variable (which Claude Code itself respects) — a real improvement for users with a relocated config,
   behaviour-identical when unset, and the seam that makes the data layer testable.
6. Verified: full solution builds clean (0 warnings); `dotnet test` → 60 passed, 0 failed.

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

### Phase 2 — Transcript reading primitives *(medium risk — the riskiest data work)* — ✅ DONE
1. Added `MtimeCache<T>` (`src/Data/MtimeCache.cs`); collapsed the 5 `TranscriptReader` caches and the
   2 `SubAgentReader` caches (7 hand-rolled cache dicts + entry records) onto it.
2. Added `TranscriptScan` (`src/Data/TranscriptScan.cs`) — `OpenShared` / `ReadLines` / `ReadLinesFrom`
   / `ReadTailLines`, the shared "open `FileShare.ReadWrite` + seek tail + drop partial first line"
   boilerplate every parser repeated.
3. Added `TranscriptJson` (`src/Data/TranscriptJson.cs`): unified `TokenLong`≡`LongOf` → `AsLong`,
   the three `ParseTimestamp` copies → one, plus `ContentArray` / `BlockType` block accessors.
4. Refactored `TranscriptReader`, `SubAgentReader`, `SessionStatsService`, and `TranscriptParser` onto
   the three primitives — each keeps its own pre-filter strings and domain mapping, so the scan logic
   is unchanged. (Left alone, by design: `TranscriptParser.Ingest`'s incremental byte-offset tailing,
   `SessionHistory.ResolveProject`'s bounded head-peek, and `UsageMonitor`'s non-transcript reads — all
   different access patterns, not this abstraction.)
5. Verified: the 60-test Phase 0 golden baseline still passes unchanged (artifact de-dup, bare-command,
   context-fill, stats counts/tokens/cost all intact); full solution builds clean (0 warnings).

### Phase 3 — UI foundation *(low risk, high consistency payoff)* — ✅ CORE DONE
Verification note: the overlay/windows are owner-drawn and can only be verified by eye (no UI tests),
so this phase was scoped to **provably value-preserving** changes — identical geometry, identical
ARGB, identical resource loading — committed in small increments (3a, 3b).

Done:
1. **`PaintKit`** (`src/Ui/PaintKit.cs`) — rounded-rect path + pill-bar fill; replaced the ~8 copies
   across `OverlayForm`, `PopoverMenu`, `UsageTooltipForm`, `QrCodeForm`, `StatsForm`, `SettingsControls`. *(3a)*
2. **`EmbeddedResources`** (`src/Ui/EmbeddedResources.cs`) — `LoadBitmap`/`LoadText`; replaced the
   4 `LoadEmbeddedBitmap` + 1 `LoadEmbeddedText`. *(3a)*
3. **Cross-file colour-function dedup** — `OverlayForm`'s `ModeColor`, `Blend`, and `UsageColor` were
   byte-for-byte duplicates of `Theme.ModeColor`/`Theme.Blend`/`Theme.UsageColor`; removed and pointed
   at `Theme`. *(3b)*
4. **`Glyphs`** (`src/Ui/Glyphs.cs`) — the permission-mode badge was drawn (bar the size) in both
   `OverlayForm` and `SettingsControls.ModeLegend`; unified into one size-parameterised painter. *(3b)*

Deferred (deliberately — these carry visual-only risk on an eyeball-only surface, or are better placed
elsewhere):
- **`UsageBarRenderer`** — the overlay and settings usage bars share ~50 lines but differ in widths,
  fonts, and dim-shades; a shared renderer is value-preserving only if every differing value is
  threaded through, and the result needs a real visual check. Worth doing with a human eyeball.
- **`ToolWindow` base form** — the three popups differ in activation/ex-style behaviour; this is a
  form-structure change, folded into **Phase 5** (form decomposition).
- **Full palette migration** — folding every window's private shade into `Theme` mostly preserves
  distinct values under new names (low dedup value) and flattens the overlay's readable semantic
  names; not pursued. The genuine duplicates (the colour *functions* above) are gone.

### Phase 4 — App orchestration *(medium risk)* — ✅ DONE
1. **`UiDispatch`** (`src/Ui/UiDispatch.cs`) — `Post` / `RunThenPost`, the off-thread→guarded-marshal
   pattern; applied at the 4 sites (`OverlayApplicationContext.RefreshUsage`/`RefreshTodayStats`,
   `StatsForm.RefreshStats`, `HistoryViewerForm.RefreshEntries`). *(4a)*
2. **`NotificationService`** (`src/App/NotificationService.cs`) — extracted balloon + chime + ntfy +
   AFK/lock gating + the last-notified-pid state out of `OverlayApplicationContext` (~120 lines). The
   context's attention handlers now flash the overlay and call `Notify`; update/plugin flows raise
   balloons via `ShowInfo`. *(4b)*
3. **`WindowHost.ShowOrFocus`** (`src/App/WindowHost.cs`) — collapsed the three reused-window methods
   (`OpenSettings`/`OpenHistoryViewer`/`OpenStats`) onto one helper (`beforeShow` for one-time wiring,
   `refresh` for "point at current data", run on both reuse and create paths). `StatsForm` no longer
   self-loads in `OnShown` — the `WindowHost` refresh kicks the first load on open. Exit/Dispose/update
   teardown wiring is unchanged. *(4c)*
4. Verified: behaviour-preserving; Release build clean + 60/60 tests throughout (the running app held a
   lock on the Debug exe, so verification used the Release config).

> Note: `UiDispatch` was placed in `ClaudeWatch.Ui` rather than `.App` — it is UI-thread plumbing used
> by the forms themselves, so it belongs below the App layer to avoid a Ui→App dependency.

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
