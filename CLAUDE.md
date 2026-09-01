# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A self-hosted .NET 10 Blazor Server dashboard that parses Claude Code's own `~/.claude/projects`
JSONL transcripts into SQLite and reports token usage, cost, skills, tools and agents for a
**Pro subscription** (no hosted usage API exists for Pro — the local files are the only source).

**`IMPLEMENTATION_PLAN.md` is the specification of record.** It documents every verified
transcript-format fact, the schema, the cost formulas, the UI spec, and the deliberate v1
non-goals (§13). Its STATUS block records two accepted deviations from the spec; a third is
undocumented there — logging is plain ASP.NET Core logging, the plan's Serilog line was never
implemented. Read the relevant § before changing ingestion, pricing or schema; prefer what real
local data shows over the document, and note any new divergence there.

## Commands

The repo path contains spaces — quote paths in shell commands.

```bash
dotnet build                                            # all three projects (ClaudeCodeOverview.slnx)
dotnet test                                             # xunit
dotnet test --filter "FullyQualifiedName~Longest_prefix_wins"   # one test (or ~ClassName)
dotnet run --project src/ClaudeCodeOverview.Web         # serves http://0.0.0.0:5199
```

- `dotnet test` does not reliably rebuild the Web project. **After touching `Components/` (.razor),
  run `dotnet build`** — Razor compile errors are invisible to `dotnet test`.
- There is no lint/format step, no `.editorconfig`, and `.github/workflows/` is empty (no CI).
- **Running the app ingests real data.** `DataRoot` is `null` in `appsettings.json`, so it falls back
  to the current user's `~/.claude/projects`, and the database defaults to
  `%LOCALAPPDATA%\ClaudeCodeOverview\usage.db` (outside the repo). To run against throwaway data,
  override both (PowerShell):
  `$env:ClaudeOverview__DataRoot='<dir>'; $env:ClaudeOverview__DatabasePath='<file>'; dotnet run --project src/ClaudeCodeOverview.Web`
- `Program.cs` calls `UseUrls(...Port)` — port **5199** always wins; the `5143`/`7023` in
  `launchSettings.json` are dead values.
- Deployment (`README.md`, `deploy/claude-code-overview.service`) is done:
  `dotnet publish src/ClaudeCodeOverview.Web -c Release -r linux-x64 --self-contained -o out`.
  The Linux binary can't be executed here — smoke-test publish changes with a `win-x64`
  self-contained publish and curl the pages; that runs in **Production**, which `dotnet run`
  (Development, via launchSettings) does not exercise.

## Architecture

```
src/ClaudeCodeOverview.Core/     Ingestion/ Data/ Pricing/ Derived/ Queries/ Notifications/
src/ClaudeCodeOverview.Web/      Blazor Server host: Components/{Layout,Pages,Widgets}, Services/
tests/ClaudeCodeOverview.Tests/  xunit + Fixtures/ (sanitized real transcripts)
```

Flow: `TranscriptWatcher` (FSW + debounce + 5-min rescan) → `IngestionService` (the only writer) →
`IngestOrchestrator` → `FileTailer` → `TranscriptLineParser` (+ `SkillExtractor`,
`ToolEventExtractor`, `AgentMetaReader`) → `IngestRepository.ApplyBatch` → SQLite →
`IDashboardQueries` → Razor pages, with `IIngestionNotifier` pushing coalesced deltas to live widgets.

### Invariants — breaking these silently corrupts the numbers

- **Single writer.** One long-lived write connection, owned by `IngestionService`; all ingest work
  runs on that one loop. Everything else opens short-lived read connections via `Db.Open` (WAL).
  `DashboardQueries.UpsertPricingAsync` is the one deliberate second writer.
- **`IDashboardQueries` is the UI↔storage seam.** Razor components never touch SQL or SQLite types.
- **Query DTOs need `[method: ExplicitConstructor]`.** With zero rows SQLite cannot type an
  expression column (`SUM`/`COUNT`/…), so Microsoft.Data.Sqlite reports it as BLOB and Dapper
  refuses to bind a positional record constructor — the whole page 500s on a fresh install. `CAST`
  does not help; the attribute (which makes Dapper bind by parameter name) does. Add it to any new
  Dapper-materialized record and keep `EmptyDatabaseTests` covering every query method.
