# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jellyfin plugin ("Jellyscribe") that syncs watch history to Letterboxd (film) and Serializd (TV). C#/.NET 9, targets Jellyfin 10.11.9 or newer (`Jellyfin.Controller`/`Jellyfin.Model` 10.11.9, matching `targetAbi.txt`). Letterboxd's official endpoint (`/api/v0/production-log-entries`) is preferred; the plugin falls back to web scraping (cookie login, CSRF tokens, HtmlAgilityPack) when the API path fails. Serializd's API needs no such fallback. The C# namespace, project folder, and solution file all still say `LetterboxdSync` (pre-rebrand name, unchanged this release, see `openspec/changes/rebrand-jellyscribe/`); only the compiled `AssemblyName` and every user-visible surface say Jellyscribe.

The sidebar link in the Jellyfin web UI is injected by `SidebarScriptStartupFilter`, an `IStartupFilter` middleware that adds the `sidebar.js` tag to the web client's index page at request time (the same approach as Jellyfin Enhanced), so no other plugin is needed. The third-party **File Transformation** plugin, when installed, injects the same tag; both paths share `SidebarScript.Inject`, which never adds a second copy. `sidebar.js` is the whole client: it adds the entry points (the sidebar before `.btnSettings` on 10.11, a clone of the avatar menu's Settings item in `#app-user-menu` on 12) and the in-app page at `#/jellyscribe`, which mounts `userPage.html` (fetched from Jellyfin's own `web/ConfigurationPage?name=letterboxduser`) into a `.mainAnimatedPage` of its own, the pattern Jellyfin Enhanced's Bookmarks uses. Capture-phase `hashchange`/`popstate` listeners keep Jellyfin's router from rendering "Page not found" for the route; the dashboard is unmounted when the page is left; anything that stops it mounting falls back to the configuration page, as does finding the user or admin (`#letterboxdSyncConfigPage`) dashboard already cached in the document. The two dashboards share element ids, so `userPage.html` looks up every element through its own root (`byId`), never `document`. No JS test harness exists: verify client changes with a Playwright run against throwaway Jellyfin 10.11 (with a base URL) and 12 containers.

## Build & Test

```bash
dotnet build -c Release
dotnet test  -c Release --verbosity normal
```

Run a single test class or method (xUnit, `dotnet test` filter syntax):

```bash
dotnet test --filter "FullyQualifiedName~ScraperTests"
dotnet test --filter "FullyQualifiedName~ScraperTests.LookupBySlug_Returns_Result"
```

CI also collects coverage via `--collect:"XPlat Code Coverage"` into `TestResults/`; Codecov consumes the Cobertura XML.

Deploy a build to a Jellyfin server: `JELLYSCRIBE_DEPLOY_TARGET=user@host ./deploy.sh` (scp's `Jellyscribe.dll` + `HtmlAgilityPack.dll` and restarts the container; `JELLYSCRIBE_DEPLOY_PLUGINS_ROOT` overrides the plugins path).

Live tests (`Category=Integration`, setup in `LetterboxdSync.Tests/Integration/README.md`) hit real letterboxd.com with a dedicated test account. CI runs them through the reusable `live-tests.yml` in three places: on PRs that touch plugin code (`integration.yml`), every Monday at 06:00 UTC against main (`live-checks.yml`, which opens or comments on one issue labelled `live-check-failure` when it fails), and in `release.yml` before anything is published (see Releasing). Each run's summary says whether the website-login fallback (the scraping path) was covered: its two tests need the `LETTERBOXD_TEST_RAW_COOKIES` secret and skip without it, and the run still passes.

Refreshing `LETTERBOXD_TEST_RAW_COOKIES`. Cloudflare cookies expire, so expect to repeat this whenever the summary warns that the secret is set but the website sign-in did not get through:

1. In a private browser window, sign in to letterboxd.com as the CI test account (never a personal account).
2. Make the tests send that browser's user agent: copy the browser's exact User-Agent string into the `LETTERBOXD_TEST_USER_AGENT` secret. Without that secret the tests send the plugin default (`LetterboxdHttpClient.DefaultUserAgent`), and Cloudflare refuses a `cf_clearance` cookie presented with a different user agent from the one that earned it.
3. In DevTools (Network tab), reload a letterboxd.com page and copy the request's whole `Cookie` header value. It must include `cf_clearance` and the Letterboxd session cookie.
4. Save it as the `LETTERBOXD_TEST_RAW_COOKIES` repository secret (Settings, Secrets and variables, Actions), then run "Live checks (weekly)" from the Actions tab and check the summary says the scraping path was covered.

Cloudflare also pins `cf_clearance` to the IP address that solved the challenge (README, "Cloudflare issues"), and a GitHub-hosted runner never shares the browser's IP, so even a fresh cookie may be refused there. The scraping tests then skip with the sign-in error (they do not fail) and the summary says the path was not covered. If that keeps happening, dependable coverage needs a self-hosted runner on the same network as the browser that made the cookie.

The telemetry/download Worker (`worker/`, deployed by hand with wrangler) has dependency-free tests for its pure helpers: `node --experimental-strip-types --test worker/test/dl.test.mjs` (Node 22.6+; CI runs it in the `worker-tests` job). Merging a worker change does not deploy it, so the maintainer runs `npx wrangler deploy` from `worker/` afterwards.

## Architecture

### Service abstraction with fallback

`ILetterboxdService` (`ILetterboxdService.cs`) is the seam every caller uses. Two implementations:

- `LetterboxdApiClient`, preferred, talks to Letterboxd's JSON endpoints.
- `ScrapingLetterboxdService`, fallback, composes `LetterboxdHttpClient` (cookies/CSRF/Cloudflare retry), `LetterboxdAuth` (login + re-auth on 401), `LetterboxdScraper` (HTML parsing, film lookup, diary/watchlist scraping), and `LetterboxdDiary` (diary writes, review posting).

`LetterboxdServiceFactory.CreateAuthenticatedAsync` tries the API first and silently falls back to scraping if auth fails; when the website login then works, it skips the API for that account for two hours, and it keeps each account's website cookie jar in memory so later events reuse the session. That is the only fallback: a method that throws later is never retried on the other implementation, so a capability gap must be handled when the service is selected, not at call time. `FilmResult.FilmId` is the LID on the API path and the numeric id on the scraping path; pass it only back to the instance that returned it. Both API clients (Letterboxd and Serializd) wait out a 429 once, for at most 60 s of Retry-After (`RetryAfterLimit`); a longer one fails the item, and the Letterboxd runner counts it as a block. The Letterboxd retry is signed again with a fresh nonce. The optional `CancellationToken` on the service methods the runners call cancels waits and reads, never a write already sent, which may have landed. The factory also exposes an `internal static OverrideForTesting` hook used via `InternalsVisibleTo` from `LetterboxdSync.Tests` to inject mock services, production code never touches it.

### The bundled Letterboxd API key

`LetterboxdApiConstants.cs` holds the API key and HMAC secret for `api.letterboxd.com`. `LetterboxdApiClient.SendSignedAsync` adds the key and a signature made with the secret to every API request: token requests and bearer-authenticated calls alike. They arrived in v1.6.0 together with the website-login fallback that takes over if they stop working. "Official API" in the code, logs and docs means Letterboxd's own JSON API as opposed to the website, not an integration Letterboxd has approved.

Treat the key as public. It ships in every `Jellyscribe.dll` and a decompiler shows it, so keeping it out of the source (for example injecting it from a CI secret at build time) would not hide it. If Letterboxd rotates or revokes it, every install falls back to the website login: new sign-ins through `LetterboxdServiceFactory.CreateAuthenticatedAsync`, and accounts that are already signed in once their cached token expires (up to about an hour). The weekly live check's `ApiClient_ValidCredentials_AuthenticatesWithTheBundledKey` test is the early warning. Symptoms and the patch-release runbook are in `SECURITY.md` ("The bundled Letterboxd API key"). Do not add remote key loading.

### Sync entry points

- `SyncTask`, scheduled, exports recent watches to the Letterboxd diary.
- `Serializd/SerializdSyncTask` / `SerializdSyncRunner`, scheduled, exports played episodes to Serializd.
- `WatchlistSyncTask` / `WatchlistSyncRunner`, imports the user's Letterboxd watchlist as a Jellyfin playlist.
- `Serializd/SerializdWatchlistSyncRunner`, mirrors a Serializd watchlist into a per-user playlist and a collection. Collections are server-wide and Jellyfin derives a collection's id from its name, so each account's collection is tracked by id in `SerializdCollectionStore` and never found by name (one upgrade rule adopts the old shared "Serializd Watchlist" only when a single account could have written it). Watchlist and playlist names are admin-only.
- `SeerrClient` creates requests with `X-API-User` set to the requester and no body `userId`, so Seerr applies that user's own auto-approve rights; `AutoApproveJellyseerrRequests` then approves what stays pending, with the admin key and no `X-API-User`.
- `DiaryImportTask`, marks Jellyfin items as played if present in the Letterboxd diary; it waits on `SyncGate` while another Letterboxd sync runs.
- `PlaybackHandler`, `IHostedService` registered in `ServiceRegistrator`, fires the real-time sync on playback completion.
- `RatingSyncHandler`, `IHostedService` registered beside it, subscribes to `IUserDataManager.UserDataSaved` and pushes movie rating changes to the member's Letterboxd film rating (`ILetterboxdService.SetFilmRatingAsync`, not a diary entry). Numeric ratings save with `UpdateUserData` and favorite/like toggles with `UpdateUserRating`, and the event carries no previous value, so it seeds the current movie ratings in memory at startup (only for users with an account that syncs ratings; users who turn it on later are seeded when the configuration is saved, and their saves only update the baseline until then) and queues only saves whose rating differs from that baseline; `RatingPushStore` (last value pushed per account, persisted) then gates each push; plugin-originated rating writes (diary import, the review-modal writeback) save with `Import`, which it takes as the new baseline but never pushes. Debounced 10s per (user, film) by one sweep loop that reuses one login per account per pass, paces pushes, and gives a failed push up to 3 attempts in total, with backoff (auth failures are not retried; they feed the breaker). Successes record `SyncStatus.Rated` (never `Success`, which the diary duplicate backstop and stats read); failures are logged only, because `Failed` events feed the diary runner's per-film abandon counter. The review modal's rating-only submit (`POST Review` with stars but no text and no rewatch) makes the same `SetFilmRatingAsync` call for each chosen account and records it the same way (`Rated`, `RatingPushStore`, never `Failed`).
- `LetterboxdSyncRunner`, shared engine used by `SyncTask` and `PlaybackHandler`; `SyncGate`, `SyncHistory`, `SyncProgress`, and `TmdbCache` coordinate dedupe, progress UI, and TMDb lookups.
- Both Letterboxd export paths log a watch on its day in the server's time zone (`Helpers.ToLocalViewingDate`), hold `FilmSyncLock` per (user, account, film), and scope every `SyncHistory` lookup by the Letterboxd account (`SyncEvent.Account`; rows without one count for every account of the user). A diary check that fails throws `DiaryCheckFailedException` rather than reporting "not logged". A film is abandoned after 3 `PermanentFailure` rows in a row (Letterboxd has no film for the TMDb id), or after 10 other failures in a row spread over at least 7 days; a run where every film fails, at least one of them transiently, is marked `Outage` and counts toward neither. Every watchlist reader (API, website and Serializd) throws on a partial read, so the watchlist syncs change nothing that run.
- `LibraryExclusion.IsExcluded` is the one per-account "excluded library" rule (issue #124). Every export path (both scheduled runners, `PlaybackHandler`, and `RatingSyncHandler`) calls it before handing an item to a service client, so it is a pre-filter above `ILetterboxdService`, never a check inside either implementation. Import paths deliberately ignore it.

### Plugin surface

- `Plugin.cs` + `ServiceRegistrator.cs` register services and config.
- `Api/LetterboxdController.cs` exposes the REST endpoints the dashboards call; `Api/SidebarController.cs` serves the static web assets (`sidebar.js`, `jellyscribe.js`, `jellyscribe.css`), the only anonymous routes. `LetterboxdController` also serves the read-only `ItemRating` endpoint the review modal uses to pre-fill its stars from the caller's stored Jellyfin rating.
- `POST Review` with text (not a rewatch, no date given, TMDb id known) holds `FilmSyncLock` for that user, account and film, then looks up the latest `Success`/`Rewatch` history row for that user, account and film. When one exists, `ILetterboxdService.AddReviewToDiaryEntryAsync` puts the review on Letterboxd's entry for that viewing date (API path: `GET /log-entries?member=&film=`, every page and read a second time after 3s when nothing is on that date because diary reads lag writes, then `PATCH /log-entry/{id}` with only `review` and `rating`; an entry that already has a review is never overwritten and the request is refused). The website session keeps the interface default, `Unsupported`, so it and a missing entry fall back to a new entry dated on the row's viewing date, never today, and the per-account reply carries a `note` saying so. Any other review is a new entry sent with an explicit date (today is the server's day, `Helpers.ToLocalViewingDate`). The review's history row records the real TMDb id, account and viewing date, so the diary sync's duplicate checks see it.
- `Api/LoginCheckLimiter.cs` rate-limits both Verify (login check) endpoints in memory: 5 failed checks per Jellyfin user, 20 checks of any outcome per user, and 20 failed checks server-wide, per service per 10 minutes, answering 429. `ControllerAuthorizationTests` pins which actions are anonymous and which are admin-only, so a new action has to be added there on purpose.
- Stored secrets (Letterboxd and Serializd passwords, raw cookies, the Seerr API key) are write-only: the plaintext properties are `[JsonIgnore]`, responses carry only `Has*`/`has*` flags, and `Configuration/SecretMerge.cs` turns every save back into the intended change (blank keeps, typed replaces, `ClearRawCookies`/`ClearJellyseerrApiKey` drop, and the write-only `Original*` fields carry secrets across a rename or an admin's owner change). `Plugin.UpdateConfiguration` applies it to Jellyfin's own plugin-config POST. Both Verify endpoints use the stored password when the field is blank, and `TestJellyseerr` sends the stored key only to the stored URL.
- `Api/LibrariesController.cs` lists the film, TV, and mixed libraries the caller can access, for the per-account "Excluded libraries" checklist on both settings pages (Jellyfin's own `/Library/VirtualFolders` is admin-only).
- `Web/*.html`, `Web/*.js` and `Web/*.css` are embedded resources (see `LetterboxdSync.csproj`). `configPage.html` and `userPage.html` are the plugin's config pages; what they share (most of the code, every common style, and the bundled WS Sans/WS Mono fonts) lives in `jellyscribe.js` and `jellyscribe.css`, which `SidebarController` serves anonymously as static files. Each page loads them with `?v=<version>`, reuses a loaded copy only under that version, and starts with the factory `jellyscribe.js` registered under it, so after an upgrade a page never runs against an older shared script left in the tab. The build stamps the version into both pages and `jellyscribe.js` (the `StampWebAssetVersion` target replaces `@@JELLYSCRIBE_VERSION@@`; a Debug build stamps `<version>-dev`), and the server caches the files for good only under this build's version. A fix to shared behaviour goes in `jellyscribe.js` once; page-specific code (how each page talks to the server, its own sections) stays in the page. Both dashboards scope every CSS rule to their root (never `html`/`body`), pick their light or dark palette from the background Jellyfin's theme paints (`applyTheme`; the OS setting only outside Jellyfin), and use native `<dialog>` modals opened with `showModal()`. `DashboardMarkupTests` pins their contrast (4.5:1 in both palettes), scoping, phone layout and keyboard markup.
- Telemetry and log bundles (`Telemetry/TelemetryService.cs`, the `Telemetry/*` actions in `LetterboxdController`): the admin page previews the exact ping (`Telemetry/Preview`) and log bundle (`Telemetry/PreviewLogs`, built by the same method as the send) before anything leaves the server, and `Telemetry/RegenerateId` replaces the instance id. Never log a Serializd email: name the account with `LogRedaction.AccountTag`. The shared log reader masks emails with `LogRedaction.RedactEmails` (encoded forms too) and cuts review-reply bodies older releases logged, so the Logs tab, the preview and the upload match. Error counts in the payload stay exact integers (the ingest Worker drops non-numbers); the docs say so.
- `SidebarScriptStartupFilter.cs` injects the sidebar link without any other plugin (kill switch: `PluginConfiguration.DisableSidebarScriptMiddleware`, XML only). `SidebarInjection.cs` also registers the same injection with the File Transformation plugin when it is installed.

## Releasing

**Every merge to `main` that changes what ships, ships a release.** No manual tag pushes, no release-notes files. **We only release when a change affects the user; non-shipping PRs are exempt**: if the whole diff sits in non-shipping paths (any `*.md`, `docs/`, `openspec/`, `site/`, `worker/`, `.github/`, `LetterboxdSync.Tests/`), skip the version bump, the `## Release notes` section, and the `release-notes.ts` entry; the merge then ships no release (release.yml sees the version already listed in `manifest.json` and stops) and the site still redeploys for `site/**` changes via deploy-docs' push trigger. `manifest.json` is never exempt. The full pipeline is:

1. Open a PR. Unless non-shipping (above), the PR must:
   - Have a **Conventional Commits** title (`feat:`, `fix:`, `chore:`, `docs:`, `ci:`, `refactor:`, `test:`, `perf:`, `build:`, `style:`). Enforced by `pr-title.yml` (this one applies to non-shipping PRs too).
   - **Bump `AssemblyVersion` / `FileVersion`** in both `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`. Patch bumps (e.g. `1.13.0.0` → `1.13.1.0`) are fine for CI / refactor changes. The new version must be higher than main's and not already tagged. Enforced by `version-gate.yml`.
   - Fill in the **`## Release notes`** section in the PR body. `release.yml` extracts text between that heading and the next H2 and uses it verbatim as the manifest changelog field and the GitHub Release body. The PR template primes the section so it's the path of least resistance. Past entries on https://jellyscribe.dev/releases set the tone: one paragraph, user-facing prose, no symbol names / internal jargon.
   - Add a structured entry to **`site/src/data/release-notes.ts`** for the new version (headline + summary + categorised highlights). The site renders these on the Releases page above the raw manifest changelog. Same tone as the manifest changelog but split into `new` / `improvements` / `fixes` / `breaking` bullets.
   - **SDK floor policy (issue #63)**: the `Jellyfin.Controller`/`Jellyfin.Model` PackageReference version MUST equal `targetAbi.txt`, Jellyfin assemblies have per-patch AssemblyVersions, so the SDK we compile against is the real minimum Jellyfin a release can load on. Never bump the SDK routinely (Dependabot PRs are a compile signal, not a merge queue); bump it only when we need a newer API, raising `targetAbi.txt` and the minor version in the same PR. CI enforces the SDK==targetAbi match.
   - **Jellyfin 12 cliff (verified 2026-07-06)**: the 12.x SDK packages are net10.0-only, they do NOT restore against this net9.0 project (NU1202). Current net9.0 builds run fine on Jellyfin 12 servers (newer runtime loads older assemblies), but compiling against the 12 SDK forces net10.0, which cannot load on 10.11's .NET 9 host, so adopting the 12 SDK is a one-way release-stream split, never a routine bump. Staged plan: `openspec/changes/add-jellyfin-12-support/`.

2. Merge with **Squash and merge**. The squash subject is the PR title with `(#NN)` appended; the release workflow extracts the PR number from that and fetches the PR body via `gh pr view` (the squash commit body itself is not reliable across merge methods).

3. `release.yml` fires automatically on the push to `main` (one run at a time, `concurrency: release`). It reads `AssemblyVersion` from `Directory.Build.props` and stops if `manifest.json` on main already lists it. A read-only `build` job (no stored credentials) builds, tests, packages, and extracts the `## Release notes` section of the PR that bumped the version (comments and HTML stripped, capped at 4000 characters); a `live-check` job then runs the live Letterboxd suite (`live-tests.yml`) against the exact commit that was packaged, and a new release is not published unless it passes (it passes, and publish warns, when the test secrets are absent; setting the repository variable `RELEASE_LIVE_CHECK` to `off` bypasses it while letterboxd.com is down or the API key is revoked, and the publish job warns when it does); a `publish` job with `contents: write`, which runs no repository build code, creates the GitHub Release at the built commit and pushes the manifest entry (using `targetAbi.txt`), retrying on a fresh main if the push races. If the tag exists but the manifest entry does not (a run that died half way), a rerun repairs it: when the release already has its asset, it skips the rebuild, reuses that asset's checksum and only writes the manifest; otherwise it rebuilds from the tag and uploads the missing asset. Actions are pinned to commit SHAs; Dependabot's github-actions entry keeps them current.

4. `deploy-docs.yml` fires via `workflow_run` on Release completion, rebuilding jellyscribe.dev with the fresh manifest. (The `GITHUB_TOKEN`-authenticated auto-commit can't fire push-based workflows, hence the explicit `workflow_run` trigger.)

5. **Archive the OpenSpec change.** Once a PR that implements an OpenSpec change has merged and its release is out, archive that change straight away, in a follow-up docs-only PR. This is not optional; see the OpenSpec section below for the steps.

### Breaking changes

The version-bump magnitude is the canonical signal, not a `!` in the PR title. Going `1.x.y` → `2.0.0` means breaking; we do not use `feat!:` / `fix!:`.

### Past incidents this pipeline prevents

- **v1.12.0.0** manifest entry was merged to main with `"checksum": "PLACEHOLDER"` and no tag ever got pushed, leaving the manifest advertising a 404'ing release for every user. `release.yml` is now the *only* writer of `manifest.json`, and `ci.yml`'s manifest validator refuses any PR that touches it with a `PLACEHOLDER` or 404 `sourceUrl`.
- **v1.13.0** was cut manually after the merge, which works but doesn't enforce that *every* merge ships. The version-gate now guarantees a release on every merge.
- **v1.12.0 and v1.13.0 site staleness**: the manifest auto-commit didn't trigger Deploy site (GitHub token limitation). The `workflow_run` trigger now fires Deploy site after every Release.
- **v1.13.0 SDK ABI break**: Jellyfin 10.11.9 removed `IUserManager.Users` (replaced by `GetUsers()`), so v1.13.0 bumped the SDK to 10.11.10 and `targetAbi.txt` to `10.11.9.0`. See `feedback_jellyfin_plugin_abi_break` in the user's memory.
- **v1.13.0 and v1.13.1 changelog drift**: v1.13.0's manifest changelog was written as a multi-paragraph incident report (markdown headings, code backticks, "MissingMethodException", PR refs) instead of the single-paragraph user prose of v1.0–v1.12. v1.13.1's was even worse: just the squash-merge commit subject `ci: enforce version bump on every PR, auto-release on merge, rebuild site on release (#50)`, because the earlier pipeline read `git log -1 --format='%B' HEAD` and `gh pr merge --squash` only puts the PR title there. release.yml now extracts the changelog from a `## Release notes` section in the PR body (via `gh pr view`), with the PR template seeding the section. Backfilled in v1.13.2.

## OpenSpec

Spec-driven workflow lives under `openspec/` (`changes/`, `specs/`, `config.yaml`). Use the `/opsx:propose`, `/opsx:apply`, `/opsx:archive`, `/opsx:explore` skills for non-trivial changes when the user requests them.

**Always archive once a change ships** (Lachlan's rule, 2026-10-06). `openspec/changes/` holds only work that has not shipped. When the PR implementing a change has merged and released, without being asked:

1. Branch from `main` and run `openspec archive <change> -y`. It moves the change to `openspec/changes/archive/YYYY-MM-DD-<change>/` and syncs its delta specs into `openspec/specs/`.
2. Replace the `TBD - created by archiving change ...` Purpose line the CLI writes into any new spec with one real sentence (what it does, the version and PR it shipped in).
3. Settle every open task. Tick it with dated evidence, or leave it unticked with a note linking a GitHub issue that carries the remaining work. Never tick a task that wasn't done.
4. Run `openspec validate --specs --strict`, then open a docs-only PR (no version bump or release notes; see "non-shipping" above). Examples: #131, and the 2026-10-06 archive of `stream-ratings-to-letterboxd` and `jellyscribe-in-app-page`.

## Skill routing

When the user's request matches an available skill, ALWAYS invoke it using the Skill
tool as your FIRST action. Do NOT answer directly, do NOT use other tools first.
The skill has specialized workflows that produce better results than ad-hoc answers.

Key routing rules:
- Product ideas, "is this worth building", brainstorming → invoke office-hours
- Bugs, errors, "why is this broken", 500 errors → invoke investigate
- Ship, deploy, push, create PR → invoke ship
- QA, test the site, find bugs → invoke qa
- Code review, check my diff → invoke review
- Update docs after shipping → invoke document-release
- Weekly retro → invoke retro
- Design system, brand → invoke design-consultation
- Visual audit, design polish → invoke design-review
- Architecture review → invoke plan-eng-review
