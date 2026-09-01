# Transcript parser test fixtures

Sanitized fixtures cut from REAL Claude Code transcripts found under
`C:\Users\dams00\.claude\projects\` on 2026-08-31. Every JSONL line is one real
record (sanitized), except where "synthesized" is noted below.

Source files referenced below:

- **PC** = `...\C--Users-dams00-source-repos-samenlevingszaken-Prlg-Api-ProductCatalogus\ca76c30a-cb41-413c-88b6-e72848e791f0.jsonl` (a `/code-review` forked-skill session, Claude Code 2.1.220, model `claude-fable-5`)
- **SUB** = `...\Prlg-Api-ProductCatalogus\ca76c30a-cb41-413c-88b6-e72848e791f0\subagents\agent-a340a8eced20e234f.jsonl` (subagent spawned *by* the forked code-review skill agent)
- **LIVE** = `...\C--Users-dams00-source-repos-samenlevingszaken-ClaudeCodeOverview\a83b3bd0-e449-48bf-9b07-2d6ffd0220ec.jsonl` (Claude Code 2.1.251)
- **IA** = `...\C--Users-dams00-source-repos-samenlevingszaken-Prlg-iSuite-iAdministratie\7ce40668-fec6-492b-bc3a-664c994ef00e.jsonl` (2026-08-03 session, model `claude-opus-4-8`, 557 KB)

Line numbers below are **1-based**.

## Sanitization rule

Applied uniformly by script to every fixture: walking the JSON tree, any *string
value* that is free text (message content text, tool inputs/outputs, command
args, prompts, descriptions, thinking signatures, file snapshots, ...) and is
longer than 60 characters is replaced with `"[redacted <original length> chars]"`
(length = original character count).

Kept intact (never redacted):

- all key names and the full JSON structure;
- string values under these keys: `type`, `subtype`, `uuid`, `parentUuid`,
  `logicalParentUuid`, `sessionId`, `session_id`, `requestId`, `id`, `model`,
  `timestamp`, `cwd`, `gitBranch`, `version`, `effort`, `agentId`,
  `attributionSkill`, `tool_use_id`, `name` (this covers `message.id`,
  `tool_use` block `name`/`id`, `tool_result` `tool_use_id`, timestamps, cwd, ...);
- the **entire `usage` object** (all token numbers, `cache_creation`,
  `iterations[]`, `server_tool_use`, ...);
- all non-string values (`is_error`, `isSidechain`, `isMeta`, numbers, ...);
- any string containing one of the special marker tags — kept **verbatim
  including inner values**: `<command-name>…</command-name>`,
  `<command-message>…</command-message>`, `<skill-format>true</skill-format>`,
  `<command-args>…</command-args>`, `<forked-skill-launch>{…}</forked-skill-launch>`.

Strings of 60 characters or fewer are left as-is (e.g. the 59-char Bash error
output in `tool_pair.jsonl`).

## Fixtures

### records_various.jsonl (7 lines, in this order)

| # | record | provenance |
|---|--------|------------|
| a | assistant whose `usage` HAS `iterations[]` (text block) | PC line 17 (`msg_011CeaZH6oCpTij7ruRSx6Rv`) |
| b | subagent assistant with `agentId` (`a340a8eced20e234f`) + `attributionSkill` (`code-review`) | SUB line 5 |
| c | user with `isMeta:true`, `turnCompanion:true`, string content containing `<skill-format>true</skill-format>` | LIVE line 19 — the only occurrence on this machine; taken from an already-written early line of the live session |
| d | system `subtype:"local_command"` — `/model` with `<command-name>/<command-message>/<command-args>` markers | PC line 3 |
| e | system containing `<forked-skill-launch>{"agentId":"a9b516948c04b2f52","skillName":"code-review",...}</forked-skill-launch>` | PC line 411 |
| f | non-assistant/user/system type: `queue-operation` (its long free-text `content` is redacted) | PC line 185 |
| g | `ai-title` record | PC line 15 |

### stream_repeat.jsonl (2 lines) — PARTIALLY SYNTHESIZED

Both lines carry the SAME `message.id` (`msg_011CeaZP25rYkSb3GrvwLx6d`).

- line 2 = the real final record, PC line 67, sanitized.
- line 1 = **synthesized**: a copy of line 2 with `usage.output_tokens` set to
  `10` and `usage.iterations` removed, simulating the mid-stream partial write
  that precedes the final record in real transcripts. No real mid-stream partial
  was on disk (finished transcripts only retain the final line per message id).

### tool_pair.jsonl (4 lines, all real)

1. assistant with `tool_use` block (`Edit`, `toolu_01BBDMS1MvmPsGSKTh3FNE6V`) — PC line 190
2. the immediately following user record with the matching `tool_result` (success) and top-level `toolUseResult` — PC line 191
3. assistant with `tool_use` block (`Bash`, `toolu_01Gr4iK1qJ8NhFxgMobp5C6H`) — PC line 392
4. the immediately following user record whose `tool_result` has `"is_error":true` — PC line 393 (a real `gh: command not found` failure; nothing synthesized)

### session_small.jsonl (165 lines)

The COMPLETE real session **IA** (`7ce40668-fec6-492b-bc3a-664c994ef00e.jsonl`,
557 KB on disk), sanitized line by line. Contains assistant stream-repeats
(72 assistant lines, 22 distinct `message.id`), 6 `local_command` system
records (incl. `/model` marker records), attachments, file-history snapshots,
`agent-name`, `ai-title`, `mode`/`permission-mode` records, etc. No skill-format
user records and no forked-skill launches exist in this session (hence 0 in the
expected counts).

### session_small.expected.json

Computed by script **over the sanitized `session_small.jsonl`** and verified by
an independent recount:

- `lines` — all lines in the file
- `assistantLines` — records with `type:"assistant"`
- `dedupedMessages` — distinct `message.id` among assistant records
- `inputTokens` / `outputTokens` / `cacheCreation` / `cacheRead` — summed
  LAST-WINS per `message.id` (the LAST line for each `message.id` wins), from
  the TOP-LEVEL `usage` only (`input_tokens`, `output_tokens`,
  `cache_creation_input_tokens`, `cache_read_input_tokens`); `iterations[]` is
  never used
- `models` — distinct `message.model`, sorted
- `toolUseBlocks` — total `tool_use` blocks across ALL assistant lines
  (stream-repeat duplicates included, i.e. counted per line, not per deduped message)
- `skillInvocations.in_session` — user records with `isMeta && turnCompanion`
  whose string content contains `<skill-format>true`
- `skillInvocations.local_command` — system records with `subtype:"local_command"`
- `skillInvocations.forked` — system records containing `<forked-skill-launch>`

### sidecars\

Real files copied byte-for-byte in structure from
`...\ca76c30a-cb41-413c-88b6-e72848e791f0\subagents\`, names exactly as found
(the same sanitization rule was applied; no value exceeded 60 chars, so the
content is unchanged):

- `agent-a9b516948c04b2f52.meta.json` —
  `{"agentType":"general-purpose","description":"/code-review high --fix","name":"code-review","spawnDepth":1}`
- `agent-a9b516948c04b2f52.forked-skill.json` —
  `{"skillName":"code-review","attributionName":"code-review","effort":"high"}`

## Observed record shapes

### (i) ai-title record

The title lives in the top-level `aiTitle` key; the record has only three keys
(`type`, `aiTitle`, `sessionId`) — no `uuid`, no `timestamp`:

```json
{"type":"ai-title","aiTitle":"Review database switching implementation across APIs","sessionId":"ca76c30a-cb41-413c-88b6-e72848e791f0"}
```

The same title is re-emitted many times per session (71 times in PC).

### (ii) toolUseResult on a successful Edit

Top-level key `toolUseResult` on the *user* record that carries the
`tool_result`. For a successful `Edit` it is an OBJECT and YES, it contains
`structuredPatch`: keys are `filePath`, `oldString`, `newString`,
`originalFile` (full pre-edit file content), `structuredPatch`, `userModified`
(bool), `replaceAll` (bool). `structuredPatch` is an array of diff hunks, each
`{oldStart, oldLines, newStart, newLines, lines[]}` where `lines` are
`" "`/`"+"`/`"-"`-prefixed diff lines. Sanitized example (from `tool_pair.jsonl`
line 2):

```json
"toolUseResult": {
  "filePath": "[redacted 95 chars]",
  "oldString": "[redacted 514 chars]",
  "newString": "[redacted 936 chars]",
  "originalFile": "[redacted 1504 chars]",
  "structuredPatch": [
    {"oldStart": 22, "oldLines": 6, "newStart": 22, "newLines": 11,
     "lines": ["         {", "[redacted 65 chars]", " ", "+            if (inputBytes.Length < 16)", "..."]}
  ],
  "userModified": false,
  "replaceAll": false
}
```

### (iii) is_error on tool_result blocks

`is_error` is a boolean INSIDE the `tool_result` content block (in
`message.content[]` of the user record), a sibling of `type`, `content` and
`tool_use_id`. It is **absent on success** (not `false`). Sanitized examples
from `tool_pair.jsonl`:

Success (line 2) — no `is_error` key at all:

```json
{"tool_use_id":"toolu_01BBDMS1MvmPsGSKTh3FNE6V","type":"tool_result","content":"[redacted 201 chars]"}
```

Error (line 4) — note the key order also differs in the real data:

```json
{"type":"tool_result","content":"Exit code 127\n/usr/bin/bash: line 28: gh: command not found","is_error":true,"tool_use_id":"toolu_01Gr4iK1qJ8NhFxgMobp5C6H"}
```

On error records the top-level `toolUseResult` is a plain STRING starting with
`"Error: "` (here 66 chars, hence redacted), not an object.

## Correction (2026-08-31, during test bring-up)

`session_small.expected.json` -> `skillInvocations.local_command` was corrected from 6 to 3.
The session contains 6 records with `subtype:"local_command"`, but 3 of them are `<local-command-stdout>`
result echoes with no `<command-name>`. Only the 3 records carrying `<command-name>` are actual
invocations; counting stdout echoes would double-count every built-in command.

## marker_only_commands.jsonl (6 lines) — SYNTHESIZED

Added 2026-09-01. No real fixture existed for the shape current CLI builds actually emit
(2.1.220 / 2.1.233 / 2.1.234), so these lines are synthesized to match the structure observed on
real local transcripts, with a neutral `cwd` and no real prompt text. Structure copied faithfully:
`user` record, `message.content` a STRING containing only marker tags, `isMeta` / `turnCompanion` /
`skill-format` all absent, `userType: "external"`, `promptId` present only on turn-starting commands.

| # | record | expected |
|---|--------|----------|
| 1 | `/clear`, marker-only | `local_command` (built-in) |
| 2 | `/model` carrying command args | `local_command`, args captured |
| 3 | `/init`, with the message-before-name ordering seen in real data | `in_session` — a skill, since Claude Code counts it in `skillUsage` |
| 4 | stdout echo with no command name | NOT an invocation |
| 5 | user prose *describing* the marker tags | NOT an invocation |
| 6 | a `tool_result` block quoting the tags, with `structuredPatch` | NOT an invocation |

Lines 5 and 6 are the important ones: these marker tags appear verbatim inside this repo's
`IMPLEMENTATION_PLAN.md`, so a substring rule would count edits to that file as skill usage.
