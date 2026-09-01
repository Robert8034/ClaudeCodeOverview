# Claude Code Overview

A self-hosted dashboard for your own Claude Code usage: tokens, estimated cost, cache savings,
skills, tools, subagents and activity — parsed straight out of the JSONL transcripts Claude Code
already writes to `~/.claude/projects`.

It exists because **there is no hosted usage API for a Claude Pro subscription**. The Admin
Usage & Cost API and the Claude Code Analytics API cover Console/API organizations; the Enterprise
Analytics API covers Team/Enterprise. For Pro, the local transcripts are the only source of truth.
No plugin, hook or telemetry setting is needed on the machine running Claude Code.

> **Every cost figure is "estimated value at API prices", not a bill.** A Pro subscription is a flat
> fee. The numbers show what the same usage *would* cost through the API — which is exactly what
> makes them interesting. The 5-hour and weekly windows are likewise *estimates*: Anthropic
> publishes no token caps for Pro, so any "cap" shown is a soft target you configure yourself.

## What you get

| Page | Content |
|---|---|
| **Overview** | Token split (in/out/cache read/cache write), estimated cost, net cache savings, sessions, turns, live "active now" dot, daily tokens by model, cost trend, model mix, top projects |
| **Projects** | Per-project totals → per-project detail → per-session detail (turn timeline, tool calls, subagent tree, skills used) |
| **Skills** | Scorecard for real skills (invocations, trend, attributed tokens/cost, tool error rate, median run time) plus a separate table for built-in slash commands |
| **Tools & Agents** | Tool frequency, error-rate table, agent-type mix, workflow totals |
| **Activity** | Calendar heatmap, 5-hour block gauge, rolling 7-day tokens, session-duration histogram, lines added/removed, commits |
| **Settings** | Editable pricing table, data-health panel (files, offsets, parse errors, unknown record types, unknown models) |

The database is the durable history. Claude Code deletes transcripts after `cleanupPeriodDays`
(30 by default) and those deletions propagate through the mirror — rows are never deleted when a
source file disappears; the file is just marked `deleted`.

## Requirements

- .NET 10 SDK to build (nothing is needed on the server if you publish `--self-contained`).
- Read access to a directory of Claude Code transcripts — either a local `~/.claude/projects` or a
  mirror of one.

## Run it locally

```bash
dotnet run --project src/ClaudeCodeOverview.Web
```

Then open <http://localhost:5199>.

With no configuration, it reads the current user's `~/.claude/projects` and writes its database to
`%LOCALAPPDATA%\ClaudeCodeOverview\usage.db` (Windows) or `$HOME/.local/share/ClaudeCodeOverview/usage.db`
(Linux). The first run backfills every transcript it can find; the Overview page shows progress.

To point it somewhere else without editing files:

```powershell
$env:ClaudeOverview__DataRoot   = 'D:\some\mirror\projects'
$env:ClaudeOverview__DatabasePath = 'D:\some\where\usage.db'
dotnet run --project src/ClaudeCodeOverview.Web
```

```bash
ClaudeOverview__DataRoot=/some/mirror/projects \
ClaudeOverview__DatabasePath=/some/where/usage.db \
dotnet run --project src/ClaudeCodeOverview.Web
```

## Configuration

All settings live under the `ClaudeOverview` section of `appsettings.json`. Every key can be
overridden by an environment variable using `__` as the separator
(`ClaudeOverview__Currency__UsdToEur=0.92`).

| Key | Default | Notes |
|---|---|---|
| `DataRoot` | `~/.claude/projects` | Transcript root. Created if missing, so a wrong path yields an empty dashboard rather than an error — except under the hardened systemd unit, where creating it is denied and the service refuses to start (see below). |
| `DatabasePath` | `<LocalAppData>/ClaudeCodeOverview/usage.db` | SQLite file (WAL). Set this explicitly in production — see the systemd note below. |
| `Port` | `5199` | Kestrel binds `http://0.0.0.0:<Port>`. |
| `RescanIntervalMinutes` | `5` | Full rescan; catches missed file events, late sidecars and deletions. |
| `DebounceMs` | `300` | Per-path debounce on the file watcher. |
| `Currency:Display` | `USD` | `EUR` shows euro by default; the app bar toggles per session. |
| `Currency:UsdToEur` | `0.86` | Display-only conversion. Everything is stored and computed in USD. |
| `Pricing` | *(built-in seed)* | Seeds the pricing table on **first run only**. After that the table in the database is the source of truth and is edited on the Settings page. |

Notes:

- **`ASPNETCORE_URLS` will not override `Port`.** `Program.cs` calls `UseUrls()` explicitly, which
  wins over the environment variable and over `launchSettings.json` (whose `5143`/`7023` are dead
  values). Change the port through `ClaudeOverview__Port`.
- **Prices are list prices, not gospel.** The seed was taken from Anthropic's public pricing;
  verify at <https://platform.claude.com/pricing> and correct it on the Settings page. A model with
  no pricing row shows **missing** cost, never zero, and raises a banner.
- Day bucketing is fixed to **Europe/Amsterdam** in v1.

## Deploying to a Linux server

The intended setup: Claude Code runs on a workstation, Syncthing mirrors its transcripts to a home
server, and this app watches the mirror.

