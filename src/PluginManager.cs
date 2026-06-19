namespace ClaudeWatch;

using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>
/// Health of the permission-monitor plugin that feeds <c>{session_id}.mode</c> files
/// to <see cref="SessionMonitor"/>. Ordered roughly worst → best for tray colour mapping.
/// </summary>
internal enum PluginHealth
{
    /// <summary>The <c>claude</c> CLI could not be located on PATH.</summary>
    CliMissing,
    /// <summary>CLI present, but neither the marketplace nor the plugin is registered.</summary>
    NotPresent,
    /// <summary>Marketplace added but the plugin is not installed.</summary>
    MarketplaceAdded,
    /// <summary>Plugin installed but currently disabled.</summary>
    Disabled,
    /// <summary>Plugin installed and enabled — detection is live.</summary>
    Healthy,
}

/// <summary>
/// Drives the Claude Code CLI to add the claude-watch marketplace and install the
/// <c>permission-monitor</c> plugin, and reports its health. The plugin writes each
/// session's live permission mode to <c>~/.claude/sessions/{id}.mode</c>, which
/// <see cref="SessionMonitor"/> already reads — so this class only manages install/health,
/// never the mode files themselves.
/// </summary>
internal sealed class PluginManager
{
    // The repo doubles as the marketplace (see .claude-plugin/marketplace.json). The
    // marketplace *name* comes from that file's "name" field, not the repo slug.
    private const string MarketplaceRepo = "ArcticGizmo/claude-watch";
    private const string MarketplaceName = "claude-watch";
    private const string PluginName      = "permission-monitor";
    private const string PluginRef       = PluginName + "@" + MarketplaceName;

    /// <summary>The slash commands a user can paste into a session if the CLI isn't on PATH.</summary>
    public static string FallbackCommands =>
        $"/plugin marketplace add {MarketplaceRepo}\n/plugin install {PluginRef}";

    /// <summary>One-line description of a health state for the tray menu.</summary>
    public static string Describe(PluginHealth health) => health switch
    {
        PluginHealth.CliMissing       => "claude CLI not found on PATH",
        PluginHealth.NotPresent       => "not installed",
        PluginHealth.MarketplaceAdded => "marketplace added — plugin not installed",
        PluginHealth.Disabled         => "installed but disabled",
        PluginHealth.Healthy          => "active — detection live",
        _                             => "unknown",
    };

    /// <summary>Inspects the CLI's view of marketplaces and plugins to derive current health.</summary>
    public async Task<PluginHealth> GetHealthAsync()
    {
        var probe = await RunClaudeAsync("--version");
        if (probe.exitCode != 0)
            return PluginHealth.CliMissing;

        var (pluginInstalled, pluginEnabled) = await ReadPluginStateAsync();
        if (pluginInstalled)
            return pluginEnabled ? PluginHealth.Healthy : PluginHealth.Disabled;

        return await IsMarketplaceAddedAsync()
            ? PluginHealth.MarketplaceAdded
            : PluginHealth.NotPresent;
    }

    /// <summary>Adds the marketplace (idempotent) then installs the plugin. Returns a user-facing message.</summary>
    public async Task<(bool ok, string message)> InstallAsync()
    {
        if ((await RunClaudeAsync("--version")).exitCode != 0)
            return (false, "claude CLI not found on PATH.");

        if (!await IsMarketplaceAddedAsync())
        {
            var add = await RunClaudeAsync($"plugin marketplace add {MarketplaceRepo}");
            if (add.exitCode != 0)
                return (false, $"Adding marketplace failed: {FirstLine(add.output)}");
        }

        var install = await RunClaudeAsync($"plugin install {PluginRef}");
        if (install.exitCode != 0)
            return (false, $"Install failed: {FirstLine(install.output)}");

        return (true, "Permission detection installed. Restart any open Claude Code sessions to load it.");
    }

    /// <summary>Uninstalls the plugin and removes the marketplace. Returns a user-facing message.</summary>
    public async Task<(bool ok, string message)> UninstallAsync()
    {
        if ((await RunClaudeAsync("--version")).exitCode != 0)
            return (false, "claude CLI not found on PATH.");

        // Best-effort: ignore failures (e.g. already removed) so uninstall always converges to clean.
        await RunClaudeAsync($"plugin uninstall {PluginRef}");
        await RunClaudeAsync($"plugin marketplace remove {MarketplaceName}");
        return (true, "Permission detection removed.");
    }

    private async Task<(bool installed, bool enabled)> ReadPluginStateAsync()
    {
        var result = await RunClaudeAsync("plugin list --json");
        if (result.exitCode != 0)
            return (false, false);

        try
        {
            using var doc = JsonDocument.Parse(result.output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var id) &&
                    string.Equals(id.GetString(), PluginRef, StringComparison.OrdinalIgnoreCase))
                {
                    var enabled = el.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                    return (true, enabled);
                }
            }
        }
        catch (JsonException) { }
        return (false, false);
    }

    private async Task<bool> IsMarketplaceAddedAsync()
    {
        var result = await RunClaudeAsync("plugin marketplace list --json");
        if (result.exitCode != 0)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(result.output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), MarketplaceName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    // Runs `claude <args>` via cmd.exe so PATHEXT shims (.exe/.cmd/.bat) all resolve, capturing
    // combined output. A non-zero exit code (including "command not found") signals failure.
    private static async Task<(int exitCode, string output)> RunClaudeAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = $"/c claude {args}",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return (-1, "");

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var sb = new StringBuilder(stdout);
            if (stderr.Length > 0) sb.Append(stderr);
            return (proc.ExitCode, sb.ToString());
        }
        catch
        {
            return (-1, "");
        }
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var nl = trimmed.IndexOf('\n');
        return nl < 0 ? trimmed : trimmed[..nl].Trim();
    }
}
