# Tasks: jellyscribe-in-app-page

## 1. Page lifecycle in sidebar.js

- [ ] 1.1 Page container `#jellyscribe-app-page` (`.page.type-interior.mainAnimatedPage.hide`, `data-url="#/jellyscribe"`, `data-title`), created once under `.mainAnimatedPages`
- [ ] 1.2 Show: `pushState` to `#/jellyscribe` keeping the path, hide the active page with `viewhide`, unhide ours with `viewshow`/`pageshow`, set the title; hide: restore the previous page if Jellyfin has not, start/stop a 300 ms location watcher while shown
- [ ] 1.3 Hide triggers: location leaves `#/jellyscribe`, another page's `viewshow`, a header or menu click that is not our entry; deep link (`#/jellyscribe` on load, `hashchange`, `popstate`)
- [ ] 1.4 Headless-Chrome probe with a stub page container: show, hide on location change, hide on another page's `viewshow`, deep link

## 2. Mounting the dashboard

- [ ] 2.1 Fetch `web/ConfigurationPage?name=letterboxduser` through `ApiClient`, parse, append `#letterboxdUserPage`, run its inline script via a fresh `<script>` element; unmount on hide
- [ ] 2.2 Fallback to the configuration page when the fetch fails, `.mainAnimatedPages` is missing, or another `#letterboxdUserPage` is in the document
- [ ] 2.3 Probe tests: mount runs `WSU.init`, unmount removes the dashboard, each fallback path navigates to the configuration page; existing config-page probe (account editor) still passes

## 3. Entry points

- [ ] 3.1 Sidebar link (`#lb-nav-link`) opens the in-app page instead of a full reload
- [ ] 3.2 Jellyfin 12: clone `#app-user-menu a[href="#/mypreferencesmenu"]` into a Jellyscribe item, close the popover, open the page; re-add on re-render
- [ ] 3.3 Probe tests for both entry points; `SidebarControllerTests` still pins sidebar.js as static embedded content

## 4. Live verification

- [ ] 4.1 Throwaway Jellyfin 10.11.11 under `/jellyfin`: open from the sidebar (no reload, base URL kept), back button, menu navigation, refresh at `#/jellyscribe`, edit an account in the page
- [ ] 4.2 Throwaway Jellyfin 12.0: avatar-menu entry opens the page, navigation away restores Jellyfin
- [ ] 4.3 The saved test server (`bin/test-server deploy`) for Lachlan's own check

## 5. Release

- [ ] 5.1 README (sidebar section), CLAUDE.md (client script), security review gate on the diff
- [ ] 5.2 Version 2.10.0 in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`; `## Release notes`; `site/src/data/release-notes.ts` entry
