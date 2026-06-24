# TODO
- figure out how the visualize tasks better (this is turning out to be a bit flakey)
    - we might need to mark it as experimental and figure out how to resolve the PID
    - Maybe hooks can help?
- add the ability to manage and customise statuslines
  - this may actually be a separate app, but I would like to be able to get all the information well formatted and swap between them at will
- Ignore some built in claude commands from showing notification alerts (like /clear /doctor)
- make a nice gif for the readme
- add focus handlers for claude processes opened in non-terminal instances
  - claude itself
  - VSCode
  - Rider

## Quick wins
- session count badge on tray icon — render active session count as a number overlay on the tray icon
- global hotkey — system-wide shortcut (configurable) to show/hide the overlay

## Moderate effort
- modern toast notifications — replace WinForms balloons with Windows 10/11 toasts (action buttons, notification centre history, looks native)
- auto-focus terminal on "Needs Attention" — optionally flash/restore the terminal when a session transitions to needing input
- session aliases/labels — let users attach a short label to a session (stored by session ID), useful when multiple sessions share the same project directory

## Bigger ideas
- daily session stats — accumulate state-change timing from sidecar files; surface "Today: 4 sessions, 3h 12m active" in tray right-click or history viewer
- tray icon hover tooltip — one-liner per session showing current tool and elapsed time, for when the overlay is hidden
- cost/token estimation — rough per-session cost estimate derived from the 5-hour rate-limit delta; token-to-cost math is simple once the model is known