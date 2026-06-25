# Changelog

All notable changes to Claude Watch are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

---

## [v0.0.40] - 2026-06-25

- Background sub-agents — now show up as child rows in the overlay, like ordinary sub-agents.

---

## [v0.0.39] - 2026-06-25

- "Check for Updates" now says "Checking for updates…" the moment you click it, instead of sitting silent until GitHub answers

---

## [v0.0.38] - 2026-06-25

- Another go at making the plugin install actually stick — it was installing fine, then quietly refusing to load (a duplicate hooks declaration the newer Claude Code stopped tolerating)
- Install and update messages now point you at `/reload-plugins`, rather than just telling you to restart your sessions

---

## [v0.0.37] - 2026-06-25

- Fast built-in commands like `/clear`, `/model`, and `/doctor` no longer set off a "done" (or "waiting for input") alert — if Claude didn't actually do any work, you won't get pinged for it
- Plugin installs at user scope, so it follows you across every project

---

## [v0.0.36] - 2026-06-25

- Context-pressure gauge — see a warning when your context window is about to boil over

---

## [v0.0.35] - 2026-06-24

- Sessions you've renamed with Claude Code's `/rename` now show that name in the overlay, notifications, and QR window — instead of the bare project folder

---

## [v0.0.34] - 2026-06-24

- Focus sessions running in VS Code's integrated terminal, not just standalone terminal windows
- Picks the correct project window when one VS Code hosts several (previously pot luck)

---

## [v0.0.33] - 2026-06-24

- New Session stats window — today's sessions, active time, prompts and tool calls, plus token totals, an equivalent API cost, an hourly activity heatmap, and breakdowns by project, tool, model, and git branch
- Switch between Today, the last 7 or 30 days, and all time, with a daily activity trend, a day-streak counter, and records (busiest day, longest single session)
- "Today: N sessions · 3h 12m active" now shows in the tray right-click menu — read straight from your transcripts, so there's history from the moment you install it
- A Session Stats settings page to hide the tray line, hide the cost, or tune the idle threshold that decides what counts as "active"
- Cost is labelled "equivalent API cost" — what the tokens would have cost pay-as-you-go, not a bil

---

## [v0.0.32] - 2026-06-24

- Optional chime when a session needs you — the built-in Windows sound, off by default, opt in per notification type
- External (ntfy) alerts stay silent; the chime is for the desktop in front of you
- Fixed settings buttons clipping their text at the bottom on scaled displays (they were holding their breath)


## [v0.0.31] - 2026-06-24

- Configurable Quick Links — shortcut to any app, not just GitKraken and Slack (existing settings carry over)
- Quick-link icons now pulled from the app itself, so Store apps like Slack show their real logo
- Quick-link editor previews the app it found as you type
- Snappier overlay (apps and icons are now cached)

## [v0.0.30] - 2026-06-23

- Added this changelog (the one you are reading)
- View the changelog in-app, no leaving required

## [v0.0.29] - 2026-06-23

- Agent colour changed to purple (correct all along)
- Right-click to copy session ID
- Open session transcript directly from the UI
- Clickable links in session view
- Quick Links — formerly "Integrations", a name that lasted until it didn't
- GitKraken and Slack added as quick-link targets
- Claude Watch now always starts expanded
- Added expected rate display to usage limits

## [v0.0.28] - 2026-06-23

- Auto-close now shows a countdown so you can watch your fate approach

## [v0.0.27] - 2026-06-23

- Updates made "a little less buggy" (source: commit message; exact improvement unquantified)

## [v0.0.26] - 2026-06-23

- Fixed auto-close not actually closing anything

## [v0.0.25] - 2026-06-22

- Sessions can now be automatically started and stopped

## [v0.0.24] - 2026-06-22

- Settings form completely reworked — new layout, more coherent, larger

## [v0.0.23] - 2026-06-22

- Reworked the invoke mechanism
- Dock overlay to the left side (right-side supremacy: contested)
- Increased default padding so things breathe

## [v0.0.22] - 2026-06-22

- Added ability to dock to the left side

## [v0.0.21] - 2026-06-22

- Increased default padding

## [v0.0.20] - 2026-06-22

- Made the remote control icon more noticeable (it was there before, quietly)

## [v0.0.19] - 2026-06-22

- Added auto plugin for automatic session management

## [v0.0.18] - 2026-06-22

- Added singleton enforcement — one instance, no negotiations
- Renamed the executable to be more CLI-friendly
- Plugin consolidation — fewer moving parts
- Added in-app configuration settings
- Immediately reverted in-app configuration settings (a bold 3-minute experiment)

## [v0.0.17] - 2026-06-21

- Send notifications when the machine is locked
- ntfy notifications now include a direct link to the remote session

## [v0.0.16] - 2026-06-21

- Added link to remote session in ntfy notification

## [v0.0.15] - 2026-06-21

- **History viewer** — browse past session transcripts with markdown rendering and clickable images

## [v0.0.14] - 2026-06-21

- **External notifications via ntfy.sh** — push alerts to your phone, with QR code setup

## [v0.0.13] - 2026-06-21

- **Remote control** — generate a QR code to control sessions from another device
- Notification settings
- Hid git worktrees from the session list (they are not sessions; they are a trap)

## [v0.0.12] - 2026-06-20

- Settings UI — all configuration in one place

## [v0.0.11] - 2026-06-20

- Dense mode — compact layout for the minimalists among us

## [v0.0.10] - 2026-06-20

- Removed taskbar sprites (they had a good run)
- Plan mode icon made consistent and less confusing

## [v0.0.9] - 2026-06-20

- Session limits — cap how many sessions run concurrently

## [v0.0.8] - 2026-06-20

- Elapsed time shown per session

## [v0.0.7] - 2026-06-20

- Live activity indicator per session

## [v0.0.6] - 2026-06-20

- Subagent display — sub-agents now surface alongside their parent sessions

## [v0.0.5] - 2026-06-19

- Plugin system introduced
- Permission monitor hooks — write-mode and cleanup-mode scripts

## [v0.0.4] - 2026-06-19

- Event-driven session state (no more polling for what's changed)
- "Needs attention" detection — properly identifies when Claude is waiting on you
- Clicking a notification now opens the relevant Claude instance

## [v0.0.3] - 2026-06-19

- Updated application icon

## [v0.0.2] - 2026-06-18

- Fixed dragging the overlay preventing subsequent clicks

## [v0.0.1] - 2026-06-16

- Velopack integration for auto-update releases

---

## [Pre-release] - 2026-06-12 to 2026-06-15

Before versioning was a concept we took seriously.

- Initial floating overlay implementation
- One square per session — humble, correct
- Drag to reposition (header only, after learning why that matters)
- Tooltip on hover; right-aligned; no bottom gap
- Clicking the overlay correctly focuses the terminal
- Session sprites: started with ducks, added cats, then quietly removed the word "cats" from the filenames (the cats remained and were never discussed again)
- Sessions ordered by name
- Swappable sprite sets
- Transparent overlay made clickable
- Permission mode support — indicators reflect Claude's current permission level
- Wolfenstein-inspired icons for permission mode (this is in the git history and cannot be undone)
- Updated app icon
