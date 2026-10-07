# [Jellyscribe](https://jellyscribe.dev/)

[![CI](https://github.com/builtbyproxy/Jellyscribe/actions/workflows/ci.yml/badge.svg)](https://github.com/builtbyproxy/Jellyscribe/actions/workflows/ci.yml)
[![Release](https://github.com/builtbyproxy/Jellyscribe/actions/workflows/release.yml/badge.svg)](https://github.com/builtbyproxy/Jellyscribe/actions/workflows/release.yml)
[![codecov](https://codecov.io/gh/builtbyproxy/Jellyscribe/branch/main/graph/badge.svg)](https://codecov.io/gh/builtbyproxy/Jellyscribe)
[![GitHub release](https://img.shields.io/github/v/release/builtbyproxy/Jellyscribe)](https://github.com/builtbyproxy/Jellyscribe/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Downloads](https://img.shields.io/github/downloads/builtbyproxy/Jellyscribe/total)](https://github.com/builtbyproxy/Jellyscribe/releases)
[![Fund contributors](https://img.shields.io/badge/%F0%9F%91%91_Fund_contributors-royalty.dev-BB953A?style=for-the-badge&labelColor=1a1a1a)](https://app.royalty.dev/builtbyproxy/Jellyscribe)

> **Formerly LetterboxdSync.** Renamed to Jellyscribe now that it syncs TV shows (via Serializd) alongside films (via Letterboxd), not just Letterboxd. Existing installs update in place automatically, no action needed.

- **Website:** [jellyscribe.dev](https://jellyscribe.dev/)
- **What's new:** [release notes for every version](https://jellyscribe.dev/releases/)
- **Built with AI:** most of this plugin is AI-written, human-reviewed, [full transparency in AI.md](AI.md)

Automatically sync your Jellyfin watch history to your Letterboxd diary (films) and Serializd diary (TV). Titles are logged in real-time when you finish watching, with a daily scheduled sync as a safety net.

Uses Letterboxd's current JSON API (`/api/v0/production-log-entries`) and Serializd's API.

<img alt="Jellyscribe dashboard inside the Jellyfin admin UI, showing sync stats and recent activity" src="docs/images/dashboard.png" />

## Features

### Syncing

- **Real-time sync**, titles logged to your diary the moment you finish watching
- **Daily catch-up**, scheduled task picks up anything missed
- **Multi-user, multi-account**, each Jellyfin user can link their own Letterboxd account, Serializd account, or both
- **TMDb matching**, films and episodes matched by TMDb ID, so foreign titles and special characters work
- **Duplicate detection**, won't log the same title twice on the same day
- **Rewatch detection**, real-time playback automatically marks rewatches
- **Date filtering**, limit catch-up syncs to recently watched titles
- **Library exclusion**, keep whole libraries (an Anime library you track elsewhere, say) off a Letterboxd or Serializd account

### Ratings, reviews & diary

- **Rating sync, both ways**, Jellyfin ratings (0-10) map to Letterboxd stars (0.5-5.0) or Serializd's 1-10 scale, and ratings you set on either service seed your Jellyfin user rating back
- **Ratings after the watch**, rate a film any time after you've watched it (or without watching it) and, about ten seconds after you settle on a score, it becomes your Letterboxd rating for that film. Jellyfin's own web app has no star rating (only the favourite heart), so the easy way in is [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced) 12.10+: in its settings turn on "Enable User Written Reviews" and "Also save review star ratings as the user's Jellyfin rating", and the stars on your reviews sync. Any other app that saves ratings to Jellyfin works too. Only changes are sent (ratings already in Jellyfin when you update are left alone until you change them), and a failed send is retried a few times. As on Letterboxd, rating a film marks it watched and removes it from your watchlist; clearing a rating in Jellyfin does not unrate it on Letterboxd. Some apps (Infuse, for one) never save ratings to Jellyfin, so their ratings can't be synced. Letterboxd only for now, not Serializd
- **Favorites**, sync Jellyfin favorites as Letterboxd likes or Serializd likes
- **Reviews**, write and post reviews to Letterboxd (films) or Serializd (shows or individual episodes) from the plugin dashboard
- **Diary import**, mark Jellyfin movies or episodes as played if they're already in your Letterboxd or Serializd diary

### TV shows → Serializd

Full feature parity with the Letterboxd side: real-time sync, ratings, reviews, favorites, diary import, rewatch detection, watchlist sync, and Seerr auto-request/backfill/mirror all work the same way for [Serializd](https://www.serializd.com) as they do for Letterboxd, just scoped to TV episodes instead of films.

- **TV sync**, finished TV episodes are logged to your Serializd watched list in real time, the TV counterpart to the Letterboxd film sync
- **Per-user accounts**, each Jellyfin user links their own Serializd account (by email or username), with a Verify login button and passwords encrypted at rest
- **Daily catch-up**, a "Sync watched TV to Serializd" scheduled task picks up anything real-time missed, plus **Sync TV now** on the dashboard's Overview (pick the TV filter)
- **TMDb matching**, episodes matched by their series' TMDb id + season/episode number
- **Isolated from Letterboxd**, films still sync to Letterboxd; a Serializd failure never blocks the Letterboxd path, or vice versa
- **No cookie fallback needed**, Serializd's API never needs the Cloudflare cookie workaround Letterboxd sometimes does (see [Cloudflare issues](#cloudflare-issues) below), so TV sync has nothing to babysit

### Watchlist & Seerr

- **Watchlist sync**, import your Letterboxd or Serializd watchlist as a Jellyfin playlist (Serializd also gets a Jellyfin collection for the shows themselves)
- **Seerr integration**, auto-request watchlisted films or shows missing from your library, attributed to the right user; optionally backfill requests for titles that arrived outside Seerr, and mirror your Letterboxd or Serializd watchlist one way into your Seerr watchlist

### Dashboard & diagnostics

- **Dashboard**, sync stats, activity history, and one-click sync, both on the admin plugin page and on each user's own Jellyscribe page
- **Send logs to developer**, one-click diagnostic bundle from the Logs tab, with a full preview of what's sent (email addresses masked) and a reference code to quote in a bug report
- **Cloudflare resilient**, automatic retry with backoff on rate limits and transient Letterboxd errors, raw cookie fallback

## Install

### Plugin repository (recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add the Jellyscribe repository:
   - **Name:** `Jellyscribe`
   - **URL:** `https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json`
3. Go to **Catalog** and install **Jellyscribe**
4. Restart Jellyfin
5. Hard-refresh the Jellyfin web UI (Ctrl/Cmd + Shift + R) so the Jellyscribe link appears in the sidebar (Jellyfin 10.11) or the profile menu (Jellyfin 12)

The URL above is an edge-cached mirror of the GitHub manifest that keeps an anonymous install count (see [Install counting](#install-counting-separate-from-the-opt-in-telemetry)). If you prefer not to be counted, use the GitHub manifest instead; it serves the identical catalog and updates arrive the same way:

- **URL:** `https://raw.githubusercontent.com/builtbyproxy/Jellyscribe/main/manifest.json`

The plugin files themselves are still downloaded through the mirror whichever repository you add, and each download is counted; only a [manual install](#manual-install) from GitHub Releases avoids that.

### Manual install

1. Download the latest Jellyscribe ZIP from [Releases](https://github.com/builtbyproxy/Jellyscribe/releases)
2. Extract `Jellyscribe.dll` and `HtmlAgilityPack.dll` to your Jellyfin plugins directory
3. Restart Jellyfin

## Setup

Each Jellyfin user can link their own accounts, no admin access needed:

1. Open **Jellyscribe** from the Jellyfin sidebar (Jellyfin 10.11) or the profile (avatar) menu (Jellyfin 12). It opens as a page inside Jellyfin, and you can bookmark it at `#/jellyscribe`
2. Go to **My accounts** and click **+ Link a diary**
3. Pick the **Service**: Letterboxd (film) or Serializd (TV)
4. Enter your Letterboxd **username** (the name in `letterboxd.com/<username>/`, not your email: Letterboxd no longer accepts email sign-in), or your Serializd email or username, and your password
5. Click **Verify login** to check it works; for Letterboxd it says whether the official API or the website login was used, or why both failed
6. Leave **Enabled** ticked, choose any other options (see below), and click **Save**

That's it. Watch a movie and check your Letterboxd diary.

Admins can do the same for any user from **Dashboard > Plugins > Jellyscribe**: open **Accounts**, find the Jellyfin user, and click **+ Link a diary** under their name (the dialog then also asks which **Jellyfin user** the diary belongs to). A Jellyfin user can link a Letterboxd account, a Serializd account, or both, and several of each.

### Settings per account

These apply the same way whether the account is a Letterboxd (film) or Serializd (TV) account, just scoped to the matching media type.

| Setting | Description |
|---|---|
| **Enabled** | Master switch for this account; nothing syncs while unticked, saved settings are kept |
| **Mark favourites as liked** | Marks the title as "liked" on Letterboxd or Serializd if favorited in Jellyfin |
| **Sync ratings to Letterboxd** | Letterboxd accounts only, on by default. Sends a film's rating to Letterboxd whenever you change it in Jellyfin, not just when the watch is logged |
| **Only sync recently played** | Limits daily catch-up to titles played in the last N days (**Days to look back**) |
| **Primary account** | When one Jellyfin user links multiple accounts on the same service, the primary wins on rating-import conflicts, is preselected in the review modal, and is the only one that mirrors into Seerr |
| **Sync watchlist to library** | Mirrors your Letterboxd or Serializd watchlist into a Jellyfin playlist daily (Serializd also gets a collection); each account gets its own playlist, named by **Watchlist name** if you set one |
| **Auto-request via Seerr** | Watchlisted films or shows missing from your library are requested in Seerr, attributed to this user's Seerr account (an admin sets the Seerr URL and API key under **Integrations** on the plugin dashboard) |
| **Backfill Seerr requests** | Extends auto-request to titles already in the library that have no request record, so titles that arrived outside Seerr still show a requester; never triggers re-downloads |
| **Mirror watchlist to Seerr** | One-way copy of your Letterboxd or Serializd watchlist into your Seerr user's own watchlist (movies for Letterboxd accounts, TV for Serializd accounts). Your Letterboxd or Serializd watchlist is the source of truth: titles you add only in Seerr are removed from your Seerr watchlist on the next run, and nothing is copied back. Primary account only; an empty watchlist is never mirrored |
| **Skip already-synced** | Uses the plugin's local sync history to skip titles already logged without hitting Letterboxd/Serializd; recommended, especially on large libraries |
| **Stop on first failure** | Halts the run at the first failure to avoid inflaming rate limits; the rest are picked up next run |
| **Import diary as watched** | Marks Jellyfin movies or episodes as played if they appear in your Letterboxd or Serializd diary |
| **Excluded libraries** | Jellyfin libraries whose films or episodes are never logged to this account's diary (by the scheduled sync or the real-time one) and whose ratings are never sent. Applies to future syncs only; anything already logged stays on Letterboxd or Serializd. It governs exports only: diary import, watchlist sync, and Seerr requests still look at every library |
| **Raw cookies** / **User-Agent** | For Cloudflare bypass, Letterboxd accounts only, see below |

### Dashboard

Admins get the plugin dashboard at **Dashboard > Plugins > Jellyscribe**, with these sections:

- **Overview**: sync statistics for films (Letterboxd) and episodes (Serializd), watchlist counts, and recent activity with links to each title, and **Load older history** to page back through everything Jellyscribe has logged. **Sync all now** runs a sync on demand and **Sync all watchlists** refreshes the watchlists; pick the Film or TV filter to run just one side. **Review** buttons on recent watches let you write and post a review to Letterboxd or Serializd
- **Accounts**: every linked diary, grouped by Jellyfin user, with **+ Link a diary** and **Edit** for each account. An account whose Letterboxd login keeps failing shows **Login failing · sync paused** until its credentials are re-saved
- **Activity**: the full sync log for films or TV, page by page, with a title search. Consecutive episodes of one show fold into a single row you can expand
- **Integrations**: server-wide settings, the Seerr URL, API key, and **Test connection**, plus anonymous telemetry
- **Logs**: recent Jellyscribe log lines, and **Send to developer** (see below)

Every user also gets their own Jellyscribe page (see [Setup](#setup)) with **Overview** (their own stats, activity, **Sync all now**, and **Review** buttons) and **My accounts**.

### Cloudflare issues

If login fails with a 403 error:

1. Log into Letterboxd in your browser
2. Open DevTools (F12) > Network tab
3. Reload and click any request to `letterboxd.com`
4. Copy the **Cookie** header value (everything after `Cookie: `, not the label itself)
5. Paste it into the **Raw Cookies** field
6. Copy the **User-Agent** request header value from the same request and paste it into the **User-Agent** field

**Important:** Cloudflare ties `cf_clearance` to the exact User-Agent that solved the challenge. If you copied cookies from Chrome but leave the User-Agent field blank, the plugin sends the default Firefox UA and Cloudflare will reject the cookie. Always paste the User-Agent from the same browser you copied the cookies from. Leave it blank only if you copied cookies from Firefox 134 on Windows.

#### Still 403ing after pasting Raw Cookies and a matching User-Agent

When a correctly-copied cookie still gets blocked, it's usually one of these:

1. **Different IP.** Cloudflare pins `cf_clearance` to the IP address that solved the challenge, not just the User-Agent. If the box running Jellyfin reaches the internet via a different public IP than the browser did (different machine, VPN, mobile tether, a server in a datacenter), Cloudflare sees the token arrive from a new IP and rejects it. Fix: paste fresh cookies from a browser running on the **same network as the Jellyfin server**, and watch out for VPNs or split tunnels.

2. **It expired.** `cf_clearance` from a managed challenge is short-lived, often around 30 minutes. If there's a gap between copying the cookies and the sync actually running, the token can already be dead. Fix: paste fresh cookies and immediately trigger a sync from the plugin dashboard rather than waiting for the scheduled run.

3. **Connection fingerprint.** Cloudflare doesn't only check the cookie and UA, it also fingerprints the TLS handshake and HTTP/2 behaviour of the connection. A plugin's HTTP client doesn't look like a real browser at that layer, so on a site running bot-fight mode the right cookie isn't always enough on its own. There isn't much the plugin can do about this one.

If you've ruled all three out and a single film keeps getting stuck on the TMDb lookup, open an issue. A workaround that skips the Cloudflare-protected lookup for that one film (pointing a TMDb ID directly at a Letterboxd slug) is being considered.

## Telemetry

The plugin can send **anonymous, opt-in** usage telemetry. It is **off by default**: nothing is ever sent unless you turn it on, from the one-time notice on the admin Overview or the checkbox under **Integrations → Anonymous telemetry**. Answering the notice either way (Enable or No thanks) hides it for good.

When enabled, one small ping is sent per week. When a kind of sync error that was not happening starts, one extra `error_transition` ping goes out straight away, capped at one a day (a second new error that day is held and sent once the day is up), so fleet-wide breakage gets caught early. Both kinds carry the same fields. The full payload is exactly this, and you can see your own at any time with **Integrations → Anonymous telemetry → Preview**:

```json
{
  "schema_version": 1,
  "instance_id": "8a6f4f6e-1f2b-4c43-9a57-2f0e6f3b9d1c",
  "ping_type": "weekly",
  "plugin_version": "2.1.0.0",
  "jellyfin_version": "10.11.11",
  "features": { "watchlist_sync": true, "diary_import": false, "tv_watchlist_sync": false, "tv_diary_import": false,
                 "...": "booleans of which Letterboxd and Serializd settings are enabled" },
  "buckets": { "accounts": "1", "library": "2k-10k", "syncs_per_week": "1-10", "syncs_ever": "11-100",
               "tv_syncs_per_week": "0", "tv_syncs_ever": "0" },
  "errors": { "cloudflare_403": 0, "auth_failure": 2, "tmdb_lookup": 0, "jellyseerr_error": 0, "rate_limit": 0,
              "server_error": 0, "write_failure": 0, "parse_error": 0, "other": 0,
              "state": { "cloudflare_403": false, "auth_failure": true, "...": "which error types are currently occurring" } }
}
```

The precise promise, worded carefully:

- **No IPs, usernames, emails, film titles or library content ever enter the dataset.** Usage counts (accounts, library size, syncs) are reported in buckets only. Error counts are the exact number of each kind of sync error since the last weekly ping, because the release canary compares error rates across versions; they say how often something failed, never what or for whom. (Transport logs at the hosting platform retain caller IPs for the platform's own short retention window, like any HTTPS service; they are never stored in the telemetry dataset.)
- The instance ID is **random**, generated when you opt in, never derived from your hardware, network, or Jellyfin install. It is kept if you turn telemetry off and on again. **Regenerate it any time** with **Integrations → Anonymous telemetry → Regenerate ID**: future pings get a fresh identity. Old rows remain (unlinked going forward); at small fleet sizes configuration similarity could in principle still allow correlation, so the honest claim is "unlinked", not "erased".
- The Preview window doubles as a **diagnostic bundle** for bug reports: **Copy** puts the exact JSON on your clipboard. It contains your instance ID, and pasting it into a public issue links that ID to your past pings, which is why the window also offers **Copy + regenerate ID**.
- Disabling telemetry stops all pings immediately.

What it's for: deciding what gets built next based on what people actually use, and an automated canary that compares error rates across releases and files regression issues before bug reports arrive.

### Install counting (separate from the opt-in telemetry)

The recommended plugin repository URL and the release downloads are served through an edge-cached mirror of the GitHub manifest. The mirror counts each request as a salted, weekly-rotating hash of the caller's IP so the project can estimate how many servers run the plugin. This is a plain traffic count, not the telemetry above: no instance ID, no settings, no versions beyond the release being downloaded. The raw IP is used only to compute the hash and is never written to the database, and the mirror keeps no request logs of its own. The hash mixes in the week and a secret salt, so the stored rows cannot be tied to an IP or linked across weeks by anyone who only sees the data. The salt is fixed, though, so whoever holds it (the maintainer) could hash a known IP and find that IP's rows; treat this as pseudonymous, not anonymous. As with any HTTPS service, the hosting platform itself still sees caller IPs in transit.

To avoid the manifest count, use the GitHub manifest URL (`https://raw.githubusercontent.com/builtbyproxy/Jellyscribe/main/manifest.json`) as your plugin repository instead of the mirror; it serves the identical catalog and is never counted. Plugin updates are still downloaded through the mirror's download link whichever repository you use (that is what the release-download count above measures); a [manual install](#manual-install) from GitHub Releases avoids that too. To switch, check **Dashboard > Plugins > Repositories**:

- **Only one Jellyscribe entry, pointing at `lbsync-telemetry.lachlanbyoung.workers.dev`** (you followed the install steps above): add the GitHub manifest URL first, then delete the mirror entry. Do not just delete the mirror entry, or you will have no Jellyscribe repository left and stop getting updates.
- **A GitHub entry plus one named "... (mirror)"**: older installs that used the GitHub manifest had the mirror added alongside it once, by v1.19.0 or later. Your GitHub entry was never removed, so you can simply delete the mirror entry; the plugin will not add it again.

### Send logs to the developer

When something goes wrong, the **Logs** tab has a **Send to developer** button. It uploads a diagnostic bundle privately and gives you a short **reference code** (e.g. `LBX-7Q2F9K`) to quote if you open a bug report. The bundle holds:

- up to the last 500 Jellyscribe log lines from the server's two newest log files, the same kind of lines the Logs tab shows. Email addresses are replaced with `[email]` (Serializd accounts appear as a short tag such as `serializd-3fa2b1`); passwords, cookies, auth tokens and review text are never logged, and the review replies older versions logged are cut from these lines;
- the plugin and Jellyfin versions, and which log files were read;
- the telemetry snapshot that Preview shows, and your telemetry instance ID (if you have none, a one-off ID that stays the same until the server restarts);
- your note, if you write one.

Unlike the anonymous telemetry above, **logs are not anonymous**: the lines name films, shows, Jellyfin users and Letterboxd usernames, and can quote error messages from Letterboxd, Serializd and Seerr, and the bundle is linked to your telemetry instance ID. So it is strictly opt-in per use: a confirmation step lists all of this, lets you add a note, and its **Preview** button shows the exact bundle, note included, before anything leaves your server. Works whether or not telemetry is enabled. Uploaded bundles are stored privately and auto-deleted after 90 days.

## Requirements

- **Jellyfin 10.11.9 or newer, including Jellyfin 12.x.** One release serves both, with nothing to change in your config and no separate Jellyfin 12 download. Verified by loading the shipped build on a clean Jellyfin 12.0.0 server, and every change is built and tested against the Jellyfin 12 SDK in CI.
  - **Migrating your server to Jellyfin 12?** Jellyfin advises removing (or disabling) external plugins before the upgrade, and that is safe to follow here: your accounts, settings, and sync history live outside the plugin folder and all survive a reinstall from the catalog.
  - Note that Jellyfin 12 moved where plugins live, from `config/data/plugins/` to `config/plugins/`. Jellyfin handles that move for you on upgrade. It only matters if you install the plugin by hand rather than from the catalog, in which case use the new path on 12.x.
- A Letterboxd and/or Serializd account
- Jellyscribe opens as its own page inside Jellyfin (no reload, like Jellyfin Enhanced's Bookmarks), from the sidebar on Jellyfin 10.11 or the profile (avatar) menu on Jellyfin 12, and you can bookmark it at `#/jellyscribe`. This needs no other plugin; if you already run the [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation), Jellyscribe uses it too, and you still get one link
- Optional: a [Seerr](https://github.com/seerr-team/seerr) instance for the auto-request and watchlist-mirror integrations

## Building from source

```bash
git clone https://github.com/builtbyproxy/Jellyscribe.git
cd Jellyscribe
dotnet build -c Release
```

Output `Jellyscribe.dll` is in `LetterboxdSync/bin/Release/net9.0/`.

## Contributing

PRs welcome. A few conventions:

- **PR body shape** lives in [`.github/pull_request_template.md`](.github/pull_request_template.md). Symptom first, plain English, six fixed sections: Release notes, What's broken, Why it happens, What this PR does, How to test, and Follow-ups (not in this PR). **Release notes** is the user-facing paragraph that `release.yml` publishes as the release changelog, so fill it in for any change that ships; a PR that only touches non-shipping files (docs, site, tests, CI) can leave it out.
- **Non-trivial changes** are planned through [`openspec/`](openspec/) before implementation: proposal, design, specs, then tasks. See [`openspec/changes/`](openspec/changes/) for active proposals and the [`archive/`](openspec/changes/archive/) folder for past ones.

## License

[MIT](LICENSE)
