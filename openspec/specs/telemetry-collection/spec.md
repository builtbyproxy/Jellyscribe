# telemetry-collection Specification

## Purpose
TBD - created by archiving change add-opt-in-telemetry. Update Purpose after archive.
## Requirements
### Requirement: Telemetry is opt-in and off by default

Telemetry SHALL be disabled by default. No network request to the telemetry backend may ever occur unless the admin has explicitly enabled `TelemetryEnabled`. Disabling telemetry MUST stop all pings immediately.

#### Scenario: Fresh install never phones home

- **WHEN** the plugin is installed or upgraded and the admin has never touched telemetry settings
- **THEN** no request is made to the telemetry backend, ever, regardless of how long the plugin runs

#### Scenario: Disabling stops pings immediately

- **WHEN** an admin unchecks the telemetry setting and saves
- **THEN** scheduled and error-transition pings stop, and the persisted instance UUID, counters, and error state remain locally but are no longer transmitted

### Requirement: One-time opt-in prompt

A dismissible notice SHALL appear on the Overview of the admin configuration page while telemetry is off and `TelemetryData.BannerDismissed` is false, asking the admin to enable telemetry. Its copy MUST describe the weekly ping, the exact error counts and the immediate error-transition pings, and it MUST offer an action that opens the exact-payload preview, plus Enable and No thanks. Either answer MUST set `BannerDismissed`, and so MUST any configuration save that turns telemetry on, so the notice never returns, even if telemetry is later turned off.

#### Scenario: Notice shows exactly once

- **WHEN** an admin opens the configuration page and has neither enabled telemetry nor answered the notice before
- **THEN** the notice renders; after the admin clicks Enable or No thanks, it is never shown again on any subsequent visit

#### Scenario: Opting in elsewhere answers the notice

- **WHEN** an admin turns telemetry on with the Integrations checkbox (or any other configuration save) and later turns it off
- **THEN** the notice does not reappear

### Requirement: Anonymous minimal payload

The telemetry payload SHALL contain only: schema version, plugin version, Jellyfin version, ping type, the random instance UUID, feature-toggle booleans, bucketed usage counts, per-period error-category counts, and the per-category error-state booleans. Usage counts (accounts, library size, syncs) MUST be bucketed. Error-category counts are exact integers, because the ingest Worker stores only numeric error counts and sums them within a week; the opt-in copy and the README MUST say so. The payload MUST NOT contain IP addresses, usernames, emails, film titles, or library content.

#### Scenario: Usage counts are bucketed

- **WHEN** a payload is built for an instance with 3 linked accounts, a 1,200-film library, and 37 syncs since the last ping
- **THEN** the payload reports accounts "2-4", library "500-2k", syncs-per-week "11-100", and none of those exact numbers

#### Scenario: Error counts are exact

- **WHEN** two auth failures were recorded since the last weekly ping
- **THEN** the payload reports `errors.auth_failure` as 2, alongside every other category (cloudflare_403, tmdb_lookup, jellyseerr_error, rate_limit, server_error, write_failure, parse_error, other)

### Requirement: Regenerable random instance identity

The instance UUID SHALL be generated randomly when telemetry is first enabled, never derived from hardware, network, or Jellyfin identifiers, and kept while telemetry is turned off and on again. The Integrations section MUST offer a Regenerate ID action, confirmed in the page, backed by the admin-only `POST /Telemetry/RegenerateId` endpoint, which replaces the UUID and the jitter slot and saves them. Documentation MUST state that regeneration unlinks the identifier going forward but does not erase old rows, and that configuration similarity may still allow correlation at small fleet sizes.

#### Scenario: Regenerate unlinks future pings

- **WHEN** the admin clicks Regenerate ID and confirms
- **THEN** a new random UUID replaces the old one in configuration and all subsequent pings and log bundles carry only the new UUID

#### Scenario: Regenerate requires admin

- **WHEN** a non-admin or unauthenticated caller posts to `/Telemetry/RegenerateId`
- **THEN** the request is rejected by the RequiresElevation policy and the UUID is unchanged

### Requirement: Weekly ping with jitter and week-boundary gating

A daily `IScheduledTask` SHALL send the weekly ping. Each instance SHALL have a jitter slot: a start minute picked at random from 0 to 719 (00:00 to 11:59 UTC, a 12-hour window) when telemetry is first enabled, and picked again on Regenerate ID. A run MUST NOT send the weekly ping before the instance's start minute (UTC time of day); a later daily run sends it instead. The task MUST NOT send if the server-computed week (UTC, starting Monday) of the last successful ping has not yet rolled over, preventing two different payloads from targeting the same (instance, week) slot.

#### Scenario: A run before the instance's start minute waits

- **WHEN** the UTC week has rolled over, the instance's start minute is 300 (05:00 UTC), and the task runs at 03:00 UTC
- **THEN** the task skips sending, and the first later run at or after 05:00 UTC sends the weekly ping

#### Scenario: Jitter cannot double-send within one week

- **WHEN** the previous successful weekly ping was 6.5 days ago, in the same UTC week, and the task runs past the instance's start minute
- **THEN** the task skips sending and retries on its next scheduled run

### Requirement: Persisted counters and error state

Window counters (measured since the last successful weekly ping), the last-successful-ping timestamp, and per-category error-state booleans SHALL persist in PluginConfiguration and flush on each successful ping.

#### Scenario: Restart does not zero the window

- **WHEN** the Jellyfin container restarts mid-week after 12 syncs were counted
- **THEN** the next weekly payload still reflects those 12 syncs in its bucket

### Requirement: Error-transition pings

When any error category transitions clean → failing, a ping of type `error_transition` carrying the full error-state map (every category) SHALL fire immediately, rate-limited to one per instance per day. Further transitions during a spent cap window MUST be queued and sent as one consolidated ping when the window reopens, deferred, never silently dropped client-side. Recovery (failing → clean) does not fire a ping; it is visible in the next weekly payload.

#### Scenario: Second category trips during the cap window

- **WHEN** cloudflare_403 transitions at 09:00 (ping sent) and tmdb_lookup transitions at 14:00 the same day
- **THEN** the plugin queues the change and sends one consolidated transition ping carrying both categories' state after the daily window reopens

### Requirement: Payload preview and diagnostic bundle

An admin-authorized endpoint `GET /Telemetry/Preview` SHALL return the exact JSON the next weekly ping would send, rendered in a dialog opened from Integrations (and from the opt-in notice). The dialog MUST offer Copy (the JSON exactly as returned) for bug reports and MUST warn that it contains the instance UUID (pasting it publicly links that identity to the instance's ping history), offering Copy + regenerate ID, which copies first and regenerates only if the copy succeeded.

#### Scenario: Preview requires admin

- **WHEN** a non-admin or unauthenticated caller requests `/Telemetry/Preview`
- **THEN** the request is rejected with the same authorization policy as the plugin configuration page

### Requirement: README documents the exact payload

The README SHALL contain a Telemetry section showing the full example payload (every error category), the precise anonymity wording (no IPs/usernames/emails/titles in the dataset; usage counts bucketed, error counts exact; platform transport logs are the platform's, retained per its policy), the opt-in default, the error-transition pings, and the regeneration semantics.

#### Scenario: A skeptical user audits the claim

- **WHEN** a user reads the README Telemetry section and clicks Preview in settings
- **THEN** the documented payload shape and the previewed JSON match field-for-field

