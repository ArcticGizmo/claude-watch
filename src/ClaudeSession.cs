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

public record ClaudeSession(
    string Pid,
    string SessionId,
    SessionStatus Status,
    string Cwd,
    string ProjectName,
    DateTime LastUpdated,
    PermissionMode Mode = PermissionMode.Normal
);
