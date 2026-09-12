# SafeChat

A Windows desktop app that keeps two conditions true while Claude or ChatGPT is
open: a VPN tunnel is up, and the system clock sits in your work time zone. Break
either one and every matching process is stopped, the traffic is cut at the
firewall, and the clock goes back to your home zone.

It guards both apps. A toggle at the top of the window moves between them, and
everything follows: the rules, the colour, the wording, the pages.

The rules, the state machine and the defaults started from
[omidkorat/claude-watch](https://github.com/omidkorat/claude-watch), which does
the same job in a PowerShell window. Same rules, no terminal, and a great deal
more you can change.

## Install

Double-click **SETUP-OR-UPDATE.cmd**. It pulls the latest code, builds it, puts
shortcuts on your Desktop and in the Start Menu, and opens the app. That window
is the only console you ever see.

It installs what it needs on its own. If the .NET SDK is missing, or only the
runtime is there, it installs the SDK with winget, and failing that with
Microsoft's own installer into your user folder, which needs no administrator
rights. Only if both routes fail does it open the download page and ask you to do
it by hand.

The build itself pulls nothing from the internet: the project has no third-party
packages, so it works with no connection and no VPN.

**Uninstall:** double-click **Uninstall.cmd**. It removes the shortcuts, the
start-with-Windows entry and the scheduled tasks, and asks before deleting your
settings.

## First run

A short wizard asks four things and then gets out of the way: your language,
which of the two apps to guard, the two time zones, and — if you have one — your
server address and key. Everything it asks can be changed later in Settings.

## Activation

Protection needs a key. Everything else — installing, the buy page, placing an
order, tracking it — works without one.

- A fresh install protects for a few days on its own, so nothing is blocking on
  the day it is installed. The owner sets how many.
- A key is one key, one computer. It starts counting from the first activation,
  not from the day it was made, so keys can be made in advance and kept.
- Once a key has been confirmed it keeps working offline right up to its own
  expiry date. A server that cannot be reached never switches a paying
  customer's protection off.

Keys are made and managed in the owner panel, and a customer can check how long
is left on theirs from the public page.

## The rules

| VPN | Time zone | The app |
| --- | --- | --- |
| Connected | Work zone (New York by default) | Allowed |
| Connected | Anything else | Stopped until the clock is fixed |
| Disconnected | Home zone (Tehran by default) | Blocked |

The clock only follows the VPN after a few consecutive misses, so a two-second
network blip does not flip your system time. The app is blocked on the first miss
either way.

Six states show on the dashboard, in the tray icon colour, and in the log:

- **Protected** — VPN up, clock correct. The app may run.
- **Blocked** — no VPN. The app is stopped and kept stopped.
- **Clock mismatch** — the clock is not the zone this state needs.
- **Standby** — home time restored on purpose while the app is closed.
- **Paused** — you paused the guard for a set number of minutes.
- **Not activated** — no key, and the trial has run out. Nothing is enforced,
  and the dashboard says so rather than claiming a protection that is not there.

## The countdown

When the tunnel drops with the app open, the traffic is cut at the firewall
straight away and a full-screen warning counts down. Reconnect inside the
countdown and nothing is closed. Let it run out and every matching process is
stopped. The length is yours to set, including zero for no warning at all.

## The kill switch

Two layers. The first stops every matching process. The second is a Windows
Firewall rule that refuses that app's outbound traffic, which closes the short
gap between a tunnel dropping and a process actually dying. Each guarded app gets
its own rule pointing at its own executable. Set it up once from Settings
(Windows asks for approval that one time) and after that it switches on and off
silently with the guard.

If the app is killed while the block is on, the block stays on. That is the safe
direction to fail in. Open the app and it lifts as soon as the conditions are met
again, or remove the rule from Settings.

## The address panel

The dashboard shows the address the internet currently sees, in large type, with
country and network. It refreshes on its own slow timer rather than every poll.

The address seen while no tunnel is up is remembered, and if that same address
ever shows up while the tunnel is supposedly connected, the panel turns red: the
traffic is going out unprotected.

A VPN is judged by where the traffic actually leaves, not by the name of an
adapter. A mesh VPN that is installed and idle — Tailscale sitting there while
everything goes out through your ISP — is not counted as protection.

## Buying and orders

The **Buy** page shows each plan's price in toman, worked out from the day's
dollar rate on your own server. The **Orders** page places an order and tracks
it without leaving the app.

The server side of that lives in `server/`: a public order form and key lookup, a
JSON API, and an owner panel with a dashboard, orders, keys, prices and settings.
See [server/DEPLOY.md](server/DEPLOY.md) to put it on a VPS — after the one-time
setup it updates itself from the repo within two minutes of a push.

## Token usage

With an Anthropic admin key (`sk-ant-admin…`) in Settings, the Usage page reads
your organisation's token counts and costs from Anthropic's Usage and Cost API:
totals, a daily bar chart, and a breakdown per model over 7, 14, 30 or 90 days.

A Pro or Max subscription on claude.ai has no admin key and no usage endpoint, so
for those accounts there is nothing to read and the page says so.

## What you can change

**Protection** — require a VPN, require the work zone, or either alone. Stop the
app or only warn. How many missed checks count as a real disconnection. How often
to check, from twice a second to once every ten seconds. How long the countdown
runs.

**Per app** — each of Claude and ChatGPT has its own switch, its own executable,
its own time zones, its own firewall rule and its own colour.

**Time zones** — any two Windows zones, not just New York and Tehran. Ask before
changing the clock, change it silently, or never touch it.

**Detection** — the adapter pattern that decides what counts as a VPN, the
process pattern that decides what counts as the app, plus explicit lists of extra
names to stop and names to never stop. The Network page lists every adapter,
marks the ones that currently count, and lets you pin one as trusted or rule one
out.

**Application** — start with Windows, start hidden in the tray, minimize or close
to tray, notifications (all of them or only problems), a confirmation before you
stop the app by hand, and a Ctrl+Alt+K shortcut that stops it from anywhere.

**Appearance** — dark, light, or follow Windows; six accent colours; English or
Persian with a full right-to-left layout.

**Housekeeping** — how long to keep the activity log, export and import of your
settings for a second machine, a log export, and a reset.

## Pages

- **Dashboard** — state, VPN, clock and the app at a glance, today's tally, the
  actions that match the moment, and the last few events.
- **Buy** — plans and prices in toman from your server.
- **Orders** — place and track an order.
- **Activation** — your key, what is left on it, and which machine it is on.
- **Usage** — token counts, costs and a daily chart from an admin key.
- **Processes** — every matching process with its PID, memory and path. Stop one
  or all of them. Matching is on the program name only, so a script that merely
  mentions Claude is never touched.
- **Network** — every adapter, what it is, whether it is up, and whether it counts.
- **Activity** — a timestamped history that survives restarts, with export.
- **Settings** and **About**.

## Shortcuts

| | |
| --- | --- |
| `Ctrl` + `1`…`9` | jump to a page |
| `Ctrl` + `Tab` | switch between Claude and ChatGPT |
| `F5` or `Ctrl` + `R` | check again now |
| `Ctrl` + `O` | open the guarded app |
| `Ctrl` + `Shift` + `S` | stop it |
| `Ctrl` + `S` | save settings |
| `Ctrl` + `Alt` + `K` | stop it from anywhere, even with the window closed |

## What it does to your machine

It stops processes, it changes the system time zone, and it switches a firewall
rule. All three are the point of the app, and all three are logged. Reading the
clock never needs rights; changing it needs Windows approval unless you set up
the shortcut in Settings.

Settings and logs live in `%APPDATA%\SafeChat`. An older install's folder is
copied across on first run, so an upgrade keeps its settings and its history.

## Layout

```
src/ClaudeWatch.Core    the rules, detection and system access — no UI, fully testable
src/ClaudeWatch.App     the WPF window, tray icon, wizard and theme
server/                 the orders service: API, public page and owner panel
tests/ClaudeWatch.Tests 186 checks over the decision table; run with `dotnet run`
tools/                  setup, removal, icon generation and the offline checkers
```

Built on .NET 8 with WPF. No third-party packages, on either side.

## Building by hand

```powershell
dotnet build -c Release
dotnet run --project tests\ClaudeWatch.Tests
dotnet publish src\ClaudeWatch.App -c Release -o build\app
```

## Credit

The rules, the state machine and the defaults come from
[claude-watch](https://github.com/omidkorat/claude-watch) by omidkorat. MIT.
