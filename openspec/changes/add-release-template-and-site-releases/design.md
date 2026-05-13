## Context

The plugin ships through three coupled artifacts on every tag push:

1. **GitHub Release** — created by `softprops/action-gh-release@v3` with `generate_release_notes: true`. Body is auto-assembled from PR titles since the last tag. No template, no editorial pass.
2. **`manifest.json`** — checked into the repo. The release workflow rewrites the `checksum` for the matching version entry, commits, and pushes back to `main`. Jellyfin reads this file to discover plugin versions, and the `changelog` field is shown in Jellyfin's in-app plugin catalog.
3. **`site/`** — Astro static site deployed to GitHub Pages by `deploy-docs.yml` on pushes that touch `site/**`. Renders install instructions and feature copy. Has no awareness of releases.

The `manifest.json.versions[*].changelog` field is hand-written before tagging, in a tight, voice-consistent style (see `v1.11.2`, `v1.10.0`, etc.). It is already the highest-quality changelog source in the project — it's just not used anywhere outside Jellyfin's plugin catalog.

The site is built with Astro 5, no framework integrations, all `.astro` files. The site is built at GitHub Actions time and deployed to GitHub Pages under the base path `/jellyfin-plugin-letterboxd`. There's no client-side data fetching today; everything is static at build time.

## Goals / Non-Goals

**Goals:**
- One canonical changelog source per release. No duplication between GitHub release body, manifest changelog, and site copy.
- A reusable template so every release has the same shape: summary, what changed (categorised), upgrade notes, contributors.
- The site shows the current latest version and a browsable list of all releases without leaving for GitHub.
- Releases page updates automatically after each tag, with at most a few minutes of lag (one site build).
- Maintainer flow stays one tag push: fill in the manifest entry, push the tag, done.

**Non-Goals:**
- A CHANGELOG.md file at the repo root. The `manifest.json` entries already serve this role and adding a third source would re-introduce the duplication we're removing.
- Server-side or client-side GitHub API calls from the site. Build-time only.
- A CMS or richer changelog editing flow. Plain markdown in the template, plain string in the manifest.
- Per-release blog posts, comments, or analytics on the site beyond what already exists.
- Changing the Jellyfin manifest schema or how Jellyfin's plugin catalog renders the `changelog` field.

## Decisions

### Decision 1: Single source of truth is `manifest.json.versions[*].changelog`

The manifest's `changelog` field is already maintained at the right granularity and tone. We promote it from "Jellyfin in-app text" to "the canonical release changelog" — used by the release workflow to render the GitHub release body and by the site to render its releases view.

**Alternative considered:** Keep auto-generated release notes and have the site call the GitHub API at build time. Rejected because (a) it preserves the duplication and inconsistent voice, (b) it makes the site build dependent on GitHub API rate limits and a token, (c) it loses the editorial pass that makes the manifest changelogs readable.

**Alternative considered:** Add a `CHANGELOG.md` and have manifest+release+site all read from it. Rejected because the manifest schema can't reference an external file — Jellyfin reads the `changelog` string directly — so we'd still need to copy the entry into manifest. Two sources, two opportunities for drift.

### Decision 2: Template is a markdown file with placeholders, rendered by a small shell+jq+envsubst script

The template lives at `.github/release-template.md` with placeholders like `{{VERSION}}`, `{{DATE}}`, `{{SUMMARY}}`, `{{CHANGELOG}}`, `{{COMMITS}}`, `{{COMPARE_URL}}`. A new workflow step in `release.yml` runs `scripts/render-release-notes.sh` which:

1. Reads the new version's `changelog` string from `manifest.json` via `jq`.
2. Generates the commit list since the previous tag via `git log --pretty=format:"- %s (%h)" PREV..HEAD`.
3. Substitutes into the template and writes `/tmp/release-body.md`.
4. Passes that file to `softprops/action-gh-release` via the `body_path:` input.

**Alternative considered:** A Node script. Rejected because the existing workflow has no Node dependency for this job and `jq` is already pre-installed on `ubuntu-latest`. Shell+jq is the lower-friction tool here.

**Alternative considered:** GitHub's release-drafter action. Rejected — it works off PR labels, which we don't apply consistently, and it would still leave the manifest changelog as a separate hand-written string.

### Decision 3: Template structure

```
## {{SUMMARY_HEADLINE}}

{{CHANGELOG}}

### Commits
{{COMMITS}}

### Install
- Plugin repository: https://lachlanyoung.dev/jellyfin-plugin-letterboxd/#install
- Manual download: jellyfin-plugin-letterboxd-{{VERSION}}.zip (attached below)

**Full Changelog**: {{COMPARE_URL}}
```

The `{{CHANGELOG}}` block is the manifest's `changelog` string verbatim — already prose. Categorised sections (Fixes / Improvements / New / Breaking) are encouraged in the manifest entry but not enforced by the template; the existing entries are single-paragraph prose and that style works.

