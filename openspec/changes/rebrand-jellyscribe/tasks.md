Was blocked on `add-serializd-tv-sync` shipping to `main` first; the
maintainer chose to bundle both into `feat/serializd-tv-scrobble` instead
(2026-07-15) rather than wait. Sections 0-4 below are executed on that
branch. Section 5 (infra: DNS/domain) and the GitHub repo rename itself
(6.3) remain explicitly deferred, the maintainer is doing the repo rename
separately, and no domain/DNS change ships until 0.1/0.2 are verified.

## 0. Verify before committing to anything

- [ ] 0.1 `curl -I` the current
      `raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json`
      against GitHub's documented repo-rename redirect behavior (or a
      throwaway test rename) to confirm raw content 301s survive a repo
      rename. Record the result in this file. If redirects don't hold, keep
      the repo name and rename display + domain only. Not run, deferred to
      whenever the maintainer does the actual repo rename.
- [ ] 0.2 Check `jellyscribe.dev` against a live registrar (design.md's
      research only ruled out project-name collisions, not registration
      status). `jellyscribe.app` and `jellyscribe.com` as fallbacks if taken.
      Not checked yet.
- [x] 0.3 Decide the exact `AssemblyName` value. **Decided: `Jellyscribe`**
      (plain, matches the product name, no continuity suffix needed). See
      design.md's open question 3.

## 1. Visual assets

- [x] 1.1 New `site/public/favicon.svg`: the bookmark-ribbon mark, gold
      `#E8B84B` on `#242833` (Midnight + Gold palette, superseding
      design.md's original brass pitch, see design.md's Palette section),
      replacing the Letterboxd-three-circles SVG. Same mark shipped inline
      in the site header/footer.
- [ ] 1.2 App icon / OG-social image derived from the same mark, for link
      previews (Twitter/Discord/Slack unfurls). Not done, no domain to
      point them at yet (blocked on 0.2).
- [ ] 1.3 Test: a visual regression baseline capture of the new favicon at
      16/32/96px. Not done, no automated visual regression harness wired up
      for `site/` yet.

## 2. Site rebuild

- [x] 2.1 `site/src/layouts/Layout.astro`: `:root` tokens rebuilt around the
      Midnight + Gold palette (`--bg`, `--gold`, `--success`, `--warn`,
      `--ink`/`--ink-dim` for the scribe flourish layer). Fraunces/Public
      Sans/JetBrains Mono/Caveat self-hosted as `@font-face` under
      `site/public/fonts/`.
- [x] 2.2 Hero copy and headline, and the full features section, rewritten
      across `site/src/pages/index.astro`. `SyncFlow.astro`,
      `TransferBento.astro`, `DashboardPeek.astro` deleted (superseded by
      the new aligned-grid `flow-row`/`travel-row`/`infra-card` sections
      built directly into `index.astro`).
- [ ] 2.3 Test: no automated visual regression harness for `site/`; verified
      manually via the `/browse` skill at 1440px and 390px instead (no
      console errors, no layout breaks).

## 3. Plugin identity

- [x] 3.1 `Plugin.cs`: display `Name` string → "Jellyscribe". `DisplayName`
      on the main config-page `PluginPageInfo` → "Jellyscribe" too. GUID
      unchanged.
- [x] 3.2 `LetterboxdSync/LetterboxdSync.csproj`: explicit
      `<AssemblyName>Jellyscribe</AssemblyName>` added. C# `RootNamespace`
      and all `.cs` file namespaces stay `LetterboxdSync` this release (see
      proposal.md Non-goals). Coordinated fixes so the rename doesn't break
      anything: `release.yml`'s package step and `deploy.sh` now zip/copy
      `Jellyscribe.dll` instead of `LetterboxdSync.dll`; `deploy.sh`'s
      plugin-directory glob checks both `Jellyscribe_*` and the old
      `LetterboxdSync_*` for the first post-rebrand deploy.
- [x] 3.3 In-app UI strings: `configPage.html`/`userPage.html`/
      `statsPage.html` page titles, `sidebar.js`'s nav link label,
      `SidebarInjectionTask`'s scheduled-task `Name`/`Category`,
      `TelemetryTask`'s description, `RepositoryMigration.cs`'s mirror-entry
      fallback name and log messages, `bug_report.yml`'s issue template.
      Left unchanged (technical/internal, not user-visible product name):
      the `[Route("LetterboxdSync/Web")]` API path, `PluginPageInfo.Name`
      route keys (`letterboxdsync`, `letterboxduser`, etc.), the
      `Jellyfin.Plugin.LetterboxdSync/...` controller route prefix used by
      the dashboard JS, and `SyncProgress.Start("Letterboxd Sync", ...)`
      (names the specific film-sync operation, parallel to "Serializd TV
      sync", not the product name).
- [x] 3.4 Test: `PluginTests.Plugin_NameIsJellyscribe` and
      `PluginTests.GetPages_ContainsConfigPage` pin `Plugin.Name` and the
      config page `DisplayName`; new
      `SidebarControllerTests.GetSidebarJs_NavLinkLabelIsJellyscribe` pins
      the embedded sidebar.js nav text.

## 4. Docs

- [x] 4.1 `README.md`: title, image alt text, install instructions'
      repository Name field, manual-install DLL filename, setup section
      path, send-logs copy, build-from-source output filename. Website link
      and all `github.com/builtbyproxy/...` badge/repo URLs deliberately
      left unchanged, repo hasn't renamed yet (0.1 still open).
      `SECURITY.md`'s dll reference fixed too.
- [x] 4.2 `CLAUDE.md`, `openspec/config.yaml`'s `context` block: product-name
      references updated, both now note the namespace/project-folder stay
      `LetterboxdSync` while `AssemblyName` and user-visible surfaces say
      Jellyscribe. `deploy.sh` dll reference in CLAUDE.md fixed too.
- [ ] 4.3 Update mental-model references to the old product name in the
      private agent-infra docs. Not done in this session, separate repo,
      separate session.

## 5. Infra

- [x] 5.1 N/A: `worker/src/index.ts` has no Origin/CORS check to update.
      The `/logs` and telemetry POST endpoints are authenticated
      server-to-server (plugin → worker via `x-lbsync-key`), and the site's
      manifest fetches (`site/src/pages/index.astro`,
      `site/src/pages/releases.astro`) run in Astro frontmatter at build
      time, not from browser JS. This task assumed an allowlist that was
      never implemented; nothing to change.
- [x] 5.2 DNS: point the new domain at the existing site host; configure
      `letterboxdsync.dev` → new domain 301. Keep the old domain registered
      (per design.md, never let it lapse to a squatter). Done via a
      Cloudflare zone (nameservers moved off Namecheap) with proxied
      placeholder A records for `@`/`www` and two Page Rules 301'ing both
      to `https://jellyscribe.dev/$1`; MX/SPF records carried over so email
      forwarding survived the cutover.
- [x] 5.3 Confirmed: `curl -I https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json`
      still returns 200 and the body still serves the `"name": "Jellyscribe"`
      manifest entry. This URL lives on `workers.dev`, entirely independent
      of the `letterboxdsync.dev`/`jellyscribe.dev` DNS cutover in 5.2, so
      it was never at risk, verified anyway per the "must NOT move" note.
- [ ] 5.4 Test: after 5.1-5.3 land, a manual check that a fresh Jellyfin
      instance can still add the existing repository URL and see the plugin
      (proves the worker/manifest path survived the domain change).

## 6. Ship

- [ ] 6.1 One deliberate version bump (minor, not patch, a display-name and
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
