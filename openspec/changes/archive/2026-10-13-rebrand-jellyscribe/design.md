## Naming

### Decision: Jellyscribe

The path here matters more than usual because three whole *directions* were
tried and closed off in turn, not just individual candidates.

### Round 1: the `-arr` direction (rejected)

Both destinations already use the word "diary" for the thing this plugin
writes to (Letterboxd's diary, Serializd's diary), and the self-hosted
Jellyfin audience this plugin lives among already reads an `-arr` suffix as
"does one job, quietly, forever" (Sonarr, Radarr, Bazarr, Prowlarr). Top
candidate was **Diaryarr**, checked against GitHub, npm, the Servarr wiki,
and `awesome-arr`: unclaimed, and distinct from the two closest adjacent
projects (`sbondCo/Watcharr`, a standalone watched-list app with its own
database; `sirrobot01/scroblarr`, a media-server scrobble relay, single b).
**Scribeloop** was the researched fallback if `-arr` read as over-saturated.

Rejected anyway, on a sharper point than collision: this plugin isn't part of
the Sonarr/Radarr/Prowlarr *interoperating* family, it doesn't acquire,
organize, or manage media, it has nothing to do with indexers or download
clients. Using their naming convention borrows that ecosystem's credibility
for a tool that doesn't actually belong to it, which is the same "riding on
someone else's brand" problem this whole rebrand exists to fix, just aimed at
a community instead of a company. Also considered and rejected in this round:
**Scrobblarr** (double b, one typo from the real `scroblarr` project, same
space, not worth the confusion), **Logarr**/**Trackarr** (both already real,
unrelated self-hosted tools), **Ledgerr** (search results are almost entirely
crypto-phishing sites impersonating Ledger.com, unrankable by pure
association), **"Diaries for Jellyfin"**/**"Jellyfin Watch Diary"** (original
candidates from `add-serializd-tv-sync/design.md` §8, too literal, and lean
on the Jellyfin name directly), **Bingesync** (another original candidate,
doesn't fit the diary framing or carry into any convention).

### Round 2: leaning on the Jellyfin name directly (rejected)

**JellyfinSync** was proposed next: maximally clear, and structurally
continuous with the current name (`LetterboxdSync` → `JellyfinSync`, same
shape). Rejected outright: Jellyfin's own branding guidelines explicitly
discourage third parties using "Jellyfin" directly in a project name.
**JellySync** (the shortened form) was checked too and separately rejected on
a plain collision: `SamVellaUK/JellySync` is a real, existing plugin that
does multi-server watch-status sync, a different job entirely, and "Jellyfin
+ Sync" is already a crowded space with that meaning (`jellyfin-server-sync`,
`jellyfin-plugin-serversync`, several others).

### Round 3: `Jelly-` prefix, reconsidered (chosen)

