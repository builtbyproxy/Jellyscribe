## 1. Release template and render script

- [ ] 1.1 Create `.github/release-template.md` with placeholders `{{VERSION}}`, `{{DATE}}`, `{{CHANGELOG}}`, `{{COMMITS}}`, `{{COMPARE_URL}}` following the structure in design.md Decision 3
- [ ] 1.2 Add `scripts/render-release-notes.sh` that reads the tag, extracts the matching `manifest.json` entry's `changelog` via `jq`, builds the commit list with `git log`, substitutes placeholders into the template, and writes `/tmp/release-body.md`
- [ ] 1.3 Make the script handle the no-previous-tag edge case (first release) per spec scenario
- [ ] 1.4 Add a manifest-changelog-not-empty pre-flight check to the script that exits non-zero with a clear message if the matching entry's `changelog` is empty or matches a placeholder marker

## 2. Wire the template into the release workflow

- [ ] 2.1 In `.github/workflows/release.yml`, add a step before `Create Release` that runs `scripts/render-release-notes.sh` for the current tag
- [ ] 2.2 Update the `softprops/action-gh-release@v3` step to use `body_path: /tmp/release-body.md` and remove `generate_release_notes: true`
- [ ] 2.3 Verify the manifest-checksum-commit step still runs after the release publishes (no ordering regression)

## 3. RELEASING.md

- [ ] 3.1 Create `RELEASING.md` at the repo root describing the version-bump-and-tag flow, the manifest entry format, and the expected `changelog` voice/length (reference existing entries v1.11.x as exemplars)
- [ ] 3.2 Link `RELEASING.md` from `README.md` (e.g. a "Releases" or "Contributing" section)

## 4. Site: data loading and shared release helper

- [ ] 4.1 Create `site/src/lib/releases.ts` exporting a function that imports `../../../manifest.json` and returns the versions array sorted by semver descending, with parsed fields (version, date, changelog, sourceUrl, githubReleaseUrl)
- [ ] 4.2 Add a unit-style smoke test or build-time assertion that the import path resolves and that at least one version is returned (prevents silent regressions if `astro.config.mjs` or `tsconfig.json` paths change)

## 5. Site: Releases page

- [ ] 5.1 Create `site/src/pages/releases.astro` that calls the helper and renders one card per version: version number, date, changelog prose, "View on GitHub" link, "Download ZIP" link
- [ ] 5.2 Add navigation link to Releases in the site header (`index.astro` nav and `releases.astro` header)
- [ ] 5.3 Style the page to match the existing visual language (reuse `container`, card styling, etc. from `index.astro`)
- [ ] 5.4 Verify the Releases page renders correctly under the `/jellyfin-plugin-letterboxd` base path locally with `npm run build && npm run preview`

## 6. Site: latest-version pill on home page

- [ ] 6.1 In `site/src/pages/index.astro`, import the shared release helper and read the latest version + date
- [ ] 6.2 Add a "Latest: vX.Y.Z (date) — What's new" element near the existing install pill in the hero, linking to `/releases#vX-Y-Z` (or `#latest`)
- [ ] 6.3 Ensure the element gracefully renders when `manifest.json.versions` is empty (defensive — should never happen in practice but build shouldn't crash)

## 7. Deploy-docs trigger

- [ ] 7.1 In `.github/workflows/deploy-docs.yml`, add `manifest.json` to the `paths:` filter so manifest changes (new version, checksum update) trigger a site rebuild
- [ ] 7.2 Confirm the existing `site/**` trigger is preserved

## 8. End-to-end verification

- [ ] 8.1 Cut a test patch release (e.g. `v1.11.3`) with a templated `changelog` entry; verify the GitHub release body matches the template and the site rebuilds with the new entry visible
- [ ] 8.2 Inspect the rendered release notes for layout correctness (headings render, commit list is right, compare URL works, ZIP link is correct)
- [ ] 8.3 Inspect the live site's Releases page and home page latest-version pill for the new version
- [ ] 8.4 Re-check Jellyfin's in-app plugin catalog text for the new version to confirm the changelog still reads well there

## 9. Cleanup

- [ ] 9.1 Remove or migrate any lingering references to the old auto-generated release notes flow in docs/README
- [ ] 9.2 Add a `TODOS.md` note pointing future maintainers to `RELEASING.md` for the release flow (or remove the obsolete release entries from `TODOS.md` if any exist)
