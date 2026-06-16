# <img src="./src/sprites/icon.png" /> Claude Watch 

A Windows system tray app that monitors active Claude Code sessions and displays their status as desktop overlays.

## What it does

Claude Watch reads session files from `~/.claude/sessions/` every 3 seconds and shows a floating indicator for each active session. The indicator reflects the current state:

- **Idle** — session is waiting for input
- **Running** — session is actively executing
- **Needs Attention** — session finished recently and is waiting for you (shows a balloon notification)

Right-click the tray icon to switch between display styles (Ducks, Cats, Squares, Wolfenstein).

## Installing

Download `ClaudeWatchSetup.exe` from the [latest release](https://github.com/ArcticGizmo/claude-watch/releases/latest) and run it.

- No admin rights required — installs to `%LocalAppData%\ClaudeWatch\`
- Starts automatically after install
- Adds a Start Menu shortcut and a standard uninstaller (Settings → Apps)

## Updating

Right-click the system tray icon and select **Check for Updates...**

The app will download the latest release and restart automatically.

## Building a release (maintainers)

Releases are created by pushing a version tag. GitHub Actions handles the build and publishes the artifacts to the GitHub Release automatically.

**Steps:**

1. Bump `<Version>` in `src/ClaudeWatch.csproj` to the new version (e.g. `0.2.0`)
2. Commit the change
3. Push a matching tag:
   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```
4. GitHub Actions builds, packs, and uploads the installer to the release page

Teammates can then use **Check for Updates...** in the tray to get the new version.

### Building locally (optional)

If you want to produce release artifacts without pushing a tag, install the `vpk` CLI once:

```
dotnet tool install -g vpk
```

Then run:

```
publish.bat
```

Artifacts land in `releases/`. Upload them manually to a GitHub Release tagged to match the version in the csproj.

## Development

Requirements: .NET 10 SDK

```
dotnet run --project src
```
