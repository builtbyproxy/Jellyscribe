## ADDED Requirements

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
