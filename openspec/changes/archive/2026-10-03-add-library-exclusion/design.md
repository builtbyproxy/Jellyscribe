## Context

Export to Letterboxd and Serializd happens in three places:

- `LetterboxdSyncRunner.SyncOneUserAsync` queries every played, non-virtual
  `Movie` for the user, then narrows by date filter and plausible watch date,
  once per enabled account.
- `Serializd/SerializdSyncRunner.SyncOneAsync` does the same for `Episode`.
- `PlaybackHandler` handles a completed movie or episode and fans out across the
  user's enabled accounts for the matching service.

None of these know which library an item lives in. Account settings are plain
properties on `Account` / `SerializdAccount`, persisted by Jellyfin's XML plugin
config, edited on the admin page (full-config GET/PUT through the inline script in `configPage.html`)
and on the per-user page (the `Accounts` GET/PUT endpoints on
`LetterboxdController` and `SerializdController`).

Issue #124 asks for a way to keep one library (Anime) out of both services.

## Goals / Non-Goals

**Goals:**
- Per-account exclude list that every export path honors, films and episodes.
- Default of "exclude nothing", so upgrading changes no behavior.
- One shared rule for "is this item in an excluded library", so the scheduled
  and real-time paths cannot drift apart.

**Non-Goals:**
- Import paths (diary import, watchlist sync, Seerr requests).
- An include-only mode, a server-wide list, or per-series exclusions.
- Retracting entries already posted.

## Decisions

### Store library ids, not names

`ExcludedLibraryIds` is a `List<string>` of library ids in `N` format (32 hex,
no dashes), matching how the plugin already stores Jellyfin user ids.

- Names can be renamed in the Jellyfin dashboard; ids survive a rename.
- Alternative: store names for readability in the XML. Rejected because a
  rename would silently re-enable sync for that library.
- A deleted library leaves a dead id in the list. It matches nothing, so it is
  harmless, and it is kept on save: the pages only replace ids for libraries they
  listed, so a restricted user's partial list (or a failed fetch) can never clear
  an exclusion an admin set. An API save that omits the field keeps the stored
  list for the same reason.

### Resolve an item's libraries with `ILibraryManager.GetCollectionFolders`

A shared static helper, `LibraryExclusion.IsExcluded(ILibraryManager, BaseItem,
IReadOnlyCollection<string>)`, returns true when any collection folder returned by
`GetCollectionFolders(item)` has an id in the list. It short-circuits to false
when the list is empty, so accounts without exclusions pay no cost.

- `GetCollectionFolders(BaseItem)` is on the `ILibraryManager` interface in the
  10.11.9 SDK we compile against, so tests stub it with NSubstitute and no new
  static seam is needed.
- The collection folder ids it returns are the same ids `GetVirtualFolders()`
  reports as each library's `ItemId`, so the UI and the filter agree on ids.
- Alternative: `item.GetTopParent()`. Rejected: the top parent is the physical
  root folder under the aggregate root, not the `CollectionFolder` the user sees
  as a library, so its id never matches a virtual folder id. Mapping physical
  folders to libraries is exactly what `GetCollectionFolders` does, and it is also
  why one item can belong to two libraries (two libraries sharing a path).
- The helper is called by the runners and `PlaybackHandler`, above the
  `ILetterboxdService` seam, so the API and scraping implementations never see an
  excluded item and cannot diverge on the rule.
- The plugin had no library scoping before this change (no use of
  `GetCollectionFolders`, `TopParentId`, or `CollectionType` in sync code), so
  this is the only mechanism.
- Alternative: push the filter into `InternalItemsQuery`. The 10.11 query has
  `TopParentIds` and `AncestorIds` but no exclusion by ancestor, so we would have
  to invert the list into "all libraries except these". That breaks for items in
  libraries the list does not know about and does not help `PlaybackHandler`,
  which receives a single item. Rejected in favor of one post-query rule used
  everywhere.

### "Any" match excludes

If an item's path is shared by two libraries (e.g. an Anime folder that is also
inside a TV library) and either is excluded, the item is skipped. The user's
intent is "do not post this content"; posting it because a second library also
contains it would defeat that.

### Filter per account, after the query

Both runners already loop per account and query per user. The exclusion filter
runs next to the existing date filter, inside the per-account path, so two
accounts on the same Jellyfin user can exclude different libraries. Skipped items
are counted and logged once per run at Information, the same shape as the
existing "no plausible LastPlayedDate" skip. They are not recorded in sync
history: an excluded library is a choice, not a failure, and recording it would
flood the history view on every scheduled run.

`PlaybackHandler` checks the same helper inside its per-account loop, before any
network call, and logs one Information line per skipped account.

### Library listing endpoint

A new `Api/LibrariesController.cs` serves
`GET /Jellyfin.Plugin.LetterboxdSync/Libraries` (`[Authorize]`, deriving from
`JellyfinUserApiController` like the existing controllers, so any authenticated
user can call it). It returns `{ id, name, collectionType }` for each virtual
folder whose collection type is movies, tvshows, mixed, or unset. For a
non-admin caller it returns only libraries that user can access, so the endpoint
does not reveal library names a user cannot otherwise see.

- Own controller rather than a route on `LetterboxdController`: the list serves
  both Letterboxd and Serializd account cards, so it belongs to neither
  service's controller, and `LetterboxdController` is already the largest API
  file.
- Alternative: have the pages call Jellyfin's `/Library/VirtualFolders`
  directly. Rejected: that endpoint is admin-only, and the per-user page runs as
  a normal user.
- Alternative: use the caller's user views (`/UserViews`). Rejected: views can
  be grouped or reordered by display preferences and are not guaranteed to be the
  collection folder ids the filter compares against.

### UI

Each account card on both pages gets a collapsed "Excluded libraries" section
with one checkbox per library from the endpoint. It saves through the existing
account PUT paths (new `excludedLibraryIds` field on the request DTOs, and the
admin page's full-config round trip carries the property automatically). The
section is hidden when the endpoint returns no libraries.

## Risks / Trade-offs

- [`GetCollectionFolders` walks the parent chain per item; a large TV catalog
  means thousands of calls per scheduled run] → Skip the call entirely when the
  list is empty (the common case), cache results per parent series within a run
  for episodes, and measure with the bench harness before merge.
- [Items Jellyfin cannot place in any library (orphaned, or not yet indexed)
  would never match] → They are treated as not excluded, which is today's
  behavior; they are rare and still need a matching TMDb id to post anything.
- [Admin page full-config round trip could drop the new list if the JS rebuilds
  account objects field by field] → It does: `configPage.html`'s `saveAccount`
  rebuilds each account literal, so `ExcludedLibraryIds` is written explicitly
  there. The legacy `configPage.js`, which also rebuilt accounts field by field,
  was dead (no page loaded it since March 2026) and is removed in this change.
- [Another in-flight change (`feat/stream-ratings`) adds a new path that pushes
  ratings to Letterboxd and new account fields on the same pages and DTOs] →
  Whichever lands second rebases and re-runs the plan gate; the ratings path must
  call `LibraryExclusion.IsExcluded` too, since "do not post this content" covers
  ratings.
- [Excluding a library after watches have already been posted leaves those
  entries on Letterboxd / Serializd] → Documented as a non-goal; the UI help text
  says exclusion applies to future syncs.

## Migration Plan

No data migration. The XML serializer leaves the new list empty for existing
accounts, which means "exclude nothing". Rollback to the previous version is safe:
older builds ignore the unknown element.

## Open Questions

- Should the endpoint include `homevideos` / `musicvideos` libraries? Proposed
  answer: no, those never produce a Movie or Episode the plugin syncs.
