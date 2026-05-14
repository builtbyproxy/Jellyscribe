# Integration tests

These talk to a real Letterboxd account over the network and are **skipped by
default**. They exist so a tagged-by-hand `dotnet test` run can verify the
plugin's auth, lookup, and read paths still work end-to-end against
production Letterboxd HTML/API surfaces that the unit suite mocks out.

## Why they're skipped by default

- Network-dependent and slow (Cloudflare warm-up, real HTTP round-trips).
- Require a Letterboxd account credential, which can't live in the public repo.
- Sometimes throttled by Cloudflare; we don't want CI to redden over that.

The unit suite (currently ~470 tests) gives the routine feedback loop; these
are for pre-release sanity checks and reproducing live bugs.

## Setup

1. Create or pick a Letterboxd account dedicated to testing (do not use a real
   personal account — these tests are read-only today, but the credential will
   end up in shell history / env state on whatever machine you run them on).
2. Export the credentials in the shell that will run `dotnet test`:

   ```bash
   export LETTERBOXD_TEST_USERNAME="your-test-account"
   export LETTERBOXD_TEST_PASSWORD="your-test-password"

   # Optional — supply if Cloudflare 403s on raw login
   export LETTERBOXD_TEST_RAW_COOKIES="cf_clearance=...; letterboxd.session=..."
   export LETTERBOXD_TEST_USER_AGENT="Mozilla/5.0 ..."
   ```

3. Run the integration suite:

   ```bash
   dotnet test -c Release --filter Category=Integration
   ```

   Run everything except integration tests:

   ```bash
   dotnet test -c Release --filter "Category!=Integration"
   ```

## Security posture

- Credentials are read from environment variables only. There is **no** code
  path in this repo that loads them from a file checked into git.
- `.env`, `.env.local`, and `*.env.local` are gitignored at the repo root in
  case you want to source a local file (e.g. `set -a && source .env && set +a`).
- Do not paste credentials into commit messages, PR descriptions, issues, or
  any markdown that lands in the repo or its history.

## Why no write tests yet

`ILetterboxdService` has no `DeleteDiaryEntry` API, so a test that posts a
diary entry leaves residue on the test account between runs. Write coverage is
deferred until either (a) we add a delete path, or (b) we accept periodic
manual cleanup on the test account. Until then, the live tests are read-only
(`Authenticate`, `LookupFilmByTmdbId`, `GetWatchlistTmdbIds`,
`GetDiaryFilmEntries`, `GetDiaryInfo`).

## CI

These tests do **not** run in GitHub Actions today. Wiring them up requires
adding the credentials as repository secrets and adjusting `release.yml` /
the CI workflow. Treat that as a separate change.
