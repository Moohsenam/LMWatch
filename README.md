# Claude Watch

A Windows desktop app that keeps two conditions true while the Claude desktop app is
open: a VPN tunnel is up, and the system clock sits in your work time zone. Break
either one and every matching Claude process is stopped, and the clock goes back to
your home zone.

It is a desktop version of [omidkorat/claude-watch](https://github.com/omidkorat/claude-watch),
which does the same job in a PowerShell window. Same rules, same defaults — no
terminal, and a lot more you can change.

## Install

Double-click **Install Claude Watch.cmd**. It puts shortcuts on your Desktop and
in the Start Menu, drops a shareable zip in `beta\`, and opens the app. That
window is the only time you ever see a console.

It installs what it needs on its own. If the .NET SDK is missing, or only the
runtime is there, it installs the SDK with winget, and failing that with
Microsoft's own installer into your user folder, which needs no administrator
rights. Only if both routes fail does it open the download page and ask you to do
it by hand.

The build itself pulls nothing from the internet: the project has no third-party
packages, so it works with no connection and no VPN. If a reference pack is
missing it retries once with the normal package source.

**A copy that runs anywhere:** double-click **Build standalone beta.cmd**. It
produces `beta\SafeChat-beta-standalone.zip`, which runs on any Windows 10 or
11 machine with nothing installed at all. It is large and needs internet while
building.

**Uninstall:** double-click **Uninstall.cmd**. It removes the shortcuts, the
start-with-Windows entry and the scheduled tasks, and asks before deleting your
settings.

## What it does for itself

Nothing here needs to be told twice.

**It finds Claude.** Six ways, tried in order of certainty: a Claude process
running right now, the installed-programs list in the registry, the `claude://`
handler, versioned install folders, the usual paths, and finally Start Menu
shortcuts. If Claude is not installed yet, the app picks up its location the
first time a Claude process appears.

**It sets itself up.** On first launch it registers the clock switches, the
firewall block rule and the two switches that toggle that rule, all in a single
elevated step. Windows asks for approval once, on that first launch, and nothing
afterwards ever asks again. If the answer is no, the guard still works; it just
asks when it needs to change the clock.

**It repeats that on its own** when Claude moves to a new version folder, since
the firewall rule points at a specific executable.

**It picks a language** from the Windows display language on first run, and it
never raises an approval prompt during normal running: if a switch is missing,
the firewall step quietly does nothing rather than interrupting.

## The rules

| VPN | Time zone | Claude |
| --- | --- | --- |
| Connected | Work zone (New York by default) | Allowed |
| Connected | Anything else | Stopped until the clock is fixed |
| Disconnected | Home zone (Tehran by default) | Blocked |

The clock only follows the VPN after a few consecutive misses, so a two-second
network blip does not flip your system time. Claude is blocked on the first miss
either way.

Six states show on the dashboard, in the tray icon colour, and in the log:

- **Protected** — VPN up, clock correct. Claude may run.
- **Blocked** — no VPN. Claude is stopped and kept stopped.
- **Clock mismatch** — the clock is not the zone this state needs.
- **Standby** — home time restored on purpose while Claude is closed.
- **Paused** — you paused the guard for a set number of minutes.
- **Guard off** — both rules switched off in Settings.

## The kill switch

Two layers. The first stops every matching Claude process. The second is a
Windows Firewall rule that refuses Claude's outbound traffic, which closes the
short gap between a tunnel dropping and a process actually dying. Set it up once
from Settings (Windows asks for approval that one time) and after that it
switches on and off silently with the guard.

If the app is killed while the block is on, the block stays on. That is the safe
direction to fail in. Open the app and it lifts as soon as the conditions are
met again, or remove the rule from Settings.

## The address panel

The dashboard shows the address the internet currently sees, in large type, with
country and network. It refreshes on its own slow timer rather than every poll.

The address seen while no tunnel is up is remembered, and if that same address
ever shows up while the tunnel is supposedly connected, the panel turns red: the
traffic is going out unprotected.

## Token usage

With an Anthropic admin key (`sk-ant-admin…`) in Settings, the Usage page reads
your organisation's token counts and costs from Anthropic's Usage and Cost API:
totals, a daily bar chart, and a breakdown per model over 7, 14, 30 or 90 days.

A Pro or Max subscription on claude.ai has no admin key and no usage endpoint, so
for those accounts there is nothing to read and the page says so.

## Orders

If you run the orders service in `server/`, put its address in Settings and the
Orders page places and tracks orders from inside the app. See
[server/DEPLOY.md](server/DEPLOY.md) to put that service on a VPS.

## What you can change

**Protection** — require a VPN, require the work zone, or either alone. Stop Claude
or only warn. How many missed checks count as a real disconnection. How often to
check, from twice a second to once every ten seconds.

**Time zones** — any two Windows zones, not just New York and Tehran. Ask before
changing the clock, change it silently, or never touch it.

**One-time setup** — the top of Settings shows whether the privileged pieces are
registered, which Claude it found and how. The button re-runs the whole thing if
you ever need it.

**Detection** — the adapter pattern that decides what counts as a VPN, the process
pattern that decides what counts as Claude, plus explicit lists of extra names to
stop and names to never stop. The Network page lists every adapter, marks the ones
that currently count, and lets you pin one as trusted or rule one out.

**Application** — start with Windows, start hidden in the tray, minimize or close to
tray, notifications (all of them or only problems), a confirmation before you stop
Claude by hand, and a Ctrl+Alt+K shortcut that stops it from anywhere.

**Appearance** — dark, light, or follow Windows; six accent colours; English or
Persian with a full right-to-left layout.

**Housekeeping** — how long to keep the activity log, an export button, and a reset.

## Pages

- **Dashboard** — state, VPN, clock and Claude at a glance, with the actions that
  match the moment and the last few events.
- **Processes** — every matching process with its PID, memory and path. Stop one or
  all of them. Matching is on the program name only, so a script that merely mentions
  Claude is never touched.
- **Orders** — place and track an order against your own orders service.
- **Usage** — token counts, costs and a daily chart from an admin key.
- **Network** — every adapter, what it is, whether it is up, and whether it counts.
- **Activity** — a timestamped history that survives restarts, with export.
- **Settings** and **About**.

## What it does to your machine

It stops processes and it changes the system time zone. Both are the point of the
app, and both are logged. Reading the clock never needs rights; changing it needs
Windows approval unless you set up the shortcut in Settings.

Settings and logs live in `%APPDATA%\ClaudeWatch`.

## Layout

```
src/ClaudeWatch.Core    the rules, detection and system access — no UI, fully testable
src/ClaudeWatch.App     the WPF window, tray icon and theme
server/                 the orders service: API, order form and owner panel
tests/ClaudeWatch.Tests 58 checks over the decision table; run with `dotnet run`
tools/                  setup, removal, icon generation and the offline checkers
```

Built on .NET 8 with WPF. No third-party packages.

## Building by hand

```powershell
dotnet build -c Release
dotnet run --project tests\ClaudeWatch.Tests
dotnet publish src\ClaudeWatch.App -c Release -o build\app
```

## Credit

The rules, the state machine and the defaults come from
[claude-watch](https://github.com/omidkorat/claude-watch) by omidkorat. MIT.
