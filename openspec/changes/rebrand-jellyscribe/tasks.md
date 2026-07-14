Blocked on `add-serializd-tv-sync` shipping and settling on `main` first (see
proposal.md Sequencing). Nothing in this file executes until then.

## 0. Verify before committing to anything

- [ ] 0.1 `curl -I` the current
      `raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json`
      against GitHub's documented repo-rename redirect behavior (or a
      throwaway test rename) to confirm raw content 301s survive a repo
      rename. Record the result in this file. If redirects don't hold, keep
      the repo name and rename display + domain only.
- [ ] 0.2 Check `jellyscribe.dev` against a live registrar (design.md's
      research only ruled out project-name collisions, not registration
      status). `jellyscribe.app` and `jellyscribe.com` as fallbacks if taken.
- [ ] 0.3 Decide the exact `AssemblyName` value (`Jellyscribe` vs
      `JellyscribeSync` vs something continuity-shaped) — maintainer's call,
      see design.md's open question 3.

## 1. Visual assets

- [ ] 1.1 New `site/public/favicon.svg`: the bookmark-ribbon mark from
      design.md (brass `#C9903C` on ink `#24201A`), replacing the current
      Letterboxd-three-circles SVG.
- [ ] 1.2 App icon / OG-social image derived from the same mark, for link
      previews (Twitter/Discord/Slack unfurls of the new domain).
- [ ] 1.3 Test: a visual regression baseline capture of the new favicon at
      16/32/96px (matches the artifact's mark-scales preview) so a future
      accidental revert back to the Letterboxd mark is caught.

## 2. Site rebuild

- [ ] 2.1 `site/src/layouts/Layout.astro`: replace the `:root` CSS variables
      (`--lb-green`, `--lb-blue`, `--lb-orange`, `--jf-purple`, `--jf-blue`)
      with the new token set from design.md (`--ink`, `--paper`, `--brass`,
      `--ledger-green`, etc.). Embed Fraunces/Public Sans/JetBrains Mono as
      self-hosted `@font-face` (CDN font links won't survive the same CSP
      concerns as the artifact — self-host the woff2 files under
      `site/public/fonts/`, don't rely on Google Fonts at request time).
- [ ] 2.2 Hero copy and headline across `site/src/pages/index.astro` and
      components (`SyncFlow.astro`, `TransferBento.astro`,
      `DashboardPeek.astro`) updated for the new name and positioning.
- [ ] 2.3 Test: `$TELE/bin/visual` rebaseline against the new palette/type
      (expected, deliberate DIFF — rebaseline, don't chase it as a
      regression).

## 3. Plugin identity

- [ ] 3.1 `Plugin.cs`: display `Name` string → "Jellyscribe" (shown in
      Jellyfin's Dashboard > Plugins). GUID unchanged.
- [ ] 3.2 `Directory.Build.props` + `LetterboxdSync/LetterboxdSync.csproj`:
      `AssemblyName` → the value decided in 0.3. C# `RootNamespace` and all
      `.cs` file namespaces stay `LetterboxdSync` this release (see
      proposal.md Non-goals).
- [ ] 3.3 In-app UI strings: `configPage.html`/`userPage.html` page titles
      and any "Letterboxd Sync" copy that refers to the whole product (not
      the Letterboxd-specific tab/feature, which keeps saying "Letterboxd").
      Sidebar link label in `SidebarInjection.cs`/`sidebar.js`.
- [ ] 3.4 Test: a snapshot/string-match test asserting the plugin's `Name`
      property and the sidebar link label both say "Jellyscribe", so a future
      partial rename can't silently regress one but not the other.

## 4. Docs

- [ ] 4.1 `README.md`: title, badges (repo path in CI/release/codecov/GitHub
      badge URLs — verify these still resolve if the repo renames per 0.1),
      website link, install instructions' repository Name field.
- [ ] 4.2 `AI.md`, `CLAUDE.md`, `openspec/config.yaml`'s `context` block: any
      literal "LetterboxdSync"/"jellyfin-plugin-letterboxd" references that
      describe the product name rather than a still-accurate code namespace.
- [ ] 4.3 Private repo: `letterboxd-telemetry/AGENTS.md` and
      `AGENT_WORKFLOW.md` mental-model references to the old name (not
      committed to the public repo, but should stay accurate for future agent
      sessions).

## 5. Infra

- [ ] 5.1 `worker/` telemetry ingest Origin allowlist: add the new domain,
      keep the old one live for the 301 transition period.
- [ ] 5.2 DNS: point the new domain at the existing site host; configure
      `letterboxdsync.dev` → new domain 301. Keep the old domain registered
      (per design.md, never let it lapse to a squatter).
- [ ] 5.3 Confirm the plugin repository URL end users already have configured
      (`https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json`)
      is unchanged by any of the above. This is the one item in this whole
      change that must NOT move.
- [ ] 5.4 Test: after 5.1-5.3 land, a manual check that a fresh Jellyfin
      instance can still add the existing repository URL and see the plugin
      (proves the worker/manifest path survived the domain change).

## 6. Ship

- [ ] 6.1 One deliberate version bump (minor, not patch — a display-name and
      branding change is significant enough to signal, even though nothing
      structurally breaking changed) in `Directory.Build.props` +
      `LetterboxdSync/LetterboxdSync.csproj`.
- [ ] 6.2 `## Release notes` PR section + `site/src/data/release-notes.ts`
      entry: user-facing prose explaining the rename, reassuring existing
      users that nothing else changed (no re-install, no re-linking accounts,
      auto-update continues working).
- [ ] 6.3 GitHub repo renamed (only after 0.1 confirms the redirect holds).
- [ ] 6.4 Confirm the ~190-server fleet still auto-updates post-rename
      (download/telemetry counts keep ticking in the days after release).
