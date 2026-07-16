# release-process Specification

## Purpose
TBD - created by archiving change add-release-template-and-site-releases. Update Purpose after archive.
## Requirements
### Requirement: Canonical release changelog source

The release process SHALL treat `manifest.json.versions[*].changelog` as the single canonical changelog for each version. The GitHub release body, the Jellyfin in-app plugin catalog text, and the site's releases view MUST all derive their per-version copy from this field.

#### Scenario: Maintainer prepares a release

- **WHEN** the maintainer adds a new entry to `manifest.json.versions` and pushes a matching `vX.Y.Z` tag
- **THEN** the entry's `changelog` string is used verbatim as the editorial body of the GitHub release notes and is the same string Jellyfin will display in its plugin catalog

#### Scenario: Changelog is missing or unfilled at tag time

- **WHEN** the release workflow runs for a tag whose matching `manifest.json` entry has an empty `changelog` field or a value matching the placeholder marker
- **THEN** the workflow MUST fail before publishing the GitHub release, with an error message naming the version and the field that needs to be filled in

### Requirement: Release notes template

A release notes template SHALL live at `.github/release-template.md` and define the shape of every GitHub release body. The template MUST contain placeholders for version, summary changelog, commit list, comparison URL, and install pointers.

#### Scenario: Template is rendered on tag push

- **WHEN** the release workflow runs for tag `vX.Y.Z`
- **THEN** a render step substitutes `{{VERSION}}`, `{{DATE}}`, `{{CHANGELOG}}`, `{{COMMITS}}`, and `{{COMPARE_URL}}` placeholders in `.github/release-template.md` and passes the rendered file to `softprops/action-gh-release` via `body_path:`

#### Scenario: Template change applies to future releases only

- **WHEN** the template file is modified on `main`
- **THEN** the change has no effect on existing GitHub releases (they remain as published) and applies only to releases created from subsequent tag pushes

### Requirement: Commit list since previous tag

The rendered release body SHALL include a commit list covering every commit between the previous tag and the current tag.

#### Scenario: There is a previous tag

- **WHEN** rendering release notes for tag `vX.Y.Z` and at least one prior `v*` tag exists
- **THEN** the `{{COMMITS}}` placeholder is replaced with a markdown bullet list of commits in the form `- <subject> (<short-sha>)` for every commit in `<previous-tag>..vX.Y.Z`

#### Scenario: There is no previous tag

- **WHEN** rendering release notes for the very first `v*` tag in the repository
- **THEN** the `{{COMMITS}}` placeholder is replaced with the bullet list of every commit reachable from the tag, and the `{{COMPARE_URL}}` placeholder is replaced with a link to the tag's tree rather than a compare URL

### Requirement: Maintainer release runbook

A `RELEASING.md` document SHALL exist at the repository root describing the steps to cut a release and the expected format of the manifest `changelog` field.

#### Scenario: New contributor cuts their first release

- **WHEN** a contributor with no prior context reads `RELEASING.md`
- **THEN** the document tells them exactly which version to bump, which file to edit in `manifest.json`, the expected voice and length for the `changelog` string, and the single command needed to trigger CI (`git tag vX.Y.Z && git push --tags`)

### Requirement: Removal of auto-generated release notes

The release workflow SHALL NOT use GitHub's `generate_release_notes: true` shortcut once the template-based rendering is in place.

#### Scenario: Workflow runs on a tag push

- **WHEN** `.github/workflows/release.yml` executes for any `v*` tag
- **THEN** the `softprops/action-gh-release` step uses `body_path:` pointing at the rendered template file, and `generate_release_notes: true` is absent from its inputs

### Requirement: Manifest checksum update remains automated

The release workflow SHALL continue to update the matching `manifest.json` entry's `checksum` field after the release ZIP is built, commit the change to `main`, and push it.

#### Scenario: Tag triggers a successful release

- **WHEN** the release workflow completes packaging the plugin ZIP for tag `vX.Y.Z`
- **THEN** the workflow computes the ZIP's MD5 checksum, writes it to the matching `manifest.json` entry (identified by `version == "X.Y.Z.0"`), commits with a message naming the version, and pushes to `main`

