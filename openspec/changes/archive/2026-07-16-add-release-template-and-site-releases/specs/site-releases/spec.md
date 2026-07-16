## ADDED Requirements

### Requirement: Releases view on the site

The Astro site SHALL expose a Releases view listing every version present in `manifest.json.versions`, ordered newest first. The view MUST be reachable from the site's primary navigation.

#### Scenario: Visitor opens the Releases view

- **WHEN** a visitor navigates to `/jellyfin-plugin-letterboxd/releases` (or the equivalent in-page section on `index.astro`, depending on implementation choice in design)
- **THEN** the page renders one entry per version in `manifest.json.versions`, each showing the version number, release date, changelog prose, a link to the GitHub release page, and a link to the ZIP

#### Scenario: Ordering

- **WHEN** the Releases view renders
- **THEN** entries appear in descending version order (semver-aware), with the most recent at the top

### Requirement: Latest version surfaced on the home page

The home page (`site/src/pages/index.astro`) SHALL display the current latest version near the install instructions, with a link to the full Releases view.

#### Scenario: Visitor lands on the home page

- **WHEN** a visitor opens the site root
- **THEN** the hero or install area shows the latest version number and release date, and a link or button leads to the Releases view

#### Scenario: Latest version changes

- **WHEN** a new version is appended to `manifest.json.versions` and the site is rebuilt
- **THEN** the home page displays the new latest version on next deploy without any code change

### Requirement: Build-time data source

The Releases view and the home page's latest-version display SHALL read from `manifest.json` at build time. The site MUST NOT make runtime or build-time network calls to the GitHub API to fetch release data.

#### Scenario: Site builds in CI

- **WHEN** the `deploy-docs.yml` workflow builds the site
- **THEN** Astro imports `manifest.json` directly from the repository (no HTTP request, no GitHub token required), and the built artifact contains release data baked in as static HTML

#### Scenario: Working offline

- **WHEN** a contributor runs `npm run build` in `site/` with no network access
- **THEN** the build succeeds and the Releases view renders correctly from the local `manifest.json`

### Requirement: Site rebuild on manifest changes

The `deploy-docs.yml` workflow SHALL rebuild and redeploy the site whenever `manifest.json` changes on `main`, in addition to the existing trigger on `site/**` changes.

#### Scenario: Release workflow commits a manifest update

- **WHEN** the release workflow pushes a commit to `main` that modifies `manifest.json` (either a new version entry or a checksum update)
- **THEN** `deploy-docs.yml` runs and deploys an updated site within one build cycle of the commit landing

#### Scenario: Site-only change

- **WHEN** a commit to `main` modifies only files under `site/**` and not `manifest.json`
- **THEN** `deploy-docs.yml` still runs (existing behaviour preserved)

### Requirement: Rendering of changelog content

Each release entry on the site SHALL render the `manifest.json` entry's `changelog` field as a paragraph of prose, preserving the text as written.

#### Scenario: Plain-prose changelog

- **WHEN** the `changelog` field is a single paragraph of plain text
- **THEN** the site renders it as a paragraph with normal typography (no code formatting, no truncation)

#### Scenario: Changelog with inline markdown

- **WHEN** the `changelog` field contains inline markdown (links, code spans, emphasis)
- **THEN** the site MAY render that inline markdown, but at minimum MUST render the text faithfully without breaking the page layout

### Requirement: No coupling to GitHub release availability

The Releases view SHALL render correctly even if the GitHub release for a given version has not yet been published or is unreachable.

#### Scenario: Version present in manifest but GitHub release missing

- **WHEN** a version exists in `manifest.json.versions` but no corresponding GitHub release exists yet
- **THEN** the Releases view still renders the version entry using manifest data; the "View on GitHub" link points at the standard release URL (the link works once the release publishes) and the page does not error
