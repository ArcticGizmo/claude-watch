using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeWatch;

/// <summary>
/// Command-line entry point for <c>ClaudeWatch.exe handle &lt;event&gt;</c>. Lets the claude-watch
/// plugin's Claude Code hooks drive the tray app's session sidecar files directly, so all command
/// logic lives here in first-class C# instead of being duplicated in PowerShell.
///
/// Reads the hook's JSON payload from stdin. For <c>prompt</c> events it recognises the /afk and
/// /history commands and writes a hook "block" decision to stdout so they never reach the model;
/// for <c>cleanup</c> (SessionEnd) it removes the session's sidecar files. Any other prompt yields
/// no output and passes through untouched. Never throws — a hook must not break the session.
/// </summary>
internal static partial class CliHandler
{
    private static string SessionsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    // Matches a prompt that *is* one of our commands, optionally namespaced (e.g. /claude-watch:afk),
    // start-anchored so a prompt that merely mentions "/afk" in prose is left alone.
    [GeneratedRegex(@"^/(?:[\w-]+:)?(?<cmd>afk|history)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommandPattern();

    public static int Run(string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        var payload = ReadStdin();
        try
        {
            return sub switch
            {
                "prompt"  => HandlePrompt(payload),
                "cleanup" => HandleCleanup(payload),
                _         => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private static int HandlePrompt(string payload)
    {
        var (prompt, sessionId) = Parse(payload);
        var match = CommandPattern().Match(prompt.Trim());
        if (!match.Success || string.IsNullOrEmpty(sessionId))
            return 0; // not one of ours (or no session id) — let the prompt through

        var cmd = match.Groups["cmd"].Value.ToLowerInvariant();
        string reason = cmd == "afk" ? ToggleAfk(sessionId) : RequestHistory(sessionId);

        // A "block" decision erases the prompt before the model sees it; the reason is shown to the user.
        WriteStdout(JsonSerializer.Serialize(new { decision = "block", reason }));
        return 0;
    }

    // Read the hook payload as UTF-8 (stripping any BOM), independent of the console code page — the
    // tray app is a WinExe so Console.In would otherwise decode with an unpredictable encoding.
    private static string ReadStdin()
    {
        if (!Console.IsInputRedirected)
            return "";
        using var stream = Console.OpenStandardInput();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Write UTF-8 bytes straight to the std handle, again to bypass the WinExe's console encoding.
    private static void WriteStdout(string text)
    {
        using var stream = Console.OpenStandardOutput();
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static int HandleCleanup(string payload)
    {
        var (_, sessionId) = Parse(payload);
        if (string.IsNullOrEmpty(sessionId))
            return 0;
        TryDelete(Path.Combine(SessionsDir, sessionId + ".afk"));
        TryDelete(Path.Combine(SessionsDir, sessionId + ".history"));
        return 0;
    }

    // Toggles this session's external-notification opt-in marker (read by SessionMonitor each scan).
    private static string ToggleAfk(string sessionId)
    {
        if (!IsTrayRunning())
            return "Claude Watch isn't running, so external notifications can't be toggled right now.";

        var marker = Path.Combine(SessionsDir, sessionId + ".afk");
        if (File.Exists(marker))
        {
            TryDelete(marker);
            return "External (ntfy) notifications are now OFF for this session.";
        }
        Directory.CreateDirectory(SessionsDir);
        File.WriteAllText(marker, sessionId);
        return "External (ntfy) notifications are now ON for this session "
            + "(requires external notifications enabled in Claude Watch settings).";
    }

    // Drops the one-shot trigger the running tray app consumes to open its history viewer.
    private static string RequestHistory(string sessionId)
    {
        if (!IsTrayRunning())
            return "Claude Watch isn't running, so the history panel can't be opened.";
        Directory.CreateDirectory(SessionsDir);
        File.WriteAllText(Path.Combine(SessionsDir, sessionId + ".history"), sessionId);
        return "Opening the Claude Watch history panel for this session.";
    }

    private static (string prompt, string sessionId) Parse(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
            string sid = root.TryGetProperty("session_id", out var s) ? s.GetString() ?? "" : "";
            return (prompt, sid);
        }
        catch
        {
            return ("", "");
        }
    }

    // The tray instance is any *other* ClaudeWatch process — this short-lived `handle` invocation is
    // itself named ClaudeWatch, so it must be excluded.
    private static bool IsTrayRunning()
    {
        int me = Environment.ProcessId;
        try
        {
            return Process.GetProcessesByName("ClaudeWatch").Any(p => p.Id != me);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
