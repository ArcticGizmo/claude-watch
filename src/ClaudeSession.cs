namespace ClaudeWatch;

public enum SessionStatus
{
    Idle = 0,
    Running = 1,
    NeedsAttention = 2,
    AwaitingInput = 3,
}

/// <summary>The kinds of desktop notification Claude Watch raises for a session, each with its
/// own settings toggle. "Done" = work finished; "WaitingForInput" = blocked on a prompt.</summary>
public enum NotificationKind
{
    Done = 0,
    WaitingForInput = 1,
}

public enum PermissionMode
{
    Normal = 0,
    AcceptEdits = 1,
    Plan = 2,
    Auto = 3,
    Bypass = 4,
}

/// <summary>
/// A Claude Code sub-agent (Task tool) currently running under a parent session.
/// Sub-agents have no session file of their own — they execute in the parent's
/// process and are surfaced only via the parent's transcript (see SubAgentReader).
/// </summary>
public record SubAgent(
    string AgentId,      // tool_use id of the Task that launched it (stable per invocation)
    string Description,  // the Task's short description, used as the row label
    string AgentType     // subagent_type, e.g. "general-purpose", "Explore"
);

public record ClaudeSession(
    string Pid,
    string SessionId,
    SessionStatus Status,
    string Cwd,
    string ProjectName,
    DateTime LastUpdated,
    PermissionMode Mode = PermissionMode.Normal,
    IReadOnlyList<SubAgent>? SubAgents = null,
    string? Activity = null,
    DateTime? RunningSince = null,
    string? BridgeSessionId = null,
    bool ExternalNotify = false
)
{
    /// <summary>Running sub-agents under this session; never null.</summary>
    public IReadOnlyList<SubAgent> SubAgents { get; init; } = SubAgents ?? [];

    /// <summary>
    /// True while this session is connected to the mobile app / claude.ai via /remote-control —
    /// i.e. its session file carries a <c>bridgeSessionId</c>. That id is also the deep-link target
    /// encoded into the QR code (https://claude.ai/code/{BridgeSessionId}).
    /// </summary>
    public bool RemoteControlled => !string.IsNullOrEmpty(BridgeSessionId);

    /// <summary>
    /// True when this session has opted in to external (ntfy) notifications — i.e. its session file
    /// has a sibling <c>{sessionId}.notify</c> marker. The marker is the single source of truth,
    /// written/removed both by the overlay's right-click toggle and the plugin's <c>/afk</c> command.
    /// </summary>
    public bool ExternalNotify { get; init; } = ExternalNotify;

    /// <summary>
    /// How long this session has been continuously running, as a compact label showing only the
    /// most significant unit ("8s", "3m", "2h"). Null when the session isn't running.
    /// </summary>
    public string? RunningElapsedLabel()
    {
        if (RunningSince is not { } start)
            return null;

        var elapsed = DateTime.Now - start;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h";
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m";
        return $"{(int)elapsed.TotalSeconds}s";
    }
}
