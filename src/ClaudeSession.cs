namespace ClaudeWatch;

public enum SessionStatus
{
    Idle = 0,
    Running = 1,
    NeedsAttention = 2,
    AwaitingInput = 3,
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
    DateTime? RunningSince = null
)
{
    /// <summary>Running sub-agents under this session; never null.</summary>
    public IReadOnlyList<SubAgent> SubAgents { get; init; } = SubAgents ?? [];

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
