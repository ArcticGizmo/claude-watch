namespace ClaudeWatch;

public enum SessionStatus
{
    Idle = 0,
    Running = 1,
    NeedsAttention = 2,
}

public record ClaudeSession(
    string Pid,
    string SessionId,
    SessionStatus Status,
    string Cwd,
    string ProjectName,
    DateTime LastUpdated
);
