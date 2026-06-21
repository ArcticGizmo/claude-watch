# Session / History Viewer — Implementation Plan

A viewer that lets you inspect what is (and was) going on inside Claude Code sessions,
reading the live transcript files on disk.

## Goals (from the request)

- Right-click a session in the overlay to open the history viewer.
- View histories for sessions that are **no longer active** via a dropdown; **active**
  sessions are listed first with an "active" indicator.
- Window ~50% of screen width, with a close ✕ top-right.

## Confirmed decisions (from follow-up questions)

| Topic | Decision |
|---|---|
| **Content** | Two views with a toggle: **Readable** (conversation + one-line tool summaries, expandable) is the default; **Raw** shows the verbatim event timeline. |
| **Dropdown scope** | **All sessions across all projects, newest first.** Active sessions pinned to the top with an "active" indicator; each entry labelled with its project name + relative time. |
| **Rendering** | **Dark custom chrome** (border / title bar / dropdown / toggle / ✕ painted to match the app) wrapping a **standard scrollable text control** for the history body (native scroll, text selection, copy). |
| **Live updates** | For an active session, **auto-refresh while open** — tail the transcript and append new events; auto-scroll to follow (toggleable). |
| **Window behavior** | **Persistent & movable.** Stays open alongside the terminal until closed (✕ / Esc). Movable, resizable, single reused instance. Opens 50% screen width, ~80% height, centered. Re-opening re-focuses the existing window. |
| **Launch points** | Right-click a session row → "View history", **and** a new "Session history…" item in the system-tray menu (opens with the most-recent session selected). |

## Data sources (already understood from the codebase)

- **Live sessions**: `~/.claude/sessions/*.json` — only present while the process runs.
  Parsed today by `SessionMonitor.ReadSession`. Gives us the set of **active** `sessionId`s.
- **History transcripts**: `~/.claude/projects/{enc-cwd}/{sessionId}.jsonl` — one file per
  session, appended live, **persists after the session ends**. This is the source for the
  viewer. cwd is encoded by replacing every non-alphanumeric char with `-`
  (see `TranscriptReader.ResolveTranscript` / `SubAgentReader`).
- **Transcript line shape** (JSONL, one record per line):
  - top-level: `type` (`"user"` | `"assistant"` | `"summary"` | …), `sessionId`, `cwd`,
    `timestamp`, `uuid`, `parentUuid`, `isSidechain`.
  - `message.content`: either a **string** (typical user prompt) or an **array of blocks**:
    `{type:"text"}`, `{type:"thinking"}`, `{type:"tool_use", id, name, input}`,
    `{type:"tool_result", tool_use_id, content}`.
  - Parsing must tolerate partial/malformed trailing lines (file is appended live) and
    unknown record types — skip in Readable, surface in Raw.

## Architecture & new files

### 1. `src/ToolSummary.cs` (new, small refactor)
Extract the tool→phrase mapping currently private in `TranscriptReader`
(`Describe` / `FileLabel` / `Clip`) into a shared `static class ToolSummary` so the
viewer's Readable mode produces the same phrases ("Reading Foo.cs", "Running: npm test").
`TranscriptReader.Describe` becomes a thin call into it (no behavior change).

### 2. `src/SessionHistory.cs` (new — data layer)
Pure, UI-free reading/parsing. Best-effort, never throws.

- `record HistoryEntry(string SessionId, string ProjectName, string Cwd, string Path, DateTime LastUpdated, bool IsActive)`
  - `ListAll(IReadOnlySet<string> activeSessionIds)`: enumerate `~/.claude/projects/*/*.jsonl`,
    derive `ProjectName` (last segment of `cwd` read from the first/last record, falling back
    to the decoded dir name), `LastUpdated` from file mtime, `IsActive` from the active set.
    Sort: **active first, then `LastUpdated` desc.**
- `enum HistoryEventKind { UserText, AssistantText, Thinking, ToolCall, Meta }`
- `record HistoryEvent(HistoryEventKind Kind, DateTime? Timestamp, string Summary, string Detail, string? ToolUseId, bool IsSidechain, string RawLine)`
  - `Detail` for a `ToolCall` holds the pretty-printed input and, once matched, the tool
    result (joined by `tool_use_id`).
- `List<HistoryEvent> Parse(string path)`:
  - First pass collects `tool_result`s by `tool_use_id`; tool calls render a one-line
    `Summary` (via `ToolSummary`) and stash full input + matched result in `Detail`.
  - User string content → `UserText`; assistant `text` → `AssistantText`; `thinking` →
    `Thinking`; tool_use → `ToolCall`. Unknown/`summary` → `Meta` (Raw only).
  - Keep `RawLine` for the Raw view.
  - For efficiency on big/active files, support incremental parse from a byte offset so
    auto-refresh appends rather than re-reading the whole file.

### 3. `src/HistoryViewerForm.cs` (new — the window)
WinForms `Form`, `FormBorderStyle.None`, dark palette mirroring `OverlayForm`/`QrCodeForm`.

