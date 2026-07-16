Was blocked on `add-serializd-tv-sync` shipping to `main` first; the
maintainer chose to bundle both into `feat/serializd-tv-scrobble` instead
(2026-07-15) rather than wait. That branch has since merged to `main`
(PR #94, `2.0.0.0`, followed by the `2.1.0.0` follow-up release).
Sections 0-4, 5, and 6.1-6.3 are all done as of 2026-07-16; only 4.3
(private agent-infra docs, separate repo/session), 5.4 (needs a live
Jellyfin instance, see below), and 6.4 (too recent to confirm, re-check
in a few days) remain open.

## How to do 5.4

No Jellyfin instance is reachable from this environment, this one needs
your hands:
1. Spin up a fresh/throwaway Jellyfin server (a clean Docker container is
   easiest: `docker run -d -p 8096:8096 jellyfin/jellyfin`).
2. Dashboard → Plugins → Repositories → Add Repository, set the URL to
   `https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json`
   (the exact URL end users already have configured, confirmed unchanged
   by 5.3).
3. Go to Catalog and confirm "Jellyscribe" shows up with the current
   version, install it, confirm it loads without error.
   This proves the manifest → GitHub release asset path still resolves
   end-to-end post-rename, not just that the raw JSON serves.

## 0. Verify before committing to anything

- [x] 0.1 Confirmed empirically: the repo is already renamed to
      `builtbyproxy/Jellyscribe` on GitHub (see 6.3). `curl -I
      https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json`
      still returns 200 with the identical etag as the new-name path;
      `https://github.com/builtbyproxy/jellyfin-plugin-letterboxd` 301s
      to `.../Jellyscribe`; `git ls-remote` against the old URL resolves
      fine. GitHub's docs don't explicitly cover raw.githubusercontent.com
      redirects (only git ops and web traffic), so this was worth the
      empirical check, redirects hold in practice, no repo-name rollback
      needed.
- [x] 0.2 `jellyscribe.dev` confirmed registered via RDAP
      (`rdap.org/domain/jellyscribe.dev`): registrar Namecheap, status
      active, registered 2026-07-14, expires 2027-07-14. Already the live
      site domain (DNS/5.2), so no `.app`/`.com` fallback needed.
- [x] 0.3 Decide the exact `AssemblyName` value. **Decided: `Jellyscribe`**
      (plain, matches the product name, no continuity suffix needed). See
      design.md's open question 3.

## 1. Visual assets

- [x] 1.1 New `site/public/favicon.svg`: the bookmark-ribbon mark, gold
      `#E8B84B` on `#242833` (Midnight + Gold palette, superseding
      design.md's original brass pitch, see design.md's Palette section),
      replacing the Letterboxd-three-circles SVG. Same mark shipped inline
      in the site header/footer.
- [x] 1.2 Done. `site/public/og-image.png` (1200x630, bookmark-ribbon mark,
      Midnight + Gold palette, hero copy) wired into
      `site/src/layouts/Layout.astro`'s `og:image`/`twitter:image` meta tags
      via `new URL('/og-image.png', Astro.site)`. Confirmed live at
      `https://jellyscribe.dev/og-image.png` (200, byte-identical to the
      local file), now that 0.2 confirmed the domain.
- [x] 1.3 Done, as a manual baseline rather than automated harness (matches
      2.3's precedent, `site/` has no visual regression/CI tooling at all
      to hook into, e.g. no test framework in `site/package.json`, standing
      one up was out of scope here). Rasterized `favicon.svg` via
      `rsvg-convert` into `site/tests/visual-baselines/favicon-{16,32,96}.png`
      plus a README explaining they're manual-diff references, not
      CI-enforced.

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

- [x] 6.1 Done, but as a *major* bump not minor: PR #94 (`6cd868e`) shipped
      the rename alongside TV/Serializd support and bumped
      `AssemblyVersion`/`FileVersion` `1.19.6.0` → `2.0.0.0` in both
      `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`.
      Per CLAUDE.md's own policy the version magnitude is the breaking
      signal, not a PR-title `!`, so bundling the rename with a genuinely
      new feature surface justified 2.0.0 over a minor bump. `2.1.0.0`
      followed as a small follow-up release (dashboard credit line, TV
      telemetry, Logs tab fix).
- [x] 6.2 Done. The `v2.0.0.0` manifest changelog (confirmed live via
      `raw.githubusercontent.com/.../manifest.json`) explains the rename in
      user-facing prose and explicitly reassures on auto-update/account
      continuity/unchanged repository URL. `site/src/data/release-notes.ts`
      has the matching structured `2.0.0` entry (headline, summary, `new`
      highlights) plus the `2.1.0` follow-up entry.
- [x] 6.3 Already done: `gh api repos/builtbyproxy/Jellyscribe` and
      `gh api repos/builtbyproxy/jellyfin-plugin-letterboxd` both resolve to
      the same repo (`full_name: "builtbyproxy/Jellyscribe"`); local
      `origin` remote already points at the new URL too. Confirmed by 0.1's
      redirect test.
- [~] 6.4 Partially checked, genuinely too early to close out. D1 query
      (`install_hits`) shows manifest-poll traffic holding steady
      post-rename: 449 unique IPs polling `manifest.json` the week of
      2026-07-06, 398 the week of 2026-07-13 (v2.0.0 shipped mid-week,
      v2.1.0 shipped 2026-07-15), no cliff. But the opt-in weekly telemetry
      `pings` table shows *no* instance has reported in on `2.0.0.0` or
      `2.1.0.0` yet as of 2026-07-16, latest weekly ping is still
      `1.18.3.0` from 2026-07-13, the opt-in fleet is a small, low-single-
      digit-percent slice of the ~190 total (order of tens of pings/week vs.
      hundreds of manifest polls), and both releases are too recent (1-4
      days) for a full weekly ping cycle to have completed. Re-check the
      `pings` table in a few days for `2.x` version strings before calling
      this confirmed; the manifest-poll numbers alone are a good but
      incomplete signal (they show the fleet is still requesting updates,
      not that the update itself succeeded).