- **`CostCalculator` is the only place cost/savings formulas live.** Longest-prefix match on
  `model_pattern`; an unknown model yields `NULL` cost — never 0. Net savings subtracts the cache
  write premium. Editing a price re-derives all `usage_events` and rebuilds `activity_blocks`.
- **Dedup is last-wins on `message.id`** (streaming repeats the id; the last line is complete).
  Sum only top-level `usage` fields — `usage.iterations[]` duplicates them and must never be summed.
- **Subagent files carry roughly half of all tokens.** Their `sessionId` is the *parent* session id
  (the join key); `agentId` / `attributionSkill` come from the record, the rest from sidecar
  `meta.json` / `forked-skill.json`, which may arrive late and are retried on each rescan.
- **There is no `Skill` tool call.** Skills are detected in four record shapes; names are normalized
  without a leading `/`. `shape` is the *semantic* category, not the record form: `local_command`
  means built-in slash command (its own table, never the scorecard), `in_session`/`forked` mean real
  skill. Current CLI builds emit a `user` record whose whole content is command markers, with no
  marker separating a skill from a built-in — `SkillExtractor.BuiltInCommands` makes that call.
  **Match the entire content, never a substring:** these marker tags appear verbatim in
  `IMPLEMENTATION_PLAN.md`, so a "contains" rule counts edits to this repo as skill usage.
  `SlashCommandShapeTests` pins both false-positive cases.
- **`tool_events` is two-phase**: INSERT on the assistant's `tool_use` block, UPDATE on the matching
  `tool_result` in the next user record. Deliberately no in-memory pending map — it survives
  restarts and batch boundaries. Lines added/removed come only from `structuredPatch`, never inferred.
- **The byte offset commits in the same transaction as the rows it produced** (crash-safe, no double
  ingest). A file shorter than its offset triggers a full reset + re-ingest.
- **The parser never throws.** Unknown record types increment `record_stats`; bad lines land in
  `parse_error_log`; a bad file gets `status='error'` and the pipeline continues.
- **`day_local` is Europe/Amsterdam**, computed once at ingest (`TimeBuckets`, with a Windows
  fallback id) and used by every filter and index.
- Schema changes go in a new embedded `Data/Migrations/NNN_*.sql`; `Migrator` applies them in name
  order tracked by `schema_version`. Never edit an applied script.
- **A parser change does not fix existing databases.** `FindChangedFiles` skips any file whose size
  equals its stored offset, and `skill_invocations` inserts are INSERT OR IGNORE, so newly-recognised
  records in already-consumed bytes stay invisible. Put one-time fixes in `Data/DataUpgrades.cs`
  (guarded by a `settings` key, run by the ingestion service after `Migrator`): reclassify stored
  rows in place, then `ResetFile` the transcripts still on disk so backfill re-reads them. Never
  rewind a file that has vanished — those rows are the only remaining record of it.

### UI pattern

Pages inherit `DashboardPage` (`Components/Pages/DashboardPage.cs`): override `LoadAsync`, get
filter-change + ingestion-delta refresh with a latest-wins gate for free, set `LiveUpdates => false`
on heavy drill-downs. `@key` every ApexChart on `DataVersion` so it re-creates instead of diffing
stale options, and build options via `Charts.Base<T>(IsDark, …)` for theme sync. Filters and
currency/number formatting live in the scoped `GlobalFilterState`.

**Framing rule (applies to every cost figure in the UI):** label it "estimated value at API prices",
never a bill — a Pro subscription is flat-fee.

## Test fixtures

`tests/ClaudeCodeOverview.Tests/Fixtures/README.md` documents each fixture's provenance, the
sanitization rule, and hard-won record-shape details (e.g. `is_error` is absent on success rather
than `false`; `toolUseResult` is an object on success but a string on error). Read it before adding
fixtures or changing extraction. Fixtures are cut from real transcripts and sanitized by rule —
any new fixture must be sanitized the same way (free-text strings over 60 chars redacted, structure
and all `usage` numbers kept).
