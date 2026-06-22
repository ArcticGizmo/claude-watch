# Settings rework — side navigation

## Goal

The settings window has grown into one long vertically-stacked, scrolling page
(`SettingsForm.cs`). Break it into a left side-nav + a content area, with five
nav items. This is a UI/layout refactor only — no behavioural changes to
monitoring, notifications, usage, or the plugin.

## Decisions (confirmed with user)

- **Permission Mode** informational content (the badge legend + explanation)
  moves into **Getting Started** as part of the feature overview. It does *not*
  get its own nav item.
- The window becomes **resizable**: fixed-width left nav, the content area grows
  and scrolls.
- Banner tagline: **"Never miss what Claude's working on"**.

## Nav structure & content mapping

| Nav item            | Content (source today)                                                                                   |
|---------------------|----------------------------------------------------------------------------------------------------------|
| **Getting started** | New logo banner + tagline; plain-language feature summary; the permission-mode legend (`ModeLegend`) + its explanation moved out of `BuildPermissionSection`. |
| **Plugin Control**  | `BuildPluginSection` (status, Enable/Update button + spinner, manual commands + copy). Expanded copy describing what the plugin unlocks: `/afk`, `/history`, and the live permission-mode badges in the overlay. |
| **Usage**           | `BuildUsageSection` moved as-is (toggle, `UsageBarsControl`, Refresh).                                    |
| **Notifications**   | `BuildNotificationsSection` **and** `BuildExternalSection` combined onto one page (Windows desktop notifications up top, ntfy "External notifications" below, separated by a `Separator()`). |
| **About**           | `BuildAboutSection` (logo/version, GitHub links) + `BuildUpdatesSection` (version line, Check for Updates). |

Landing page on open: **Getting started**.

### Getting started — feature summary (user-facing, no implementation detail)

Short bullets drawn from the actual feature set:
- See every active Claude Code session in one floating overlay — Idle, Running,
  or Needs Attention at a glance. Click to jump to a session; drag to dock it
  left or right.
- Get a desktop notification the moment a session finishes or needs your input.
- Push those same alerts to your phone (via ntfy) so you're covered when you're
  away from your desk.
- Track your 5-hour and weekly usage limits without leaving your desktop.
- Install the companion Claude Code plugin for permission-mode badges, `/afk`,
  and `/history`.

(Permission-mode badge legend + one-line explanation follows, reusing
`ModeLegend`.)

## Implementation approach (`src/SettingsForm.cs`)

This is contained almost entirely within `SettingsForm.cs`. The constructor
signature and all public surface stay **unchanged**, so
`OverlayApplicationContext.OpenSettings()` (and the wired events
`UsageEnabledChanged`, `CheckForUpdatesRequested`, `TestNotificationRequested`,
`ExternalNotificationsEnabledChanged`, `TestExternalNotificationRequested`,
`UpdateUsage`) need no edits.

### 1. Window chrome
- `FormBorderStyle = FormBorderStyle.Sizable`, `MaximizeBox = true`.
- `MinimumSize ≈ 640×520`; default `ClientSize ≈ 760×640`.
- Keep dark title bar (`OnHandleCreated`).

### 2. Shell layout (replaces the single `_root` flow panel in `BuildLayout`)
- A nav `Panel` docked `Left` (fixed width ~170px), background slightly darker
  than `Theme.FormBg` to read as a sidebar; nav buttons stacked top-down.
- A content host `Panel` docked `Fill`.
- One page panel per nav item: `FlowLayoutPanel { FlowDirection = TopDown,
  WrapContents = false, AutoScroll = true, Dock = Fill, Padding = 16 }`. All five
  are built once and held in a `Dictionary<string, FlowLayoutPanel>`; switching
  nav toggles `Visible` (only the active page visible). Dark scrollbars applied
  per page in `OnShown`.

### 3. Nav buttons
- Small helper building a flat, full-width, left-aligned button per item with a
  hover background and a "selected" state (accent left-bar / highlighted
  background using `Theme.Accent`).
- Clicking sets the active page and restyles the buttons. Track
  `(string key, Button btn)` list + a `_currentPage` field.

### 4. Fluid width (the main new work, because the window is resizable)
Today every spanning control hardcodes `const int ContentWidth = 607` and the
custom rows absolutely-position a right-anchored toggle. To make content reflow
on resize:
- Replace the `ContentWidth` constant with a per-page computed width =
  `contentHost.ClientWidth − padding − scrollbar allowance`.
- Maintain a per-page list of "full-width" controls (the `TextBox`es, `BodyText`
  labels' `MaximumSize`, `UsageBarsControl`, `Separator`s, and the custom row
  `Panel`s for notify / lock / remote / `TitleRow`). On the content host's
  `Resize`, walk the active page's list and set each control's width (and
  `MaximumSize` for wrapped labels) to the available width.
- The right-anchored toggles inside the row panels already use
  `AnchorStyles.Top | Right`, so re-sizing the parent panel keeps them pinned —
  only the panel widths need updating.

### 5. Section helpers
- `SectionTitle` / `TitleRow` / `BodyText` / `FieldCaption` / `MakeTextBox` /
  `CodeBlock` / `Separator` / `ButtonRow` / `MakeButton` / `LinkRow` are reused
  as-is, with `ContentWidth` references swapped for the dynamic width.
- The `Build*Section` methods are lightly refactored to add their controls to a
  passed-in page panel instead of the single root, and to register their
  full-width controls for the resize pass.

### 6. Delete / move
- `BuildPermissionSection` is removed; its `ModeLegend` + explanatory `BodyText`
  move into the Getting started page builder. The "Manage it in the … section
  below" sentence is dropped (no longer a single scroll).

## Files touched
- `src/SettingsForm.cs` — all of the above. (No other files require changes;
  `OverlayApplicationContext.cs` wiring is untouched.)

## Out of scope
- No new settings/persisted values.
- No changes to the overlay, monitors, plugin manager, or notification logic.
- No new embedded assets — the existing `icon.png` is reused for the banner.

## Verification
- `dotnet run --project src`, open settings from the tray.
- Click through all five nav items; confirm each shows the right content and the
  landing page is Getting started.
- Resize the window (and maximize): nav stays fixed-width, content reflows, text
  wraps, toggles stay right-pinned, tall pages scroll with dark scrollbars.
- Exercise live behaviour to confirm no regressions: usage toggle/refresh,
  notification master + per-type toggles and Test, ntfy host/topic/Generate/QR/
  lock/remote toggles + Send test, plugin Enable/Update, Check for Updates,
  GitHub links.
