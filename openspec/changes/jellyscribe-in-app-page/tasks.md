# Tasks: jellyscribe-in-app-page

## 1. Page lifecycle in sidebar.js

- [x] 1.1 Page container `#jellyscribe-app-page` (`.page.type-interior.mainAnimatedPage.hide`, `data-url="#/jellyscribe"`, `data-title`), created once under `.mainAnimatedPages`
- [x] 1.2 Show: `pushState` to `#/jellyscribe` keeping the path, hide the active page with `viewhide`, unhide ours with `viewshow`/`pageshow`, set the title; hide: restore the previous page if Jellyfin has not, start/stop a 300 ms location watcher while shown
- [x] 1.3 Hide triggers: location leaves `#/jellyscribe`, another page's `viewshow` (header and menu clicks need no handler of their own: they change the location or show a page, verified live); deep link (`#/jellyscribe` on load, `hashchange`, `popstate`)
- [x] 1.4 Headless-Chrome probe with a stub page container: show, hide on location change, hide on another page's `viewshow`, deep link (live Playwright run instead of a stub page: show, hide on location change and on another page's viewshow, deep link; see 4.x)

## 2. Mounting the dashboard

- [x] 2.1 Fetch `web/ConfigurationPage?name=letterboxduser` through `ApiClient`, parse, append `#letterboxdUserPage`, run its inline script via a fresh `<script>` element; unmount on hide
- [x] 2.2 Fallback to the configuration page when the fetch fails, `.mainAnimatedPages` is missing, or another `#letterboxdUserPage` is in the document
- [x] 2.3 Probe tests: mount runs `WSU.init`, unmount removes the dashboard, each fallback path navigates to the configuration page; existing config-page probe (account editor) still passes (live: mount runs WSU and renders the dashboard, unmount empties the page on leave; fallback paths reviewed in code, not forced live)

## 3. Entry points

- [x] 3.1 Sidebar link (`#lb-nav-link`) opens the in-app page instead of a full reload
- [x] 3.2 Jellyfin 12: clone `#app-user-menu a[href="#/mypreferencesmenu"]` into a Jellyscribe item, close the popover, open the page; re-add on re-render
- [x] 3.3 Probe tests for both entry points; `SidebarControllerTests` still pins sidebar.js as static embedded content (live: sidebar on 10.11 and avatar menu on 12 both open the page; SidebarControllerTests still green)

## 4. Live verification

- [x] 4.1 Throwaway Jellyfin 10.11.11 under `/jellyfin`: open from the sidebar (no reload, base URL kept), back button, menu navigation, refresh at `#/jellyscribe`, edit an account in the page (2026-10-04, Jellyfin 10.11.11 under /jellyfin with Jellyfin Enhanced: open without reload keeping /jellyfin, back, Home, cold load at #/jellyscribe, account saved from inside the page; layout matches a native tab-less page: header 57px, tabs hidden, 104px top padding)
- [x] 4.2 Throwaway Jellyfin 12.0: avatar-menu entry opens the page, navigation away restores Jellyfin (2026-10-04, Jellyfin 12.0.0: avatar-menu item opens the page, back/Home restore Jellyfin, deep link, account saved)
- [ ] 4.3 The saved test server (`bin/test-server deploy`) for Lachlan's own check

## 5. Release

- [x] 5.1 README (sidebar section), CLAUDE.md (client script), security review gate on the diff
- [x] 5.2 Version 2.10.0 in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`; `## Release notes`; `site/src/data/release-notes.ts` entry
