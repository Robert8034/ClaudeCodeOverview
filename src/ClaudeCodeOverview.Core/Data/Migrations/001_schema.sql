-- ClaudeCodeOverview schema v1.
-- Design notes:
--  * usage_events dedup is LAST-WINS on message_id (streaming repeats a message.id; the
--    final line carries completed counts). The upsert lives in IngestRepository.
--  * record_stats is keyed per FILE so a truncation reset can delete cleanly by file_id.
--  * skill_invocations carries the source record uuid for idempotent re-ingestion.

CREATE TABLE projects (
    id             INTEGER PRIMARY KEY,
    cwd            TEXT NOT NULL UNIQUE,
    slug           TEXT,
    first_seen_utc TEXT,
    last_seen_utc  TEXT
);

CREATE TABLE sessions (
    id           TEXT PRIMARY KEY,          -- sessionId
    project_id   INTEGER,                   -- first-seen cwd wins (display only)
    git_branch   TEXT,
    cli_version  TEXT,
    first_ts_utc TEXT,
    last_ts_utc  TEXT,
    title        TEXT                       -- from ai-title records
);

CREATE TABLE agents (
    agent_id        TEXT PRIMARY KEY,
    session_id      TEXT,
    parent_agent_id TEXT,
    agent_type      TEXT,
    description     TEXT,
    spawn_depth     INTEGER,
    tool_use_id     TEXT,
    workflow_id     TEXT,
    skill_name      TEXT,
    skill_effort    TEXT,
    meta_loaded     INTEGER NOT NULL DEFAULT 0   -- 0 until sidecar meta.json has been read
);

CREATE TABLE usage_events (
    message_id        TEXT PRIMARY KEY,     -- deduped last-wins
    session_id        TEXT NOT NULL,
    agent_id          TEXT,
    project_id        INTEGER NOT NULL,
    ts_utc            TEXT NOT NULL,
    day_local         TEXT NOT NULL,        -- YYYY-MM-DD, Europe/Amsterdam
    model             TEXT NOT NULL,
    input_tokens      INTEGER NOT NULL DEFAULT 0,
    output_tokens     INTEGER NOT NULL DEFAULT 0,
    cache_creation    INTEGER NOT NULL DEFAULT 0,
    cache_read        INTEGER NOT NULL DEFAULT 0,
    cache_5m          INTEGER NOT NULL DEFAULT 0,
    cache_1h          INTEGER NOT NULL DEFAULT 0,
    web_search        INTEGER NOT NULL DEFAULT 0,
    web_fetch         INTEGER NOT NULL DEFAULT 0,
    service_tier      TEXT,
    cost_usd          REAL,                 -- NULL when the model has no pricing row
    cache_savings_usd REAL,
    attribution_skill TEXT,
    request_id        TEXT,
    effort            TEXT
);
CREATE INDEX ix_usage_project_day ON usage_events(project_id, day_local);
CREATE INDEX ix_usage_session     ON usage_events(session_id);
CREATE INDEX ix_usage_day_model   ON usage_events(day_local, model);
CREATE INDEX ix_usage_ts          ON usage_events(ts_utc);

CREATE TABLE tool_events (
    id            INTEGER PRIMARY KEY,
    tool_use_id   TEXT NOT NULL UNIQUE,     -- insert on tool_use, update on tool_result
    session_id    TEXT NOT NULL,
    agent_id      TEXT,
    project_id    INTEGER NOT NULL,
    ts_utc        TEXT NOT NULL,            -- timestamp of the calling assistant record
    day_local     TEXT NOT NULL,
    tool_name     TEXT NOT NULL,
    is_error      INTEGER NOT NULL DEFAULT 0,
    is_mcp        INTEGER NOT NULL DEFAULT 0,
    lines_added   INTEGER,                  -- only from structuredPatch data, never guessed
    lines_removed INTEGER,
    is_git_commit INTEGER NOT NULL DEFAULT 0  -- attempt flag; commit metrics filter is_error=0
);
CREATE INDEX ix_tool_name_day    ON tool_events(tool_name, day_local);
CREATE INDEX ix_tool_session     ON tool_events(session_id);
CREATE INDEX ix_tool_project_day ON tool_events(project_id, day_local);

CREATE TABLE skill_invocations (
    id          INTEGER PRIMARY KEY,
    record_uuid TEXT NOT NULL UNIQUE,       -- source record uuid: idempotent re-ingestion
    session_id  TEXT NOT NULL,
    project_id  INTEGER NOT NULL,
    ts_utc      TEXT NOT NULL,
    day_local   TEXT NOT NULL,
    skill_name  TEXT NOT NULL,              -- normalized: no leading '/'
    shape       TEXT NOT NULL CHECK (shape IN ('in_session','local_command','forked')),
    agent_id    TEXT,
    args        TEXT
);
CREATE INDEX ix_skill_name_day ON skill_invocations(skill_name, day_local);

CREATE TABLE ingested_files (
    id             INTEGER PRIMARY KEY,
    path           TEXT NOT NULL UNIQUE,
    byte_offset    INTEGER NOT NULL DEFAULT 0,
    file_size      INTEGER,
    last_write_utc TEXT,
    session_id     TEXT,
    agent_id       TEXT,
    status         TEXT NOT NULL DEFAULT 'active',  -- active | deleted | error
    parse_errors   INTEGER NOT NULL DEFAULT 0,
    unknown_types  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE parse_error_log (
    id      INTEGER PRIMARY KEY,
    file    TEXT NOT NULL,
    line_no INTEGER,
    ts_utc  TEXT NOT NULL,
    snippet TEXT
);

CREATE TABLE record_stats (
    file_id     INTEGER NOT NULL,
    record_type TEXT NOT NULL,
    cnt         INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_id, record_type)
);

CREATE TABLE pricing (
    model_pattern  TEXT PRIMARY KEY,        -- longest-prefix match against model id
    in_usd         REAL NOT NULL,           -- USD per million tokens
    out_usd        REAL NOT NULL,
    cache_w5m_usd  REAL NOT NULL,
    cache_w1h_usd  REAL NOT NULL,
    cache_r_usd    REAL NOT NULL
);

CREATE TABLE activity_blocks (
    id              INTEGER PRIMARY KEY,
    block_start_utc TEXT NOT NULL,          -- first event floored to the hour (UTC)
    block_end_utc   TEXT NOT NULL,          -- block_start + 5h
    tokens          INTEGER NOT NULL,       -- input+output+cache_creation+cache_read
    cost_usd        REAL,
    messages        INTEGER NOT NULL
);
CREATE INDEX ix_blocks_start ON activity_blocks(block_start_utc);

CREATE TABLE settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);
