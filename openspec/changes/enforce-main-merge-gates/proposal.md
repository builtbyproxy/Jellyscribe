## Why

Every check that guards a release (Version Gate, PR Title, build-and-test) is advisory: `main` only blocks deletion and force pushes, so a red PR, an unbumped PR or a direct push can still land and trigger a release. This is audit finding CI-2, deferred because `release.yml` pushes its manifest commit straight to `main`, and a rule requiring pull requests would refuse that push. There is no GitHub issue; the audit tracks it privately.

Separately, on 2026-10-12 #166 (bumping to 2.12.4) and #167 (bumping to 2.13.0) reached `main` in one push. Release runs once per push and reads only the newest version, so 2.12.4 was never published, its release notes reached neither the GitHub release nor the plugin catalog, and the site silently dropped its `release-notes.ts` entry because no manifest version matched it.

## What Changes

- `release.yml`'s publish job pushes the manifest commit over SSH with a write deploy key held in a `release` environment, instead of with `GITHUB_TOKEN`. The deploy key is the only bypass actor for pull-request and status-check rules on `main`.
- The `main` ruleset requires a pull request (squash only, as today), requires the Version Gate, PR Title and build-and-test checks to pass on an up-to-date branch, and keeps blocking deletion and force pushes. Administrators can still bypass, but only when merging a pull request, never by pushing directly.
- Version Gate and PR Title get distinct check names (both are currently reported as `check`, which a ruleset cannot tell apart).
- When one push carries several version bumps that are not yet in `manifest.json`, the release ships the newest version and its notes combine every skipped version's `## Release notes`, oldest first. The run names the versions it folded in.
- The site attaches a `release-notes.ts` entry whose version never shipped to the next version that did, so its highlights still appear.
- One-off repair for 2026-10-12: the 2.12.4 fix notes are restored on the v2.13.0 GitHub release body. The 2.13.0 manifest changelog is left as published, because only the release workflow writes `manifest.json`.

## Non-goals

- Releasing every skipped version separately. A skipped version is superseded by the newer one in the same push, and Jellyfin offers users only the newest compatible version, so publishing it later would add nothing users can install.
- Requiring the integration (live Letterboxd) check on pull requests. It is paths-filtered and does not run on every PR, so requiring it would block docs-only PRs forever. Releases already gate on the live suite.
- Rewriting the stale tag-driven requirements in the existing `release-process` spec. They predate the merge-driven pipeline; correcting them is separate work.
- Branch rules for branches other than `main`, including the branches a `gh stack` stack targets.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `release-process`: adds merge gates enforced on `main`, a manifest push that bypasses them only through the release deploy key, and combined notes when one push carries several version bumps.
- `site-releases`: release notes for a version that never shipped are shown with the next shipped version.

## Impact

- `.github/workflows/release.yml` (build job: version bump discovery and notes; publish job: SSH push with the deploy key, `release` environment).
- `.github/workflows/version-gate.yml`, `.github/workflows/pr-title.yml` (job names).
- `site/src/lib/releases.ts` (folding unshipped notes), plus a test for it if the site has a test runner.
- Repository settings, applied with `gh api` after the workflow change merges: a write deploy key, a `release` environment with the `RELEASE_DEPLOY_KEY` secret restricted to `main`, and the updated `main` ruleset.
- `CLAUDE.md` Releasing section and the PR template's guidance.
- No plugin code changes. The whole diff is non-shipping, so no version bump or release notes.
