# telemetry-backend Specification

## Purpose
TBD - created by archiving change add-opt-in-telemetry. Update Purpose after archive.
## Requirements
### Requirement: Single-table storage with server-computed week

(Extended) The Worker SHALL also expose a `/logs` route that accepts user-initiated diagnostic bundles and stores them in a separate `log_bundles` table (ref_code primary key, received_at, instance_id, plugin_version, jellyfin_version, telemetry JSON, note, log_lines JSON). This is distinct from the anonymous `pings` table and follows a different retention rule (90-day prune). The anonymous-pings storage and contract are unchanged.

#### Scenario: Log bundle stored separately from pings

- **WHEN** a valid bundle is POSTed to `/logs`
- **THEN** it is inserted into `log_bundles` with a generated ref code and never touches the `pings` table

### Requirement: Ingest validation and caps

The Cloudflare Worker SHALL be the sole write path. It MUST require the publishable ingest key header, validate schema_version and payload shape, reject payloads over 2 KB, enforce a per-instance daily cap on error-transition inserts (returning 204 on cap hit), apply a transient in-memory per-IP rate limit plus a global requests-per-minute cap, and never persist IP addresses into the dataset.

#### Scenario: Malformed or oversized payload

- **WHEN** a request arrives with an unknown schema_version, a missing field, a bad key, or a body over 2 KB
- **THEN** the Worker rejects it without writing a row

#### Scenario: Flood of fresh UUIDs

- **WHEN** a client mints new instance UUIDs and posts at high volume
- **THEN** the per-IP and global rate limits bound ingestion volume, and the daily canary's row-growth alarm fails loudly if totals exceed expected bounds

### Requirement: No client-facing database access

The D1 database SHALL be reachable only through the ingest Worker and scoped Cloudflare API tokens (used by the canary workflow and the maintainer's analysis tooling). The plugin SHALL ship only the ingest URL and the publishable write key, whose compromise is bounded to junk rows, never reads, never privacy.

#### Scenario: Extracted key cannot read the dataset

- **WHEN** someone extracts the ingest key from plugin source
- **THEN** they can POST validated, rate-limited payloads and nothing else; no read, list, or delete path exists for them

### Requirement: Always-on backend with loud failure detection

(Extended) The Worker SHALL run a daily `scheduled()` job that deletes `log_bundles` rows older than 90 days, so user-identifying bundles are never retained indefinitely. The anonymous `pings` are not subject to this prune.

#### Scenario: Scheduled prune runs

- **WHEN** the daily scheduled job executes
- **THEN** bundles older than 90 days are removed and anonymous pings are left intact

