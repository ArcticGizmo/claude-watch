# Testing background-shell / sub-agent visualisation

Claude Watch surfaces two kinds of child under a session, drawn as a tree below the parent row:

- **Sub-agents** (Agent/Task tool) — light-blue dot, status `running`.
- **Background shells / "tasks"** (a Bash/PowerShell tool call with `run_in_background: true`) —
  violet dot, status `shell`.

Both are read live from the parent transcript by `SessionChildReader`. A background shell shows up
the moment it is launched and disappears when Claude Code records its completion
(`<task-notification>` … `<status>completed|failed|killed</status>`). Background shells are
display-only: a long-running server can keep showing under a session that has otherwise gone idle.

## Test prompt

Open a **new** Claude Code session in any project, make sure Claude Watch is running, then paste the
prompt below. Watch the overlay: child rows should appear under that session and drop off as each
shell finishes.

---

Help me test a monitoring tool by launching some background shells. Do exactly this, and don't do
anything else:

1. Launch a background shell that runs for about 30 seconds:
   `for i in $(seq 1 15); do echo "short $i"; sleep 2; done` with `run_in_background: true`.
2. Launch a background shell that runs for about 2 minutes:
   `for i in $(seq 1 60); do echo "long $i"; sleep 2; done` with `run_in_background: true`.
3. Launch a third background shell that runs for about 4 minutes:
   `for i in $(seq 1 120); do echo "longest $i"; sleep 2; done` with `run_in_background: true`.

Also spawn one Explore sub-agent that searches this repo for "TODO" comments. It will run as a sub-agent (a separate row) while the background shells run.

After launching all three, tell me their shell IDs and then just wait — do not check their output or
kill them. Let me watch them in the monitoring tool. Each should disappear from the tool as it
finishes (short first, then long, then longest).

---

### Optional: also test a sub-agent at the same time

Add this to the prompt to get a concurrent sub-agent row (light-blue) for comparison:

> Also spawn one Explore sub-agent that searches this repo for "TODO" comments. It will run as a
> sub-agent (a separate row) while the background shells run.

## What to look for

- Three violet `shell` rows appear under the session within a poll or two of launching.
- They vanish one at a time in finish order — no manual `BashOutput`/kill needed.
- If you added the sub-agent, one light-blue `running` row appears and vanishes when it returns.
- The session's own status/notifications are unaffected by the shells (a running shell does not keep
  the parent marked "Running" or suppress its "done" alert).
