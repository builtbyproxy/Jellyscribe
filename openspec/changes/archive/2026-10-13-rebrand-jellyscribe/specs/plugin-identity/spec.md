## ADDED Requirements

### Requirement: Existing installs auto-update through the rename
This rebrand SHALL NOT change the plugin's GUID or the manifest URL end users
already have configured in their Jellyfin repository list. Jellyfin keys
plugins by GUID, not by display name or assembly name, so an unchanged GUID is
what lets an already-installed instance pick up the renamed release as a
normal update rather than requiring reinstall.

#### Scenario: Existing server checks for updates post-rename
- **WHEN** a Jellyfin server with the plugin already installed (old display
  name, old assembly name) polls its configured repository URL after the
  rebrand release ships
- **THEN** it resolves the plugin by GUID, sees a newer version available, and
  updates in place — the user sees the plugin's display name change to
  "Jellyscribe" in their Dashboard, with no reinstall and no reconfiguration

#### Scenario: Repository URL is untouched by the rename
- **WHEN** any part of this change (repo rename, domain change, site rebuild,
  display name change) ships
- **THEN** `https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json`
  — the URL users already have entered under Dashboard > Plugins >
  Repositories — continues to serve the same manifest schema at the same
  address; nothing in this change edits or replaces it

### Requirement: Rebrand carries no functional or data changes
This change SHALL be display identity only: sync behavior, dedupe/history
format, encrypted secret format, and stored `PluginConfiguration` shape MUST
remain unaffected.

#### Scenario: Config survives the update untouched
- **WHEN** a server updates to the rebranded release
- **THEN** every previously configured Letterboxd and Serializd account,
  encrypted credential, and sync history entry reads back identically; no
  migration step runs

### Requirement: New identity replaces borrowed brand elements
The plugin's display name, favicon, and site palette SHALL NOT use another
service's name, logo, or brand colors. This closes the trademark exposure the
prior identity carried (Letterboxd's own three-circle mark and brand colors
used as this plugin's own).

#### Scenario: Favicon no longer matches Letterboxd's mark
- **WHEN** the rebrand ships
- **THEN** `site/public/favicon.svg` is the bookmark-ribbon mark in the new
  palette (`#C9903C` on `#24201A`), not Letterboxd's circles in Letterboxd's
  colors (`#00E054`/`#40BCF4`/`#FF8000`)

#### Scenario: Site palette no longer names itself after other brands
- **WHEN** the rebrand ships
- **THEN** `site/src/layouts/Layout.astro`'s CSS custom properties use the new
  token names (`--ink`, `--paper`, `--brass`, `--ledger-green`, etc.), not
  `--lb-green`/`--jf-purple`/`--jf-blue`
