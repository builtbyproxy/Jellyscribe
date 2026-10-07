# log-bundles Specification

## Purpose
TBD - created by archiving change add-send-logs. Update Purpose after archive.
## Requirements
### Requirement: Explicit, disclosed consent before sending

The "Send logs to developer" action SHALL require an explicit click and a confirmation step that lists, before anything is sent, everything the bundle holds: the recent log lines (naming films, shows, Jellyfin users and Letterboxd usernames, and possibly quoting service error messages, with email addresses masked), the plugin and Jellyfin versions and which log files were read, the telemetry snapshot, this instance's telemetry id (or, when it has none, a one-off id that stays the same for the server run), and the optional note; that the bundle is NOT anonymous; that passwords, cookies, and auth tokens are never logged; and the 90-day retention. A Preview of what would be sent MUST be available from the confirmation step (`POST /Telemetry/PreviewLogs`, with the note typed so far in the body, never in the URL), and that preview MUST show the COMPLETE bundle, the actual log lines AND the telemetry snapshot, not just the anonymous portion. The preview and the send MUST be assembled by the same code path, with the same one-off id when telemetry has none, so the preview cannot diverge from what is uploaded.

#### Scenario: User opens the send dialog

- **WHEN** an admin clicks "Send logs to developer"
- **THEN** a confirmation modal appears stating the logs are not anonymous and offering a preview, and nothing is uploaded until the admin confirms

#### Scenario: Preview shows the real log lines

- **WHEN** the admin clicks Preview in the confirmation step
- **THEN** the preview renders the exact bundle including the log lines and the typed note (not only the anonymous telemetry snapshot), byte-for-byte identical to what the send would upload

#### Scenario: User cancels

- **WHEN** the admin closes or cancels the confirmation modal
- **THEN** no bundle is uploaded

### Requirement: Bundle contents

The bundle SHALL contain only: recent LetterboxdSync-tagged log lines (the same sanitized lines the Logs tab shows), the current telemetry snapshot, the plugin and Jellyfin versions, the collector status (which log files were read, how many lines matched), an instance id, and an optional user-supplied note. It MUST reuse the existing sanitized log reader so no content beyond the Logs-tab lines is included. The reader MUST replace email addresses (including URL-encoded, HTML-entity and JSON-escaped forms) with `[email]` and MUST cut the body from review-reply lines that older releases logged, and the plugin MUST NOT log Serializd account emails (log lines name those accounts by a short hash tag instead) or the body of a successful review reply.

#### Scenario: Email addresses never leave the server

- **WHEN** a log line holds an email address (an older log, a login typed as an email, or a quoted error message)
- **THEN** the Logs tab, the preview and the uploaded bundle all show `[email]` in its place

#### Scenario: Bundle assembled from the shared log reader

- **WHEN** a send is confirmed
- **THEN** the bundle's log lines are exactly those produced by the shared `ReadRecentLogLines` reader, capped to the recent window, and the telemetry snapshot is embedded as structured JSON

### Requirement: Works regardless of telemetry opt-in

Sending logs SHALL succeed whether or not anonymous telemetry is enabled. If a telemetry instance id exists it is used (so the bundle joins that instance's telemetry); if not, a one-off id is generated for the bundle.

#### Scenario: Telemetry disabled

- **WHEN** an admin with telemetry disabled sends logs
- **THEN** the bundle uploads successfully using a generated instance id and a snapshot reflecting the disabled configuration

### Requirement: Admin-only endpoint

The `POST /Telemetry/SendLogs` endpoint SHALL require elevated (admin) authorization, the same policy as the configuration page that hosts it.

#### Scenario: Non-admin caller

- **WHEN** a non-admin or unauthenticated client calls the endpoint
- **THEN** the request is rejected by the RequiresElevation policy

### Requirement: Reference code returned and quotable

On a successful send the user SHALL receive a short, human-quotable reference code (e.g. `LBX-7Q2F9K`) to optionally quote in a bug report so the maintainer can locate the bundle. Delivery is push: the bundle is stored regardless of whether the user opens a report.

#### Scenario: Successful send

- **WHEN** a bundle uploads successfully
- **THEN** the UI shows the returned reference code and a copy action, and the bundle is retrievable by that code

#### Scenario: Backend unreachable

- **WHEN** the upload fails (no connectivity, backend down)
- **THEN** the UI shows a clear error and no reference code, and the action can be retried

### Requirement: Private storage with bounded retention

Bundles SHALL be stored privately in the `log_bundles` D1 table, reachable only through the ingest Worker and scoped API tokens. A scheduled Worker job MUST delete bundles older than 90 days. The `/logs` route MUST enforce the publishable-key check, a 256 KB size cap, and the per-IP and global rate limits.

#### Scenario: Bundle pruned after retention window

- **WHEN** a bundle is older than 90 days at the daily prune
- **THEN** it is deleted from the table

#### Scenario: Oversized or unauthenticated upload

- **WHEN** a `/logs` request exceeds 256 KB or omits the valid key
- **THEN** the Worker rejects it (413 or 401 respectively) without storing anything