Jellyfin's guideline also discourages the `Jelly[word]`/`[word]fin` pattern
generally, which ruled out **Jellydiary** and **Jellyscribe** on first pass
too. Revisited: the guideline is phrased as a request ("strongly requests"),
not an enforcement mechanism, and there's real precedent of it being
tolerated for successful, actively-maintained plugins (Jellyscrub, Jellystat),
the concern it's actually guarding against is client apps competing for
app-store shelf space under a name that reads as officially blessed, which
doesn't apply to a server plugin installed via a repository URL, never an app
store. With that reopened, **Jellydiary** (plainest, ties to the exact word
both destinations use) and **Jellyscribe** (personified, a scribe that
writes for you, and lines up with the tagline already in the pitch, "a
quiet scribe for everything you watch") were the finalists. **Jellyscribe**
chosen for the extra bit of character; both were unclaimed. A plain,
unprefixed fallback, **Watchdiary**, was also researched and confirmed
unclaimed in case the `Jelly-` call gets revisited later.

## Visual identity

### Palette (superseded, see below)

The original pitch below (a warm "brass on ink" ledger palette) was the first
round shown to the maintainer. Feedback: too brown, didn't read as distinct
enough from a generic warm-dashboard look. Superseded by **Midnight + Gold**,
shipped in `site/src/layouts/Layout.astro`'s `:root`:

| Token | Hex | Use |
|---|---|---|
| `--bg` | `#14161C` | page ground |
| `--bg-elev` | `#1C1F27` | elevated surface (cards, panels) |
| `--bg-elev-2` | `#242833` | further-elevated surface, icon chips |
| `--border` | `#343A47` | borders, rules |
| `--text` | `#EDEFF5` | primary text |
| `--text-dim` | `#9AA1B5` | secondary text |
| `--gold` / `--gold-bright` | `#E8B84B` / `#F5CC6E` | the one accent: links, CTAs, the mark |
| `--success` | `#5FC9A8` | synced/success state only, never decorative |
| `--warn` | `#E0714F` | warning/error state only |
| `--ink` / `--ink-dim` | `#90A6EE` / `rgba(144,166,238,0.4)` | handwritten "scribe" marginalia only, never the main accent |

Same restraint principle as the original pitch (one accent everywhere, a
strictly-semantic second color), just cooler and less brown. `--ink` is a
deliberate *third* color, reserved exclusively for the hand-drawn/handwritten
flourishes leaning into the "scribe" name (margin notes, a
stamped badge), it never appears in body copy or structural UI, so it reads
as an annotation layer, not a second brand color competing with gold.

Still deliberately single-theme (dark only), same reasoning as the original
pitch: real-time sync fires at any hour, "written while you weren't looking"
is the actual mechanism, not just a mood. A light mode, if ever added, is not
scoped in this change.

### Type

- **Display: Fraunces**, weight 600 and italic 500. A serif with real
  optical weight and a hand-set, printed-record character, not a Times New
  Roman default. Headlines and the wordmark only.
- **Body: Public Sans**, weight 400 through 700. Institutional, precise,
  built for dense reading (US Web Design System heritage), fits a tool whose
  whole job is getting small facts right, repeatedly, without drama.
- **Mono: JetBrains Mono**, weight 400 through 600. Unchanged from the
  existing admin UI (`876b279`), kept for continuity and because it's already
  the right tool for version numbers, timestamps, and TMDb ids.
- **Script: Caveat**, weight 700. Added for the "scribe" flourish layer only
  (margin notes, a stamped badge, a hand-drawn arrow), never used for body
  copy or anything load-bearing, same restraint as the `--ink` color it's
  paired with.

### Mark

A bookmark ribbon, not a clever logo: a rectangle with a V-notch cut from the
bottom, gold on `--bg-elev-2`. Reads instantly at 16px (the only size most
people will ever actually see it at: a browser tab, a Jellyfin plugin
catalog row) and says exactly one true thing: your place is being kept.
Distinct in shape and color from every mark it replaces (Letterboxd's three
circles, Jellyfin's own mark) and from the closest adjacent projects'
branding. Shipped as `site/public/favicon.svg` and inline in the site header
mark and footer mark.

### Rendered pitch

Three iterative artifact passes (brand pitch → 4-way color comparison →
content/alignment redesign → scribe-flourish pass) walked through with the
maintainer live; the shipped site (`site/src/pages/index.astro` and
components) is the result, not a separate mockup to regenerate against.

## What does NOT change

- Plugin GUID (`c7a3e1b9-5d42-4f8a-9c06-2b7d8e4f1a35`), Jellyfin keys plugins
  by GUID; this is the entire reason existing installs survive the rename.
- The manifest URL end users already have configured in their Jellyfin
  repository list. It is a Cloudflare Worker URL
  (`lbsync-telemetry.lachlanbyoung.workers.dev`), already decoupled from the
  plugin's display name, and must stay stable regardless of branding.
- C# namespace (`LetterboxdSync`), see proposal.md's Non-goals.
- Sync behavior, history format, stored configuration shape, encrypted
  secrets format.

## Open questions to close before execution (not blocking this plan)

1. **Repo rename redirect.** `curl -I` the current
   `raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json`
   URL against a *test* rename (or GitHub's documented redirect behavior) to
   confirm raw content 301s survive a repo rename before doing it for real.
   If they don't hold, keep the repo name and rename display + domain only,
   per `add-serializd-tv-sync/design.md` §7. **Still open**, the maintainer
   is doing the actual GitHub repo rename themselves, separately; code/docs
   in this change deliberately leave every `github.com/builtbyproxy/...` URL
   and the `letterboxdsync.dev` domain/CNAME untouched so nothing 404s before
   that happens.
2. **Domain registration.** `jellyscribe.dev` availability was not checked
   against a live registrar in this planning pass (only checked for
   collisions with existing projects/products, which is a different
   question from "is the domain registered"). **Still open**, DNS/CNAME
   migration (design.md's infra section) is not part of this change; the
   site still deploys to `letterboxdsync.dev`.
3. **AssemblyName exact value.** ~~`Jellyscribe` vs `JellyscribeSync` vs
   keeping a `LetterboxdSync`-shaped suffix~~ **Decided: `Jellyscribe`.** Set
   explicitly in `LetterboxdSync/LetterboxdSync.csproj`; the compiled output
   is `Jellyscribe.dll`. C# `RootNamespace` and every `.cs` namespace stay
   `LetterboxdSync` (see "What does NOT change" above), only the assembly
   name changed. `release.yml`'s package step and `deploy.sh` were updated to
   zip/copy `Jellyscribe.dll` instead of `LetterboxdSync.dll` in lockstep.