### Decision 4: Site reads `manifest.json` directly at build time via Astro's import

Astro supports `import manifest from '../../../manifest.json'` from inside `src/pages/`. The releases page does this, sorts by version descending, and renders. No fetch, no API, no token. Build is deterministic.

The `base` path in `astro.config.mjs` is `/jellyfin-plugin-letterboxd`, and the build runs from `./site` per `deploy-docs.yml`. The relative import path `../../../manifest.json` resolves to the repo root.

**Alternative considered:** Copy `manifest.json` into `site/public/` as part of the build. Rejected — direct import is simpler and Vite handles the JSON parsing.

### Decision 5: Releases view shape

- `/releases` page (Astro page at `site/src/pages/releases.astro`) listing every version in `manifest.json.versions`, newest first. Each entry: version, date, changelog prose, link to GitHub release, link to the ZIP.
- "Latest version" pill in the existing hero/install area on `index.astro`, showing the top version and date with a "What's new" link to `#latest` on `/releases`.
- No client-side filtering, no search, no pagination — there are ~10-20 versions, all fit on one scroll.

### Decision 6: Rebuild trigger

Extend `deploy-docs.yml`'s `paths:` filter to include `manifest.json`. The release workflow already commits the updated manifest to `main`; that commit will trigger a docs deploy, which rebuilds the site with the new release visible. Lag: ~2-3 minutes after the tag's release workflow completes.

**Alternative considered:** `workflow_run` trigger after the release workflow completes. Rejected because it's strictly more complex than a `paths:` glob and the outcome is the same — manifest commit triggers rebuild.

### Decision 7: RELEASING.md doc

Short doc at the repo root explaining:
1. Bump version in `LetterboxdSync/LetterboxdSync.csproj` (or wherever the version is set).
2. Add a new entry at the top of `manifest.json.versions` with `checksum: "PLACEHOLDER"` and a templated `changelog` string.
3. `git tag vX.Y.Z && git push --tags`.
4. CI takes over: builds, tests, packages, creates the GitHub release using the new template, updates the manifest checksum, commits, pushes. The docs deploy fires on that commit.

Format guidance for the `changelog` string is included: 1-3 sentences, lead with the user-facing impact, avoid implementation jargon, match the existing v1.11.x voice.

## Risks / Trade-offs

- **Risk:** The `manifest.json` `changelog` string is used in two places now (Jellyfin catalog + GitHub release body). A length or formatting choice that works in one might look off in the other.
  → **Mitigation:** The template wraps the changelog in standard markdown; Jellyfin's catalog already accepts the same text. Existing entries (v1.11.2, v1.10.0) have been spot-checked and render fine as both plaintext and markdown.

- **Risk:** Forgetting to fill in the manifest entry before tagging means CI runs against an empty/placeholder changelog.
  → **Mitigation:** Add a pre-tag check in the release workflow that fails fast if the tag's matching manifest entry has an empty `changelog` or still contains a clearly-placeholder marker. Documented in RELEASING.md.

- **Risk:** The site build pulling `manifest.json` from the repo root means the docs-deploy job needs to checkout from the right commit (the one after the manifest update). The existing workflow already does this since it triggers on push to main.
  → **Mitigation:** No special handling needed; verify with a dry run.

- **Risk:** Auto-generated release notes (current behavior) include things like "Dependabot bumped X" which are useful to some viewers. The new template's commit list preserves this.
  → **Mitigation:** The `{{COMMITS}}` section keeps the raw commit log, so the "what merged" view is still available, just below the editorial summary.

- **Trade-off:** Removing `generate_release_notes: true` means we lose GitHub's contributor list auto-generation. We add it back manually by parsing `git log --pretty=format:"%an"` for unique authors since the previous tag.

## Migration Plan

1. Land the template, render script, and `release.yml` changes. Test by tagging a no-op patch release (e.g. `v1.11.3` with a `changelog` entry like "Maintenance: testing release template rollout — no plugin behaviour changes.").
2. Inspect the rendered release. If the body looks right, proceed; if not, iterate on the template/script and re-tag.
3. Land the site changes (releases page, hero pill, `deploy-docs.yml` path filter). The site picks up all existing manifest versions on first deploy.
4. Land `RELEASING.md`.

**Rollback:** Revert the workflow change; releases fall back to `generate_release_notes: true`. The site keeps working off whatever `manifest.json` is at HEAD — no coupling to the release workflow.

## Open Questions

- Do we want a per-release `published_at` field on the site? `manifest.json.versions[*].timestamp` exists but the dates are sometimes the in-repo date rather than the actual release date. Decision: use `timestamp` as-is; if it drifts we can switch to the GitHub release's `published_at` later (build-time-fetchable without auth via the public API, but adds a network call).
- Should the template include a "Known issues" section? Decision: no — keep the template tight; known issues can go in the manifest changelog prose if relevant to that release.
