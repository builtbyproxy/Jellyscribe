-- Telemetry storage on Cloudflare D1 (SQLite). The database is reachable ONLY
-- through the ingest Worker and scoped API tokens; there is no direct client
-- access path, which is what RLS provided in the abandoned Supabase design.
-- See openspec/changes/add-opt-in-telemetry/specs/telemetry-backend.

CREATE TABLE IF NOT EXISTS pings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    received_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    -- Computed by the Worker from UTC arrival time (client clocks drift).
    week TEXT NOT NULL,
    instance_id TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    plugin_version TEXT NOT NULL,
    jellyfin_version TEXT NOT NULL,
    ping_type TEXT NOT NULL CHECK (ping_type IN ('weekly', 'error_transition')),
    features TEXT NOT NULL DEFAULT '{}',
    buckets TEXT NOT NULL DEFAULT '{}',
    errors TEXT NOT NULL DEFAULT '{}'
);

-- One weekly row per instance per week; the Worker merges counters on conflict
-- so a duplicate send can never destroy a window's counts.
CREATE UNIQUE INDEX IF NOT EXISTS pings_weekly_instance_week
    ON pings (instance_id, week) WHERE ping_type = 'weekly';

CREATE INDEX IF NOT EXISTS pings_instance_received ON pings (instance_id, received_at DESC);
CREATE INDEX IF NOT EXISTS pings_type_received ON pings (ping_type, received_at DESC);

-- User-initiated diagnostic bundles (sent via the plugin's "Send logs to developer"
-- button). NOT anonymous, unlike pings: may contain the user's Letterboxd username
-- or film titles, sent only on an explicit disclosed click. Keyed by a quotable ref
-- code; pruned after 90 days by the Worker's scheduled() handler.
CREATE TABLE IF NOT EXISTS log_bundles (
    ref_code TEXT PRIMARY KEY,
    received_at TEXT NOT NULL,
    instance_id TEXT NOT NULL,
    plugin_version TEXT NOT NULL,
    jellyfin_version TEXT NOT NULL,
    telemetry TEXT,
    note TEXT,
    log_lines TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS log_bundles_received ON log_bundles (received_at DESC);
CREATE INDEX IF NOT EXISTS log_bundles_instance ON log_bundles (instance_id);

-- Install-count telemetry (NOT opt-in, unlike pings). Captures the two signals
-- that reach every install: the Jellyfin manifest poll (Option A, GET /manifest.json
-- on the Worker) and the release-asset download (Option B, GET /dl/<tag>/... which
-- 302s to GitHub). A unique install is approximated by a SHA-256 of
-- (ip : week : HASH_SALT): weekly-rotating so the same install dedupes WITHIN a
-- week (giving a WAU-style headcount) but cannot be linked ACROSS weeks. The raw
-- IP is never stored. IP-based identity is coarse (NAT undercounts, dynamic IPs
-- overcount), so read these as order-of-magnitude active-install figures, not an
-- exact roster — the opt-in pings.instance_id remains the only exact per-install id.
-- version is '' for manifest polls, the release tag (e.g. v1.18.4) for downloads.
CREATE TABLE IF NOT EXISTS install_hits (
    week TEXT NOT NULL,
    ip_hash TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('manifest', 'download')),
    version TEXT NOT NULL DEFAULT '',
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    hits INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (week, ip_hash, kind, version)
);
CREATE INDEX IF NOT EXISTS install_hits_week_kind ON install_hits (week, kind);

-- Active installs by version: latest weekly ping per instance.
CREATE VIEW IF NOT EXISTS latest_per_instance AS
SELECT p.instance_id, p.received_at, p.week, p.plugin_version, p.jellyfin_version,
       p.features, p.buckets, p.errors
FROM pings p
JOIN (
    SELECT instance_id, MAX(received_at) AS max_received
    FROM pings WHERE ping_type = 'weekly' GROUP BY instance_id
) latest ON latest.instance_id = p.instance_id AND latest.max_received = p.received_at
WHERE p.ping_type = 'weekly';
