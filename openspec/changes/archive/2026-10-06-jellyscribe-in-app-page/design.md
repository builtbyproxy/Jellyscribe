# Design: jellyscribe-in-app-page

## Context

Since 2.9.0 `sidebar.js` is injected into every web client page (by `SidebarScriptStartupFilter`, or by File Transformation when installed) and adds a "Jellyscribe" entry before `.btnSettings` in the sidebar drawer. Clicking it does `window.location.assign(base + "/web/configurationpage?name=letterboxduser")`: a full reload into Jellyfin's plugin configuration page, which renders `userPage.html` (the user dashboard: diaries, accounts, activity, review modal).

`userPage.html` is self-contained: one root, `#letterboxdUserPage`, styles scoped to it, and one inline script that defines `window.WSU`, resolves auth from `ApiClient` (falling back to stored credentials), and calls `WSU.init()` immediately. Its only document-level side effects are a `keydown` listener (Escape closes modals) and the viewport meta tweak, both idempotent in effect. It looks elements up with `document.getElementById`, so two copies in one document would collide.

Jellyfin Enhanced's Bookmarks (`js/enhanced/bookmarks/bookmarks-library-page.js`, read 2026-10-04) shows the in-app pattern: a `div.page.type-interior.mainAnimatedPage.hide` appended to `.mainAnimatedPages`, `history.pushState` to `#/bookmarks`, hiding the active Jellyfin page and dispatching `viewhide`/`viewshow`/`pageshow`, and hiding itself again from a location watcher (Jellyfin's router uses `pushState`, which fires no event), from other pages' `viewshow`, and from header or menu clicks. On Jellyfin 12's layout it adds its entry to the avatar menu by cloning `#app-user-menu a[href="#/mypreferencesmenu"]`.

## Goals / Non-Goals

**Goals:**
- `#/jellyscribe` shows the user dashboard inside Jellyfin's app with no reload, on Jellyfin 10.11 (with or without a base URL) and 12.
- Leaving it by any route (menu, header, back button, Jellyfin navigation) restores Jellyfin's page cleanly.
- A visible entry point on both 10.11 (sidebar) and 12 (avatar menu).
- Never worse than today: if the in-app page cannot mount, the user lands on the configuration page as before.

**Non-Goals:**
- Changing the dashboard's content or design; Plugin Pages / Custom Tabs integration; the admin configuration page.

## Decisions

**1. One client script, `sidebar.js`, owns the route, the page and the entry points.**
It is already injected everywhere and served as a static anonymous file, so no new injection path or endpoint is needed. It stays static content (the 2.9.0 byte-identical test keeps holding).

**2. The page reuses `userPage.html` as is, fetched from Jellyfin's own configuration-page endpoint.**
On show, the script fetches `web/ConfigurationPage?name=letterboxduser` through `ApiClient` (auth headers and base URL handled), parses it with `DOMParser`, appends the `#letterboxdUserPage` element into the in-app page container, and runs its inline script by inserting a new `<script>` element with the same text (scripts inserted through `innerHTML` do not execute). One source of truth for the dashboard, no duplicate HTML to keep in sync, and the configuration page keeps working unchanged.

**3. Mount on show, unmount on hide.**
The dashboard element is removed when the page hides and fetched again on the next show. Reasons: there is never a lingering hidden copy to collide with a configuration-page instance; each visit shows fresh data (the same as today's full load); and the dashboard needs no new "refresh" API. The cost is one small HTML fetch per visit. The repeated `keydown` listener is harmless (closing already-closed modals) but is guarded anyway: the in-app script registers its own Escape handler once and the re-run is tolerated.

**4. Collision fallback.**
If a `#letterboxdUserPage` already exists in the document outside the in-app container (a configuration-page view Jellyfin is caching), the in-app page does not mount and the script navigates to the configuration page instead. Two copies would make `getElementById` resolve to the wrong one, so the safe outcome is today's behaviour. The same fallback covers a failed fetch or a missing `.mainAnimatedPages`.

**5. Page lifecycle follows Jellyfin Enhanced's proven shape.**
- Container: `div#jellyscribe-app-page.page.type-interior.mainAnimatedPage.hide` with `data-url="#/jellyscribe"`, `data-title="Jellyscribe"`, appended to `.mainAnimatedPages` (created once, kept).
- Show: `pushState` to `#/jellyscribe` (preserving the path, so a base URL is kept), hide the active `.mainAnimatedPage` with `viewhide`, unhide ours with `viewshow`/`pageshow`, mount, set the document title.
- Hide: on a location change away from `#/jellyscribe` (a 300 ms signature watcher, running only while shown), on another page's `viewshow`, or on a header/menu click that is not our own entry; restore the previous page with `viewshow` if Jellyfin has not shown one itself; unmount.
- Deep link and refresh: on script load, and on `hashchange`/`popstate`, a `#/jellyscribe` location shows the page. Jellyfin's router does not know the route, so the script waits for `.mainAnimatedPages` and then shows ours over whatever the router rendered (verified live per task 4.x).

**6. Entry points.**
- 10.11 (and 12's legacy drawer): the existing `#lb-nav-link` now calls show instead of a full reload.
- 12's layout: clone `#app-user-menu a[href="#/mypreferencesmenu"]` into a "Jellyscribe" item (so it inherits MUI's classes), close the popover via its backdrop, then show. A body observer re-adds it when the menu re-renders; it is a no-op when the menu is absent.

## Risks / Trade-offs

- [Jellyfin's page system is private API] → Same surface Jellyfin Enhanced relies on in production on 10.11 and 12; every failure path falls back to the configuration page (decision 4). Live checks on both versions are tasks, not assumptions.
- [Router fights the unknown route on deep link] → Show is re-asserted after `.mainAnimatedPages` exists; if Jellyfin navigates away the watcher hides ours, which is correct.
- [Running a fetched inline script] → It is our own embedded `userPage.html` served by Jellyfin from the plugin assembly, the same script Jellyfin itself runs for the configuration page; no new trust boundary. The fetch is same-origin through `ApiClient`.
- [A second dashboard instance via the configuration page] → Decision 4 falls back rather than mounting a colliding copy.
- [No JS test harness] → Behaviour is verified with the headless-Chrome probe pattern used for the account editor, plus live browser checks on throwaway Jellyfin 10.11 (with `/jellyfin` base URL) and 12 containers.

## Migration Plan

None: client-side only. Old links to the configuration page keep working. Rollback is the previous `sidebar.js`.

## Open Questions

- Answered live (2026-10-04): on both 10.11 and 12 the router renders its "Page not found" view for `#/jellyscribe`. Capture-phase `hashchange`/`popstate` listeners stop the event before the router for in-app navigation, and on a cold load a `viewshow` that arrives while the location is still `#/jellyscribe` is treated as that not-found view and hidden. Jellyfin 10.11 also needed the native tab-less page classes (`libraryPage noSecondaryNavPage`) and the header tabs hidden, or the dashboard sat under the header.
