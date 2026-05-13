## Why

Release notes today are whatever `softprops/action-gh-release` auto-generates from merged PRs, plus a hand-written changelog string that's duplicated into `manifest.json`. The voice and shape drift between releases, the site has no view of release history at all, and a user landing on the site has to leave for GitHub to see what's new or what version they're about to install. A consistent release template plus a site-side releases view fixes both problems with one source of truth.

## What Changes

- Add a GitHub release notes template (`.github/release-template.md` or equivalent) defining the standard sections: summary one-liner, what changed (Fixes / Improvements / New / Breaking), upgrade notes, and a contributors line.
- Replace `generate_release_notes: true` in `.github/workflows/release.yml` with a templated body assembled from a structured source (the same per-version `changelog` already in `manifest.json`, plus auto-generated commit list rendered into the template).
- Promote `manifest.json`'s per-version `changelog` field to the canonical changelog source — the release workflow reads it and renders the GitHub release body from the template.
- Add a CONTRIBUTING/RELEASING note documenting the template, the version-bump-and-tag flow, and the requirement that the new manifest entry's `changelog` follow the template summary style before tagging.
- Add a "Releases" / "What's new" section to the Astro site (`site/`) that fetches and renders releases. Source of truth is `manifest.json` (already checked into the repo and deployed with the site build), so no runtime GitHub API call is needed — the site reads `manifest.json` at build time.
- Link the latest release prominently in the hero install area (current latest version, date, "what's new" expandable), and add a `/releases` (or in-page `#releases`) view listing all versions with their templated changelog.
- Trigger a site rebuild whenever `manifest.json` changes so the latest release appears on the site shortly after a tag lands. The release workflow already commits the updated `manifest.json` to `main`; extend `deploy-docs.yml` `paths:` to include `manifest.json`.

## Capabilities

### New Capabilities
- `release-process`: The repeatable shape of every release — template, source of truth, workflow steps, and the contract between manifest, GitHub release, and the site.
- `site-releases`: How the Astro site surfaces release history and the current version to visitors, including the build-time data source and the rebuild trigger.

### Modified Capabilities
<!-- No existing specs in openspec/specs/ to modify. -->

## Impact

- **Workflows**: `.github/workflows/release.yml` (body template rendering replaces `generate_release_notes`), `.github/workflows/deploy-docs.yml` (add `manifest.json` to `paths`).
- **New files**: release notes template under `.github/`, a small render script (shell or node) invoked from `release.yml`, a `RELEASING.md` doc, new Astro page/section + component for releases.
- **Site**: `site/src/pages/` gains a releases view; the existing `index.astro` hero/install area gains a "latest version" pill that links to it.
- **Data flow**: `manifest.json`'s `changelog` field becomes load-bearing for both Jellyfin's in-app plugin catalog *and* the GitHub release body *and* the site — its format (length, voice) becomes part of the release checklist.
- **No breaking changes** for plugin users; the manifest schema is unchanged. Existing released versions stay as-is.
- **Maintainer flow** changes: before tagging, fill in the new manifest entry's `changelog` in the template-friendly format; everything else is automated.
