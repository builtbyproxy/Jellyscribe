# release-process Specification

## Purpose
Where every release's changelog comes from and how a release is cut: the PR's "## Release notes" section, the PR template, and the release workflow that writes the GitHub release and manifest entry; introduced with the releases page in 1.2.0 and automated in #50.
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

### Requirement: Merge gates are enforced on main

`main` SHALL accept new commits only through a squash-merged pull request whose required checks (`build-and-test`, `Version Gate`, `PR Title`, `Release scripts`) passed on a branch up to date with `main`. The only exception is the release workflow's manifest commit. Deletion and force pushes MUST stay blocked. A repository administrator MAY merge a pull request whose checks have not passed, and MUST NOT push to `main` directly.

#### Scenario: Pull request with a failing required check

- **WHEN** a pull request to `main` has a failing or missing `Version Gate` result
- **THEN** GitHub refuses to merge it for anyone but a repository administrator

#### Scenario: Direct push from a maintainer

- **WHEN** a repository administrator pushes a commit straight to `main` from their own account
- **THEN** the push is rejected

#### Scenario: Pull request behind main

- **WHEN** a pull request's required checks passed but `main` has moved since
- **THEN** the pull request cannot merge until it is brought up to date and its checks pass again

### Requirement: Required checks have distinct names

Each required check SHALL report under a name no other workflow job uses, and SHALL be required from the GitHub Actions integration only.

#### Scenario: Version Gate and PR Title both run

- **WHEN** a pull request triggers both `version-gate.yml` and `pr-title.yml`
- **THEN** their results appear as two checks named `Version Gate` and `PR Title`, and each can be required on its own

### Requirement: Release manifest commit bypasses the merge gates only through the release deploy key

The release workflow SHALL push its manifest commit to `main` over SSH using a write deploy key stored as `RELEASE_DEPLOY_KEY` in a `release` environment that only `main` can deploy from. The `main` ruleset MUST let deploy keys bypass it. The repository MUST hold no other write deploy key. No other actor than repository administrators merging a pull request MAY bypass the ruleset.

#### Scenario: Release publishes a new version

- **WHEN** the publish job adds a version to `manifest.json`
- **THEN** it pushes the commit to `main` as the deploy key, the ruleset allows it, and the push starts `deploy-docs.yml` once

#### Scenario: A workflow on a pull request branch asks for the key

- **WHEN** a job running for a branch other than `main` declares the `release` environment
- **THEN** the environment's branch rule refuses the deployment and the job never receives `RELEASE_DEPLOY_KEY`

### Requirement: Several version bumps in one push release once with every note

When the commits being released contain more than one version bump not yet listed in `manifest.json` on `main`, the release workflow SHALL publish only the newest version. Its release notes MUST be the `## Release notes` sections of every such bump's pull request, oldest first, separated by a blank line, cleaned and capped as for a single release. The run MUST name the versions it folded in. A version listed in `manifest.json`, and every older bump, MUST NOT be included.

#### Scenario: Two stacked pull requests land in one push

- **WHEN** one push brings a 2.12.4 bump (PR A) and a 2.13.0 bump (PR B), and the manifest's highest version is 2.12.3
- **THEN** the workflow releases v2.13.0 with PR A's notes followed by PR B's notes, and its log notes that 2.12.4 was folded into 2.13.0

#### Scenario: A docs-only merge follows the bump in the same push

- **WHEN** the newest commit in the push changes no version, and the bump before it raised the version to 2.13.0
- **THEN** the workflow releases v2.13.0 with that bump's notes only

#### Scenario: A bump's pull request has no release notes section

- **WHEN** one of the folded bumps has no `## Release notes` section, or its commit subject carries no pull request number
- **THEN** that bump contributes its commit subject (without the PR number) and the run warns