```
Workstation (Claude Code, Pro)                    Linux server
  ~/.claude/projects  ──── Syncthing ────►  /srv/claude-mirror/projects  (Receive Only)
       (Send Only)                                     │ file watch + 5-min rescan
                                              ClaudeCodeOverview (systemd)
                                                ingester → SQLite → web UI
                                                     │
                          any browser on the LAN ◄────┘   http://<server>:5199
```

### 1. Syncthing

On the **workstation**, share `%USERPROFILE%\.claude\projects` (or `~/.claude/projects`) as
**Send Only**. On the **server**, accept it as **Receive Only** into `/srv/claude-mirror/projects`.
Send Only/Receive Only matters: the server must never push anything back into your real Claude Code
state directory.

Syncthing's own scratch files (`.syncthing.*`, `~syncthing~*`, `.stfolder`, `.stversions`) need no
ignore configuration — the ingester already skips them, along with `journal.jsonl`, `tool-results/`,
`workflows/scripts/` and `memory/`. Syncthing replaces files by temp-file + rename; because
transcripts are append-only, the stored byte offsets stay valid across that.

Consider raising retention on the workstation so the first backfill catches more history — in
`~/.claude/settings.json`:

```json
{ "cleanupPeriodDays": 365 }
```

The app never edits that file, and its own database keeps history forever regardless.

### 2. Publish

From the repo (cross-compiles fine from Windows or macOS):

```bash
dotnet publish src/ClaudeCodeOverview.Web -c Release -r linux-x64 --self-contained -o out
```

That produces a ~120 MB folder with no .NET dependency on the server. Copy it to
`/opt/claude-code-overview` and **restore the executable bit** — publishing from Windows loses it,
and `scp`/`rsync` will faithfully copy mode 644:

```bash
rsync -a out/ server:/opt/claude-code-overview/
ssh server 'chmod +x /opt/claude-code-overview/ClaudeCodeOverview.Web'
```

### 3. systemd

[`deploy/claude-code-overview.service`](deploy/claude-code-overview.service) is ready to use:

```bash
sudo useradd --system --no-create-home claudeoverview
sudo install -m 644 deploy/claude-code-overview.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now claude-code-overview
systemctl status claude-code-overview
journalctl -u claude-code-overview -f
```

Two details in that unit are load-bearing:

- **`Type=simple`, not `Type=notify`.** sd_notify readiness would need the
  `Microsoft.Extensions.Hosting.Systemd` package and `UseSystemd()`, which this app does not use.
  With `Type=notify` systemd would wait for a readiness ping that never arrives and kill the service.
- **Configuration is set as `Environment=` lines, not in `appsettings.json`.** `dotnet publish`
  overwrites `appsettings.json` in the output directory on every redeploy, silently reverting
  operator edits. `DatabasePath` in particular must be set: left unset under a system user without
  `$HOME`, the default resolves to a *relative* path and the database would land inside the publish
  directory, to be wiped by the next deploy. `StateDirectory=claude-overview` creates and owns
  `/var/lib/claude-overview` for it.

- **`ConditionPathIsDirectory` guards the mirror path.** Locally the app creates a missing
  `DataRoot`; under `ProtectSystem=strict` that is denied, the ingester's exception stops the host,
  and `Restart=always` would turn a typo'd path into a silent five-second crash loop. With the
  condition, `systemctl status` says the unit was skipped and names the path. Keep it in sync with
  `ClaudeOverview__DataRoot`.

The service user must be able to read the mirror. If Syncthing runs as a different user, add
`claudeoverview` to its group or grant an ACL on `/srv/claude-mirror`.

If the mirror is large and the watcher misses events, raise the inotify limit:

```bash
echo 'fs.inotify.max_user_watches=524288' | sudo tee /etc/sysctl.d/40-inotify.conf
sudo sysctl --system
```

The 5-minute rescan is the safety net for anything the watcher drops.

### Security

**There is no authentication, no TLS and no reverse proxy in v1 — this is a LAN-only appliance.**
It binds `0.0.0.0`, so anything that can reach the port can read your usage data, including project
paths, session titles and skill names. Do not expose it to the internet without putting your own
authenticating proxy in front of it.

The app reads transcripts only. It never ingests `~/.claude.json` (which holds OAuth account
identity) and never stores prompt or tool-output text — only counts, timestamps, model ids, tool
names and file paths.

## Verifying the numbers

- `npx ccusage daily` reads the same transcripts. This app's totals should be **higher**, because
  ccusage does not walk the per-session `subagents/` directories — which hold roughly half of all
  token volume.
- On the workstation, `~/.claude.json` holds `skillUsage` counters and *last-session-only* token
  totals per project (`lastTotalInputTokens`, …). Useful as an independent cross-check of the most
  recent session; useless as history, since it is overwritten every run.
- Delete the database and let it re-backfill — totals are deterministic and must come out identical.
- The Settings → data health panel shows parse errors, unknown record types and unpriced models. In
  normal operation all three should be empty; a new Claude Code release that changes the transcript
  format will show up there first.

## Development

See [CLAUDE.md](CLAUDE.md) for commands and the architectural invariants, and
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for the full specification: the verified transcript
formats and their traps, the schema, the cost formulas, and the deliberate v1 non-goals.

```bash
dotnet build     # all three projects
dotnet test      # xunit; fixtures are sanitized cuts of real transcripts
```
