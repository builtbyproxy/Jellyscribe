# Proposal: jellyscribe-in-app-page

## Why

Opening Jellyscribe from the sidebar does a full page load to `/web/configurationpage?name=letterboxduser`, a plugin settings page: the app reloads, the URL reads like a settings screen, and the page feels bolted on. Jellyfin Enhanced's Bookmarks shows the alternative: its own route (`#/bookmarks`) rendered inside Jellyfin's app, with no reload, keeping the header, the menu and the back button. Lachlan asked for Jellyscribe to work the same way. A second gap: on Jellyfin 12 the web client has no sidebar drawer, so the link added in 2.9.0 is not visible there at all (no issue filed; noted as a known limitation in PR #137).

## What Changes

- **In-app page**: the client script that already adds the sidebar link also registers a `#/jellyscribe` route. Navigating there shows a Jellyscribe page inside Jellyfin's page container, rendered from the existing user dashboard (diaries, accounts, activity, review modal), without reloading the app. Leaving it (menu, header, back button, any Jellyfin navigation) restores Jellyfin's own page.
- **Sidebar link** opens the in-app page instead of reloading to the configuration page.
- **Jellyfin 12**: a Jellyscribe entry in the avatar menu (`#app-user-menu`), opening the same in-app page.
- **Fallback**: the configuration page (`configurationpage?name=letterboxduser`) keeps working unchanged, for bookmarks, old links, and as the fallback if the in-app page cannot mount.

## Capabilities

### New Capabilities

- `in-app-page`: The Jellyscribe page inside Jellyfin's web client: the `#/jellyscribe` route, showing and hiding it within Jellyfin's page system, rendering the user dashboard into it, the entry points that open it (10.11 sidebar link, Jellyfin 12 avatar menu), and the configuration-page fallback.

### Modified Capabilities

<!-- none: no existing spec covers the sidebar link or the user dashboard -->

## Impact

- `LetterboxdSync/Web/sidebar.js`: grows from a link injector into the client entry script (route, page container, show/hide, entry points). Served by the existing anonymous `SidebarController` and injected by the 2.9.0 middleware or File Transformation; it stays static content.
- `LetterboxdSync/Web/userPage.html`: must render correctly when mounted into the in-app page as well as when loaded as a configuration page (it already scopes styles to `#letterboxdUserPage` and boots itself).
- No server-side API changes; the page's data calls are the dashboard's existing endpoints.
- Tests: client-script behaviour has no JS test harness in the repo; verification is the headless-Chrome probe pattern plus live checks on Jellyfin 10.11 (with and without a base URL) and 12.
- Ships a release: version 2.10.0, `## Release notes`, `release-notes.ts` entry.

## Non-goals

- Redesigning the dashboard itself; the in-app page shows the existing user dashboard as is.
- The admin configuration page (Dashboard > Plugins > Jellyscribe); it stays a settings page.
- Integrations with the Plugin Pages or Custom Tabs plugins (Jellyfin Enhanced supports them; not needed here).
- A home-screen tab or a library-style "view" with posters; this is one page.
