# Claude Code Overview — Implementation Plan (self-contained handoff)

> **STATUS (2026-08-31): §11 steps 1–4 are DONE and in this repo** — solution scaffold, the
> complete ingestion engine (tailer, parser with all three skill shapes, two-phase tool events,
> subagent attribution, watcher + backfill + rescan), SQLite schema + migrator, cost & net-savings
> calculator, block calculator, notifier, the full `IDashboardQueries` implementation, and a
> 26-test suite (all green) with fixtures cut from real transcripts. Verified end-to-end against
> real `~/.claude` data (0 parse errors; totals match independently computed ground truth; live
> session ingestion works). A minimal Blazor Overview page proves the wiring.
> **UPDATE (2026-09-01): §11 steps 5 and 6 are DONE too** — the full dashboard UI, plus `README.md`,
> `deploy/claude-code-overview.service` and a verified `linux-x64 --self-contained` publish. The
> publish output was smoke-tested by way of a `win-x64` self-contained publish (the Linux binary
> cannot run on the dev machine): all six pages return 200 in Production with an empty data root.
> **Remaining: the §12 checks that need the real home setup** — ccusage/`~/.claude.json`
> cross-checks and the systemd/LAN deployment itself.
>
> Deviations from the spec: (1) `record_stats` is keyed per file (see schema); (2) stdout-echo
> `local_command` records (no `<command-name>`) are NOT counted as invocations — only records
> carrying `<command-name>` are; (3) **Serilog was never added** — logging is the ASP.NET Core
> default; (4) query DTOs carry `[method: ExplicitConstructor]`, because on an empty database
> SQLite cannot type an aggregate column and Dapper otherwise fails to bind the record constructor,
> which made every page 500 on a fresh install (§12 check 5 — found by smoke-testing the publish).

> **How to use this document.** This is a complete, standalone specification for building a personal
> Claude Code usage-analytics dashboard. It was produced by a planning session that researched the
> Claude Code data formats against real transcripts and official docs (2026-08-31, Claude Code
> 2.1.220/2.1.251) and had the design adversarially reviewed. If you are a Claude Code session:
> implement it top to bottom — every data-format fact you need is in here; re-verify against the
> local `~/.claude` data where told to, and prefer what you observe locally over this document if
> they disagree (note the difference in the README). If you are the human: see "§0 Human checklist"
> and then hand the rest to Claude ("implement IMPLEMENTATION_PLAN.md").

---

## 0. Human checklist (one-time setup, ~15 minutes)

