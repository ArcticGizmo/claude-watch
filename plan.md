# Plan: Fix Background Shell Running-State Detection

## Context

Background shell detection (`SessionChildReader.cs`) has two confirmed inaccuracies:

- **False negatives** — long-running shells with infrequent output disappear from the overlay before they finish. The culprit is a 5-minute stale-output-file cutoff that was meant as an orphan-cleanup fallback but is too aggressive.
- **False positives** — shells that end without a task-notification (e.g. force-killed process, mid-run crash) linger in the running list indefinitely when their output file was never created, because `IsOutputFileStale` only returns `true` when the file *exists*.

A third robustness issue: the shell ID is extracted from the spawn-result content string via regex (`BackgroundIdRegex`). The JSONL record already carries a structured `toolUseResult.backgroundTaskId` field that contains the same ID without text parsing.

Sub-agent detection is unaffected and should not be changed.

---

## Changes — all in `src/SessionChildReader.cs`

### 1. Use `toolUseResult.backgroundTaskId` as the primary shell-ID source

When a `user` record contains a tool_result for a shell launch, the outer record also has:

```json
"toolUseResult": { "backgroundTaskId": "bmy2hhnii", ... }
```

Before the content-block loop, read this field:

```csharp
var bgTaskId = node["toolUseResult"]?["backgroundTaskId"]?.GetValue<string>();
```

Inside the `type == "tool_result"` branch, prefer it over the regex:

```csharp
if (shellUses.ContainsKey(rid))
{
    if (!string.IsNullOrEmpty(bgTaskId))
        shellIdByUseId[rid] = bgTaskId;
    else
    {
        var text = ResultText(block["content"]);
        var m = BackgroundIdRegex.Match(text);
        if (m.Success) shellIdByUseId[rid] = m.Groups[1].Value;
    }
}
```

### 2. Increase the stale-file timeout from 5 to 30 minutes

```csharp
// before:
var staleCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
// after:
var staleCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(30);
```

This fixes false negatives for shells that legitimately run silently for more than 5 minutes. 30 minutes is still short enough to eventually clean up truly orphaned shells (those whose Claude Code process is still alive but the shell was force-killed without a task-notification).

### 3. Add instance-level "first-seen" tracking to catch no-output-file false positives

`IsOutputFileStale` currently returns `false` when the output file does not exist — this leaves orphaned shells running forever if no file was ever created.

Add to the `SessionChildReader` instance:

```csharp
private readonly Dictionary<string, DateTime> _shellFirstSeen = new();
```

In `GetRunning()`, after calling `Parse()`, update first-seen times and apply a secondary cleanup pass:

```csharp
var result = Parse(path);

// Maintain first-seen timestamps for each running shell.
var now2 = DateTime.UtcNow;
foreach (var shell in result.Shells)
    _shellFirstSeen.TryAdd(shell.Id, now2);

// Remove entries for shells that are no longer in the running list.
foreach (var key in _shellFirstSeen.Keys.Except(result.Shells.Select(s => s.Id)).ToList())
    _shellFirstSeen.Remove(key);

// Retire shells with no output file that have been "running" for > 30 minutes —
// their output file was never created, so the stale-file check can't fire.
if (tasksDir != null)
{
    var filtered = result.Shells.Where(shell =>
    {
        if (!_shellFirstSeen.TryGetValue(shell.Id, out var firstSeen)) return true;
        if ((now2 - firstSeen).TotalMinutes < 30) return true;
        var outputFile = Path.Combine(tasksDir, shell.Id + ".output");
        return File.Exists(outputFile); // keep if file exists; stale-file check handles it
    }).ToList();

    if (filtered.Count != result.Shells.Count)
        result = new SessionChildren(result.SubAgents, filtered);
}
```

`tasksDir` is currently computed inside `Parse()`. Move the `TasksDir(path)` call to before the cache check in `GetRunning()` so it is available both for the cleanup pass above and can be passed into `Parse()`.

---

## Files changed

- `src/SessionChildReader.cs` — all three changes above

---

## Verification

1. Run the test prompt from `docs/test-background-shells.md` (three shells: 30s, 2min, 4min).
   - All three should appear immediately as shell rows.
   - The 30s shell should disappear ~30s after launch; the others follow in order.
2. Test a silent long-running shell (e.g. `sleep 300`) — it should remain visible for the full 5 minutes and disappear only after a task-notification or the 30-minute fallback.
3. Verify that killing Claude Code mid-run and restarting does not leave ghost shell rows from the previous run (since the old session process is dead, `IsProcessRunning` removes it entirely).