- **Chrome** (custom-painted): title "Session history", ✕ top-right (hover brightens, like
  `QrCodeForm`), a project/session **dropdown**, a **Readable | Raw** segmented toggle, and a
  **follow / auto-scroll** indicator. A thin draggable title strip repositions the window.
- **Body**: a `RichTextBox` (read-only, dark theme, `BorderStyle.None`) — gives native
  scrolling, selection, copy, and per-run color/bold formatting.
  - **Readable**: user/assistant turns with colored role labels; tool calls as one-line
    summaries. Expand/collapse a tool call to reveal full input + result inline — tracked via
    an `_expanded` `HashSet<toolUseId>`; clicking a summary line hit-tests
    (`GetCharIndexFromPosition` → event range) and re-renders. "Expand all / Collapse all"
    affordance in the chrome. Sidechain (sub-agent) lines indented.
  - **Raw**: each `RawLine` (or its salient fields) verbatim, in timestamp order.
  - Re-render preserves scroll position unless **follow** is on, in which case it scrolls to
    bottom on new content.
- **Resizing with borderless chrome**: override `WndProc`/`WM_NCHITTEST` to return the edge
  hit-codes (`HTLEFT`/`HTRIGHT`/`HTBOTTOM`/`HTBOTTOMRIGHT`/…) so Windows performs native
  resize while the window stays borderless and dark.
- **Sizing**: on open, 50% of `Screen.WorkingArea.Width`, ~80% height, centered; remembers
  user moves/resizes for the session (single reused instance).
- **Dismiss**: ✕ button and Esc. Does **not** close on click-away (unlike `QrCodeForm`).
- **Auto-refresh**: a `FileSystemWatcher` scoped to the **selected** transcript's directory,
  filtered to its filename, debounced (~150 ms, same pattern as `SessionMonitor`). Re-created
  when the dropdown selection changes. Inactive sessions simply never fire.
- **Public surface** for the context to drive active-state:
  - `void SetActiveSessions(IReadOnlyList<ClaudeSession> sessions)` — refresh the dropdown's
    active indicators and ordering when sessions change while the window is open.
  - `void SelectSession(string sessionId)` — used by the launch points.

### 4. `src/OverlayForm.cs` (wire the right-click)
- Add `public event Action<string>? HistoryRequested;` (carries `sessionId`).
- In `ShowContextMenuAt`, for any **session row** (and sub-agent rows resolved to their parent
  session, mirroring the existing click→parent rule), add a **"View history"** item that
  invokes `HistoryRequested?.Invoke(session.SessionId)`.

### 5. `src/OverlayApplicationContext.cs` (own & launch the window)
- Add `private HistoryViewerForm? _historyForm;` managed exactly like `_settingsForm`
  (lazy create, reuse, re-focus, null on `FormClosed`).
- `OpenHistoryViewer(string? sessionId)`: create/reuse the form, seed it with the current
  live sessions (active set) via `SetActiveSessions`, then `SelectSession` (or pick the
  most-recent entry when `sessionId` is null).
- Subscribe `_overlay.HistoryRequested += sid => OpenHistoryViewer(sid);`.
- Add a **"Session history…"** `ToolStripMenuItem` to the tray `ContextMenuStrip` (between
  "Settings…" and "Check for Updates…") → `OpenHistoryViewer(null)`.
- In `OnSessionsChanged`, if `_historyForm` is open, call
  `_historyForm.SetActiveSessions(sessions)` so active indicators stay current.
- Close/dispose `_historyForm` in `Exit()` / `Dispose()` alongside `_settingsForm`.

## Edge cases & notes

- **No transcript found** for a session id (e.g. brand-new session): show an empty-state
  message in the body; the dropdown still lists everything else.
- **Large transcripts**: incremental append-parse on refresh; the initial full read is a
  deliberate user action so a one-time parse is acceptable. Flag if any file proves huge.
- **Active detection** is by `sessionId` ∈ live sessions; an entry can flip active→inactive
  live (handled by `SetActiveSessions`).
- **Best-effort throughout**: any IO/JSON failure degrades to empty/partial, never crashes —
  consistent with `TranscriptReader`/`SubAgentReader`.
- Each form defines its own palette constants today; the viewer follows that convention
  (duplicated dark palette) rather than introducing a shared theme file.

## Build / verify

- `dotnet build src/ClaudeWatch.csproj` (net10.0-windows).
- Manual: run the app with at least one active session; right-click it → "View history";
  confirm live append + auto-scroll; switch Readable/Raw; pick an inactive session from the
  dropdown; resize/move; close via ✕ and Esc; open via tray "Session history…".

## Open / deferred (not blocking)

- Search/find within a transcript (Ctrl+F) — could lean on RichTextBox's `Find`.
- Per-event copy / "copy as markdown".
- Token/cost surfacing if present in records.