1. **Windows machine** (where Claude Code runs under your Pro subscription): install
   [Syncthing](https://syncthing.net/). Add folder `%USERPROFILE%\.claude\projects`, type **Send Only**.
2. **Linux server**: install Syncthing, accept the share as **Receive Only** into
   `/srv/claude-mirror/projects` (any path works; it goes in the app's config).
3. Pair the two devices in the Syncthing UIs; confirm files appear on the server.
4. Optional but recommended — keep more history on the Windows machine: in
   `%USERPROFILE%\.claude\settings.json` add `"cleanupPeriodDays": 365`
   (default is 30 days, after which Claude Code deletes old transcripts; the app's database keeps
   history forever regardless, but a longer window helps the first backfill catch more).
5. Open Claude Code in a fresh repo containing this file and say: *implement IMPLEMENTATION_PLAN.md*.
   Development can happen on any machine that has a `~/.claude` folder with real data (the Windows
   machine is ideal); the data root is configurable, so point it at your own transcripts while
   developing and at the mirror in production.

---

## 1. What is being built and why

A single self-hosted web app ("**ClaudeCodeOverview**") that gives the owner insight into their
Claude Code usage under a **Claude Pro subscription**:

1. **Token usage per project** (and per model, per day, per session).
2. **Skill usage** — invocation counts per skill and honest effectiveness proxies.
3. **Cost & cache savings** — estimated **API-equivalent value** (a Pro subscription is flat-fee;
   the dashboard must label costs as "estimated value at API prices", never as a bill).
4. **Model mix & rate-limit-window usage** (Pro limits: 5-hour rolling + weekly; not exposed by
   any API — approximated from activity timestamps, labeled as estimates).
5. **Tool & agent analytics** — tool frequency, error rates, subagent/workflow usage.
6. **Productivity signals** — sessions-per-day heatmap, session durations, lines added/removed,
   commits.

**Why parse local files?** For Pro/Max subscriptions there is **no hosted usage API**: Anthropic's
Admin "Usage & Cost" API and the Claude Code Analytics API cover Console/API organizations, and the
Enterprise Analytics API covers Team/Enterprise claude.ai orgs — none cover Pro. Everything needed
is already written to disk by Claude Code under `~/.claude`. No plugin, hook, or telemetry
configuration is required on the Claude Code machine.

**Deployment (decided):** the app runs on the home **Linux server** and ingests a **Syncthing
mirror** of the Windows machine's `~/.claude/projects`. Dashboard is reachable from any browser at
home. The app's SQLite database is the **durable history** — Claude Code deletes transcripts after
30 days (`cleanupPeriodDays` default) and those deletions propagate through the mirror, so rows are
never deleted when source files disappear.

**Stack (decided):** .NET 10 (current LTS), Blazor Web App with **Interactive Server** rendering,
SQLite. Single process, single deployable, published `linux-x64 --self-contained`, run via systemd.

**Known simpler alternative** (for context, not the goal): `npx ccusage` prints token/cost reports
from the same JSONL. Useful as a verification baseline (§12); it has no skill analytics, no durable
history, no dashboard.

---

## 2. Data source: verified facts about `~/.claude`

Observed on real transcripts from Claude Code 2.1.220 and 2.1.251 and cross-checked against
official docs. **The parser must follow these exactly; each carries a "trap" that produces wrong
numbers if ignored.** Re-verify opportunistically against local data during implementation
(formats may drift across CLI versions — that is what `record_stats` in §5 is for).

### 2.1 File layout

```
<claude-home>/projects/<path-slug>/            one directory per project
    <sessionId>.jsonl                          main session transcript (append-only)
    <sessionId>/                               per-session sidecar dir (only for some sessions)
        subagents/agent-<id>.jsonl             subagent transcript (append-only)
        subagents/agent-<id>.meta.json         {agentType, description, spawnDepth, toolUseId, parentAgentId?}
        subagents/agent-<id>.forked-skill.json optional: {skillName, attributionName, effort}
        subagents/workflows/wf_<id>/           workflow runs: agent-*.jsonl (+ journal.jsonl)
        tool-results/toolu_*.txt               persisted large tool outputs (do not ingest)
        workflows/scripts/*.js                 workflow scripts (do not ingest)
    memory/*.md                                may exist; not a session (do not ingest)
```

- Ingest **every** `**/*.jsonl` under the root **except** `journal.jsonl` and anything under
  `tool-results/`, `workflows/scripts/`, `memory/`.
- **The project-directory slug is lossy** (`\`, `/` and `.` all become `-`). Never derive project
  identity from it — use the `cwd` field present on transcript records.
- **Subagent files hold ~50–60% of all token volume** and are NOT included in the parent session
  file. Skipping them undercounts by roughly half. Inside a subagent file, `sessionId` equals the
  **parent** session UUID (that is the join key) and records additionally carry `agentId`,
  `attributionAgent`, and sometimes `attributionSkill`.

### 2.2 JSONL record shapes

Each line is one JSON object with a `type`. Types observed:
`assistant`, `user`, `system`, `attachment`, `mode`, `permission-mode`, `ai-title`, `last-prompt`,
`file-history-snapshot`, `file-history-delta`, `queue-operation`, `atis-latch`. Unknown/new types
**must never crash the parser** — count them and move on.

**`assistant` records** (the token source; `message.usage` was present on 100% of observed records):

```jsonc
{
  "type": "assistant",
  "uuid": "…",              // unique per line
  "parentUuid": "…",
  "sessionId": "…",          // in subagent files: the PARENT session id
  "timestamp": "2026-08-31T12:34:56.789Z",   // ISO-8601 UTC
  "cwd": "C:\\Users\\x\\source\\repos\\ProjectA",  // authoritative project path
  "gitBranch": "main",
  "version": "2.1.251",      // CLI version
  "isSidechain": false,       // true in subagent files
  "requestId": "…",
  "effort": "high",           // may be absent
  "slug": "…",               // short session label; may be absent
  "message": {
    "id": "msg_…",            // dedup key — repeats across lines while streaming!
    "model": "claude-fable-5",  // raw model id
    "content": [ /* text / tool_use / thinking blocks */ ],
    "stop_reason": "…",
    "usage": {
      "input_tokens": 2,
      "output_tokens": 514,
      "cache_creation_input_tokens": 9160,
      "cache_read_input_tokens": 26364,
      "cache_creation": { "ephemeral_1h_input_tokens": 9160, "ephemeral_5m_input_tokens": 0 },
      "server_tool_use": { "web_search_requests": 0, "web_fetch_requests": 0 },
      "service_tier": "standard",
      "iterations": [ /* DUPLICATES of the top-level counters — never sum these */ ]
    }
  }
}
```

**Traps (each verified against real data):**

1. **`usage.iterations[]` duplicates the top-level counters.** Sum top-level fields only.
2. **`message.id` repeats across lines** while a response streams; the **final** line carries the
   completed counts. Dedup **last-wins** on `message.id` (`INSERT … ON CONFLICT DO UPDATE`, §5).
   `uuid` is unique per line and is NOT the dedup key for token counting.
3. Some subagent records omit `iterations`/`speed` — every `usage` field must be optional in the model.

**Tool calls:** `tool_use` blocks (`{"type":"tool_use","id":"toolu_…","name":"Edit","input":{…}}`)
appear in assistant `message.content`. The matching result arrives in the **next `user` record** as
a `tool_result` content block / `toolUseResult` field, which may carry an error flag and (for
Edit/Write) structured patch info. Large outputs are replaced inline by a
`<persisted-output>…</persisted-output>` pointer — irrelevant for ingestion (outputs are not stored).

### 2.3 Skill invocations — there is NO `Skill` tool call

Verified: zero `tool_use` blocks named `Skill` exist in real transcripts even though skills were
used. A parser that looks for a Skill tool reports 0% skill usage. Skills appear as **three shapes**:

1. **In-session skill** — a `user` record with `isMeta: true, turnCompanion: true` whose `content`
   is a **string** like:
   `"<command-message>my-skill</command-message>\n<command-name>my-skill</command-name>\n<skill-format>true</skill-format>"`
   (a `promptId` field links it to its turn). The `<skill-format>true</skill-format>` marker
   distinguishes skills from other command records.
2. **Built-in slash command** — a `system` record with `subtype: "local_command"` and content
   `"<command-name>/model</command-name> <command-message>model</command-message> <command-args>…</command-args>"`
   (examples seen: `/model`, `/effort`, `/login`, `/clear`). Recorded as `shape='local_command'`;
   shown in a separate "Built-in commands" table on the Skills page, **never in the skills scorecard** (§9).
3. **Forked (background) skill** — a `system` record whose content contains
   `<forked-skill-launch>{"agentId":"a9b5…","skillName":"code-review","description":"/code-review high --fix"}</forked-skill-launch>`
   plus the sidecar `agent-<id>.forked-skill.json`. The subagent's records then carry
   `attributionSkill: "code-review"` — this enables full token/cost attribution for forked skills.

`attachment` records with `attachment.type: "skill_listing"` are skill *availability*, not usage — ignore.

### 2.4 Other machine-local sources (v2 candidates; used for verification in v1)

- `~/.claude/history.jsonl` — one line per user prompt: `{display, timestamp(ms), project, sessionId}`.
  Outlives the 30-day transcript cleanup. Not synced/ingested in v1.
- `~/.claude.json` — contains `skillUsage: {<skill>: {usageCount, lastUsedAt}}` and per-project
  **last-session-only** counters (`lastTotalInputTokens`, `lastTotalOutputTokens`,
  `lastTotalCacheCreationInputTokens`, `lastTotalCacheReadInputTokens`, `lastCost`,
  `lastLinesAdded`, `lastLinesRemoved`, …) — overwritten every run, so useless as history but
  **gold for verification** (§12). Contains OAuth account info — never ingest or log this file's
  identity fields.
- Files are **append-only** and, on the Windows side, held open by live sessions. Lines can exceed 1 MB.

---

## 3. Deployment architecture

```
Windows PC (Claude Code, Pro)                     Linux server
  %USERPROFILE%\.claude\projects   --Syncthing-->   /srv/claude-mirror/projects   (Receive Only)
        (Send Only)                                        │ inotify watch + 5-min rescan
                                                    ClaudeCodeOverview app (systemd)
                                                      ingester → SQLite → Blazor UI
                                                           │
                                any browser at home ◀──────┘   http://<server>:5199
```

- Sync latency is seconds, so "live" dashboard updates remain meaningful.
- Syncthing updates files via temp-file + rename. Transcript content is append-only, so per-file
  **byte offsets remain valid** across replacements. Ignore Syncthing temp patterns
  (`.syncthing.*`, `~syncthing~*`, `.stfolder`, `.stversions`).
- The 30-day cleanup on Windows propagates as file deletions — the ingester treats a vanished file
  as a non-event (rows persist; mark the file row `deleted`).
- Bind Kestrel to `http://0.0.0.0:5199` (configurable). Home-LAN exposure only; auth/reverse proxy
  is out of v1 scope (document the assumption in the README).
- If inotify limits are hit on large mirrors, raise `fs.inotify.max_user_watches`; the 5-minute
  rescan also covers missed events.

---

## 4. Solution structure

```
ClaudeCodeOverview.sln
  src/ClaudeCodeOverview.Web/          Blazor host
    Components/{Layout,Pages,Widgets}/
    Services/DashboardRefreshService.cs (scoped, per-circuit)
    Program.cs
  src/ClaudeCodeOverview.Core/
    Ingestion/   IngestionService.cs (BackgroundService), TranscriptWatcher.cs, FileTailer.cs,
                 TranscriptLineParser.cs, SkillExtractor.cs, ToolEventExtractor.cs, AgentMetaReader.cs
    Data/        Db.cs (connection factory), Migrations/NNN_*.sql (embedded resources), Repositories/
    Pricing/     CostCalculator.cs      ← the ONLY place cost & savings formulas live
    Derived/     BlockCalculator.cs, TimeBuckets.cs
    Queries/     IDashboardQueries.cs + DashboardQueries.cs   ← the UI↔storage seam
    Notifications/ IIngestionNotifier.cs, IngestionDelta.cs
  tests/ClaudeCodeOverview.Tests/      xunit
```

**Packages:** `Dapper` + `Microsoft.Data.Sqlite` (bulk appends + hand-tuned SQL; EF adds nothing
here), `MudBlazor`, `Blazor-ApexCharts`, `Serilog.AspNetCore` + file sink. Pin current stable
versions at implementation time.

**SQLite discipline:** pragmas `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000` on
every connection. Exactly **one** long-lived write connection, owned by `IngestionService`; all
writes flow through a `Channel<IngestBatch>` (single consumer = single writer). UI reads use
short-lived connections (WAL gives concurrent readers). Migrations: ordered embedded
`NNN_name.sql` scripts + a `schema_version` table.

**Configuration (`appsettings.json`):**

```jsonc
"ClaudeOverview": {
  "DataRoot": "/srv/claude-mirror/projects",   // dev: your own ~/.claude/projects
  "DatabasePath": "/var/lib/claude-overview/usage.db",
  "Port": 5199,
  "RescanIntervalMinutes": 5,
  "DebounceMs": 300,
  "Currency": { "Display": "USD", "UsdToEur": 0.86 },
  "Pricing": [ /* seeds the pricing table on first run; table is source of truth after — see §7 */ ]
}
```

---

## 5. Database schema

```sql
projects(id INTEGER PRIMARY KEY, cwd TEXT UNIQUE NOT NULL, slug TEXT,
         first_seen_utc TEXT, last_seen_utc TEXT);

sessions(id TEXT PRIMARY KEY,               -- sessionId
         project_id INTEGER, git_branch TEXT, cli_version TEXT,
         first_ts_utc TEXT, last_ts_utc TEXT, title TEXT);   -- title from ai-title records

agents(agent_id TEXT PRIMARY KEY, session_id TEXT, parent_agent_id TEXT,
       agent_type TEXT, description TEXT, spawn_depth INTEGER, tool_use_id TEXT,
       workflow_id TEXT, skill_name TEXT, skill_effort TEXT);  -- from meta.json + forked-skill.json

usage_events(                                -- one row per deduped assistant message
  message_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, agent_id TEXT,
  project_id INTEGER NOT NULL, ts_utc TEXT NOT NULL, day_local TEXT NOT NULL,  -- YYYY-MM-DD Europe/Amsterdam
  model TEXT NOT NULL,
  input_tokens INTEGER, output_tokens INTEGER, cache_creation INTEGER, cache_read INTEGER,
  cache_5m INTEGER, cache_1h INTEGER, web_search INTEGER, web_fetch INTEGER,
  service_tier TEXT, cost_usd REAL, cache_savings_usd REAL,
  attribution_skill TEXT, request_id TEXT, effort TEXT);
-- indexes: (project_id, day_local), (session_id), (day_local, model), (ts_utc)
-- upsert:  INSERT … ON CONFLICT(message_id) DO UPDATE SET <every column>   ← last-wins (§2.2 trap 2)

tool_events(id INTEGER PRIMARY KEY, tool_use_id TEXT UNIQUE, session_id TEXT, agent_id TEXT,
  project_id INTEGER, ts_utc TEXT, day_local TEXT, tool_name TEXT,
  is_error INTEGER DEFAULT 0, is_mcp INTEGER DEFAULT 0,
  lines_added INTEGER, lines_removed INTEGER, is_git_commit INTEGER DEFAULT 0);
-- indexes: (tool_name, day_local), (session_id), (project_id, day_local)

skill_invocations(id INTEGER PRIMARY KEY, session_id TEXT, project_id INTEGER,
  ts_utc TEXT, day_local TEXT, skill_name TEXT NOT NULL,
  shape TEXT NOT NULL CHECK(shape IN ('in_session','local_command','forked')),
  agent_id TEXT, args TEXT);
-- index: (skill_name, day_local)

ingested_files(id INTEGER PRIMARY KEY, path TEXT UNIQUE, byte_offset INTEGER NOT NULL DEFAULT 0,
  file_size INTEGER, last_write_utc TEXT, session_id TEXT, agent_id TEXT,
  status TEXT, parse_errors INTEGER DEFAULT 0, unknown_types INTEGER DEFAULT 0);

parse_error_log(id INTEGER PRIMARY KEY, file TEXT, line_no INTEGER, ts_utc TEXT, snippet TEXT);
-- keep newest ~200 rows (delete older on insert)

record_stats(file_id INTEGER, record_type TEXT, cnt INTEGER, PRIMARY KEY(file_id, record_type));
-- per ingested file (so a truncation reset can cleanly delete by file_id);
-- session/agent rollups via join to ingested_files

pricing(model_pattern TEXT PRIMARY KEY,       -- longest-prefix match against model id
  in_usd REAL, out_usd REAL, cache_w5m_usd REAL, cache_w1h_usd REAL, cache_r_usd REAL);

activity_blocks(id INTEGER PRIMARY KEY, block_start_utc TEXT, block_end_utc TEXT,
  tokens INTEGER, cost_usd REAL, messages INTEGER);   -- materialized, always rebuildable

settings(key TEXT PRIMARY KEY, value TEXT);
```

No rollup tables — at personal scale (well under 1M usage rows) the indexed fact tables serve every
dashboard query directly.

---

## 6. Ingestion pipeline

**`FileTailer`** — per file: open `FileStream(path, FileMode.Open, FileAccess.Read,
FileShare.ReadWrite | FileShare.Delete)` (share flags matter on Windows during development; harmless
on Linux), seek to the stored `byte_offset`, scan a pooled buffer for `\n`, and hand **complete
lines only** to the parser — a trailing partial line is left for the next pass, and the new offset
is committed **in the same SQLite transaction as the rows it produced** (crash-safe, no double
ingest). Grow the buffer for long lines (cap 64 MB; beyond that log a parse error and skip to the
next `\n`). If `file_size < byte_offset` (replacement by a shorter file — rare): reset offset to 0,
delete that file's rows (`usage_events`/`tool_events`/`skill_invocations` by session_id+agent_id —
one file maps to exactly one such pair; parent-file rows have `agent_id` NULL — plus `record_stats`
by file_id), re-ingest.

**`TranscriptLineParser`** — `System.Text.Json` with a source-generated `JsonSerializerContext`;
`message.content` stays a `JsonElement` (shapes vary). Routing:

| record | action |
|---|---|
| `assistant` | upsert `usage_events` (last-wins; top-level usage only); compute `cost_usd`/`cache_savings_usd` (§7); for each `tool_use` block **INSERT a `tool_events` row immediately** (`ts_utc` = this assistant record's timestamp; result fields NULL, `is_error=0`) |
| `user` | match `tool_result`/`toolUseResult` by tool_use id → **UPDATE the existing `tool_events` row** (`is_error`; lines added/removed **only** from structured patch data — never inferred from input strings; absent → NULL). This insert-then-update two-phase design is crash-safe: there is no in-memory pending map to lose across batch or restart boundaries. If `isMeta && turnCompanion` and content contains `<skill-format>true</skill-format>` → `skill_invocations(shape='in_session')` with the `<command-name>` value |
| `system` | `subtype=="local_command"` → `skill_invocations(shape='local_command')` with `<command-name>`/`<command-args>`; content containing `<forked-skill-launch>{json}</forked-skill-launch>` → `skill_invocations(shape='forked')` + upsert `agents` |
| `ai-title` | update `sessions.title` |
| anything else | increment `record_stats`; **never throw** |

Every parsed record also upserts `projects` (on `cwd`) and widens the session's
`first_ts_utc`/`last_ts_utc`. `sessions.project_id` = the **first-seen** `cwd` for that session
(display only); per-event `project_id` on `usage_events`/`tool_events` remains authoritative for
all aggregates. When a new `agent-<id>.jsonl` is first seen, read the sibling `meta.json` +
optional `forked-skill.json` into `agents`; if the sidecars haven't arrived yet (Syncthing can
deliver them after the JSONL), leave the row incomplete and retry during each 5-minute rescan
until found. **Skill-name normalization:** store `skill_name` without any leading `/` in every
shape, so the same skill groups across shapes; `shape` preserves the origin.

`is_git_commit`: `tool_name` is `Bash` **or `PowerShell`** (Windows transcripts are full of
PowerShell tool calls) and `input.command` matches `\bgit\b[\s\S]*?\bcommit\b`; only rows with
`is_error = 0` count as commits. `is_mcp`: tool name starts with `mcp__`.

**`TranscriptWatcher`** — one `FileSystemWatcher` on the data root (`*.jsonl`, recursive,
300 ms per-path debounce) feeding the ingest channel; on startup, a full recursive scan compares
size/mtime against `ingested_files` and enqueues new/changed files (**backfill**), publishing
progress (files done / total) through the notifier so the UI can show it; a full rescan every
5 minutes catches anything the watcher missed. Per-line and per-file try/catch —
a bad line increments `parse_errors` + `parse_error_log`, a bad file gets `status='error'`;
the pipeline never stops.

**`IIngestionNotifier`** — singleton. The writer publishes after each committed batch; the notifier
coalesces on a 1.5 s timer and emits one
`IngestionDelta { HashSet<long> ProjectIds; HashSet<string> SessionIds; DateOnly MinDayLocal; bool NewParseErrors; bool NewUnknownModels; BackfillProgress? Backfill }`.
Each Blazor circuit's scoped `DashboardRefreshService` subscribes, filters by interest, re-queries,
and calls `InvokeAsync(StateHasChanged)` with a latest-wins gate (skip if a refresh is running).

---

## 7. Cost model (single implementation in `CostCalculator`)

Prices are **USD per million tokens**; match `model` against `pricing.model_pattern` by longest
prefix. No match → `cost_usd = NULL` and the model id appears in the unknown-models banner —
**never silently price at 0**.

```
cost_usd = (in·P.in + out·P.out + c5m·P.w5m + c1h·P.w1h + cread·P.r) / 1_000_000
```

where `c5m`/`c1h` come from `usage.cache_creation.ephemeral_{5m,1h}_input_tokens`
(fallback: all of `cache_creation_input_tokens` at the 5m rate when the breakdown is absent).

**Net cache savings** (the write premium is subtracted — a gross formula overstates savings and can
mask a net loss under heavy 1h-writes):

```
cache_savings_usd = ( cread·(P.in − P.r) − c5m·(P.w5m − P.in) − c1h·(P.w1h − P.in) ) / 1_000_000
```

UI tooltip cites this formula. Editing a pricing row recomputes `cost_usd`/`cache_savings_usd` on
all affected `usage_events` **and rebuilds `activity_blocks`** (single writer; fine at this scale).

**Pricing seed** (Anthropic API list prices, verified 2026-08; cache write = 1.25× input for 5-min
TTL and 2× for 1-hour TTL; cache read = 0.1× input — re-verify at
<https://platform.claude.com/pricing> during implementation and adjust; table stays editable in the UI):

| model_pattern | in | out | w5m | w1h | read |
|---|---|---|---|---|---|
| `claude-fable-5` | 10.00 | 50.00 | 12.50 | 20.00 | 1.00 |
| `claude-opus-5` | 5.00 | 25.00 | 6.25 | 10.00 | 0.50 |
| `claude-opus-4` | 5.00 | 25.00 | 6.25 | 10.00 | 0.50 |
| `claude-sonnet-5` | 2.00 | 10.00 | 2.50 | 4.00 | 0.20 |
| `claude-sonnet-4-6` | 3.00 | 15.00 | 3.75 | 6.00 | 0.30 |
| `claude-haiku-4-5` | 1.00 | 5.00 | 1.25 | 2.00 | 0.10 |

**Framing rule:** every cost figure in the UI is labeled "estimated value at API prices" —
a Pro subscription is a flat fee; this number shows what the usage *would* cost, which is exactly
what makes it interesting.

---

## 8. Derived computations

- **5-hour activity blocks** (approximation of Pro's rolling 5h window, ccusage semantics):
  order `usage_events` by `ts_utc` (UTC); a block starts at the first event's timestamp **floored
  to the hour**; events within `start + 5h` belong to it; a gap beyond that starts a new floored
  block. Persist into `activity_blocks`. **Recompute the touched time range from scratch on every
  batch** — backfill and late-arriving subagent files deliver events out of order, which breaks
  incremental split/merge logic; full-range recompute costs milliseconds at this scale.
- **Weekly usage** = **rolling 7-day UTC sum** of tokens/cost (not ISO calendar weeks — Anthropic's
  weekly cap is rolling). Label both 5h and weekly views as *estimates*: Anthropic publishes no
  token caps for Pro; any gauge "cap" is a user-configured soft target in `settings`.
- **`day_local`** = the calendar date in **Europe/Amsterdam**, computed at ingest
  (`TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")`; on Windows dev machines fall back to
  `"W. Europe Standard Time"` in a try/catch). Hardcoded in v1 — no timezone setting.
- **Session duration** = `last_ts_utc − first_ts_utc`. **Active session** = last usage event
  younger than 5 minutes (no extra files needed on the server).
- **"Turns"** — wherever the UI says turns, it counts **deduped assistant messages**
  (`usage_events` rows) in the filtered scope.
- **Currency:** everything stored/computed in USD; EUR applied at display time only
  (`Currency:UsdToEur`, `nl-NL` formatting).

---

## 9. Skill analytics — scope and honesty

Fixed disclaimer shown on the Skills page (verbatim): *"These are proxies of usage, cost and
friction — they cannot tell whether a skill's output was correct or accepted."*

The scorecard covers **real skills only** (`in_session` + `forked`, badged by shape); built-in
slash commands (`local_command`) appear in their own "Built-in commands" count table on the same
page and never enter the scorecard.

**All real skills:**
- Invocation counts per day / per project; 30-days-vs-prior-30-days trend.
- Sanity cross-check available on the Windows machine: `skillUsage` counters in `~/.claude.json`.

**Forked skills only (v1)** — these have hard attribution via `attributionSkill`/`agent_id` on
subagent usage rows:
- Attributed tokens & estimated cost over time.
- Tool error rate within attributed work (`is_error` ratio on the skill's `tool_events`).
- Median run duration (last − first timestamp in the skill's agent transcript).

**Deferred to v2** (documented, not built): in-session skill attribution — requires walking the
`uuid`/`parentUuid` turn chain from the invocation's `promptId` to the next real user prompt;
re-invocation-rate proxy (misleading for legitimately iterative skills).

---

## 10. Dashboard UI

**Global layout:** MudBlazor `MudAppBar` with filter bar — date-range picker with presets
(today/7d/30d/90d/all), project multi-select, model multi-select, USD/EUR toggle, dark/light toggle
(persisted in `localStorage`), live-status dot (watcher healthy/stalled = last rescan age).
Filters live in a scoped `GlobalFilterState` (event-based, cascading). Banner slot beneath the bar:
unknown-model warning ("set a price" → Settings), data-health alerts, and — when the mirrored data
suggests default retention — a tip showing the exact `"cleanupPeriodDays": 365` snippet for the
Windows machine's `settings.json` (the app never edits that file).

| Page | Content |
|---|---|
| **Overview `/`** | Stat tiles: total tokens (in/out/cache-read/cache-write split), estimated cost, net cache savings, sessions, turns, active-session dot. Charts: daily tokens stacked by model; daily cost line + 7-day moving average; model-mix donut; top-5 projects bar; compact 5h-block gauge. **Live.** |
| **Projects `/projects`** | Table: project (folder name; full `cwd` tooltip), tokens, cost, sessions, last activity, inline model-mix mini-bar. → `/projects/{id}`: per-day stacked tokens + cost, top skills/tools scoped to project, sessions table (start, duration, turns, tokens, cost, subagent count, tool errors, title). → `/sessions/{id}`: turn timeline (tokens/model/effort per assistant turn), tool-call table, subagent tree (`parent_agent_id`), skills invoked, git branch. |
| **Skills `/skills`** | Scorecard table per §9 + disclaimer callout; separate "Built-in commands" count table (`local_command`); per-skill drill-down: invocations over time, attributed tokens/cost (forked), error-rate trend, per-project split, shape badges. |
| **Tools & Agents `/tools`** | Tool frequency top-20 horizontal bar; error-rate table (calls, errors, rate; min-calls filter); agent-type donut with token totals; workflows tile (count + tokens). |
| **Activity `/activity`** | GitHub-style calendar heatmap via ApexCharts heatmap (sessions or tokens per `day_local`, toggle); 5h gauge (current block vs configured soft cap) + last-7-days block timeline; rolling-7-day token line; session-duration histogram; lines added/removed per day; commits per day. |
| **Settings `/settings`** | Pricing table editor (inline edit + pre-filled "add" rows for detected unpriced models); data root path with a validation check; EUR rate; 5h/weekly soft caps; **data health panel**: files ingested + offsets, parse-error log (last 50, file+line), unknown record types with counts, unknown model ids, watcher lag, backfill state. |

**Live updates:** Overview tiles, 5h gauge, active dot, heatmap's today-cell, and the health badge
subscribe to `IngestionDelta`; historical drill-downs re-query on navigation (each card gets a
manual refresh icon). Empty state (no rows yet): a setup card showing data-root check, files found,
and backfill progress.

**Charting:** `Blazor-ApexCharts` for all charts (area/line, stacked bar, donut, heatmap,
radialBar gauge), theme synced to MudBlazor dark/light.

---

## 11. Implementation order

1. **Scaffold** — solution + projects + packages; migrator with `001_schema.sql`; appsettings +
   options binding; Serilog. App boots to an empty MudBlazor shell.
2. **Parser core, tests first** — `FileTailer`, `TranscriptLineParser`, `SkillExtractor`,
   `ToolEventExtractor`, `AgentMetaReader`. Build fixtures by trimming records from the real local
   `~/.claude/projects` (redact any prompt text in fixtures). Required test cases: last-wins dedup
   across repeated `message.id`; `iterations[]` ignored; partial trailing line not consumed; >1 MB
   line; unknown record type counted, not thrown; all three skill shapes extracted; subagent file →
   `agent_id` + `attribution_skill` + parent-session join; git-commit detection for Bash *and*
   PowerShell; truncation reset.
3. **Ingestion service** — channel writer, watcher + debounce + rescan, startup backfill with
   progress, notifier, offset-in-same-transaction commit.
4. **Derived + queries** — `CostCalculator` (both §7 formulas + longest-prefix matching + NULL for
   unknown), `BlockCalculator` (out-of-order arrivals covered by tests), `IDashboardQueries`
   implementations, pricing-edit recompute path.
5. **UI** — layout/filters/theme → Overview (establishes the live-widget pattern) →
   Projects/Sessions → Skills → Tools & Agents → Activity → Settings + health + banners + empty state.
6. **Ship** — README (Syncthing Send-Only/Receive-Only setup, ignore patterns, systemd unit file,
   `dotnet publish -c Release -r linux-x64 --self-contained`, config reference, LAN-only security
   note, `cleanupPeriodDays` advice); smoke-test the publish output.

Suggested `IDashboardQueries` surface (all take a
`QueryFilter { DateOnly From, To; long[]? ProjectIds; string[]? Models }` unless noted):
`GetHeadlineStats`, `GetDailySeries(groupBy: Model|Project|TokenClass)`, `GetModelMix`,
`GetProjectSummaries`, `GetSessions(projectId)`, `GetSessionDetail(sessionId)`,
`GetSkillScorecard`, `GetSkillDaily(skill)`, `GetToolUsage`, `GetAgentUsage`,
`GetActivityHeatmap(year)`, `GetRateWindows(nowUtc)`, `GetSessionDurationHistogram`,
`GetProductivityDaily`, `GetDataHealth`, `GetPricing`/`UpsertPricing`, `GetUnknownModelIds`.

---

## 12. Verification

1. **Unit tests** from §11 step 2 and 4 all green.
2. **Ground-truth cross-checks** (run on the machine with real data):
   - Most recent session's totals vs `~/.claude.json` → that project's `lastTotalInputTokens`,
     `lastTotalOutputTokens`, `lastTotalCacheCreationInputTokens`,
     `lastTotalCacheReadInputTokens` — should match (small drift only if the session is still open).
   - Daily totals vs `npx ccusage daily` — same order of magnitude; if ccusage skips subagent
     files, this app's numbers should be **higher**, and must equal the raw transcript sum exactly
     (write a one-off script summing top-level usage over all JSONL to prove it).
   - Skill invocation counts vs `skillUsage` in `~/.claude.json`.
3. **Live path:** with the app watching real data, run a Claude Code turn → Overview tiles update
   within ~2 s of the transcript write (on the server: within seconds of the sync).
4. **Determinism:** delete the DB, re-backfill → identical totals.
5. **Fresh install:** empty data root → setup card, no crashes; then point at real data → backfill
   progress → populated dashboard.
6. **Deployment:** systemd unit starts on boot, survives restart; dashboard reachable from another
   device on the LAN; publish folder is fully self-contained (no .NET install needed on the server).

---

## 13. Explicit non-goals for v1

Cut deliberately (most were flagged by the adversarial design review — do not re-add without need):
in-session skill token attribution (v2), re-invocation proxy, spawn-depth histogram,
persisted-output volume tile, custom SVG calendar heatmap, filter deep-links, timezone setting,
temporal pricing history, `history.jsonl` / `.claude.json` ingestion (v2: `history.jsonl` outlives
the 30-day cleanup and can backfill the heatmap), authentication / reverse proxy, multi-machine
aggregation (the schema is ready for it — add a `machine` column to `ingested_files` and separate
mirror subfolders per machine when needed).
