/*
 * Jellyscribe client script, injected into Jellyfin's web client (by SidebarScriptStartupFilter, or
 * by the File Transformation plugin). It adds the Jellyscribe entry points (the sidebar on 10.11,
 * the avatar menu on Jellyfin 12) and an in-app page at #/jellyscribe that shows the user dashboard
 * (userPage.html) inside Jellyfin's page container without reloading, the same way Jellyfin
 * Enhanced's Bookmarks works. Anything that stops the page mounting falls back to the dashboard's
 * configuration page, which is what the link opened before. Static content: no user data here.
 */
(function () {
    "use strict";
    if (window.__jellyscribeClient) return;
    window.__jellyscribeClient = true;

    var ROUTE = "#/jellyscribe";
    var PAGE_ID = "jellyscribe-app-page";
    var SLOT_CLASS = "jellyscribe-app-content";
    var DASHBOARD_ID = "letterboxdUserPage";
    var state = { visible: false, previous: null, watcher: null, mounting: 0, previousTitle: "", tabs: null };

    // Any server base URL (e.g. /jellyfin) in front of /web/. Only a plain path prefix is accepted,
    // so an odd pathname can never turn a navigation into one to another host.
    function basePath() {
        var path = window.location.pathname;
        var webAt = path.lastIndexOf("/web/");
        var base = webAt > 0 ? path.substring(0, webAt) : "";
        return /^(\/[A-Za-z0-9._~-]+)*$/.test(base) ? base : "";
    }

    function openConfigPage() {
        window.location.assign(window.location.origin + basePath() + "/web/configurationpage?name=letterboxduser");
    }

    // Exactly our route (optionally with a query), never another route that merely starts with it.
    function onRoute() {
        var hash = window.location.hash || "";
        return hash === ROUTE || hash.indexOf(ROUTE + "?") === 0;
    }

    function signedIn() {
        try { return !!(window.ApiClient && window.ApiClient.accessToken()); } catch (e) { return false; }
    }

    function fire(el, name, detail) {
        el.dispatchEvent(new CustomEvent(name, { bubbles: true, detail: detail || {} }));
    }

    function pageElement() {
        var page = document.getElementById(PAGE_ID);
        if (page) return page;
        var host = document.querySelector(".mainAnimatedPages");
        if (!host) return null;
        page = document.createElement("div");
        page.id = PAGE_ID;
        // The classes Jellyfin 10.11 gives its own tab-less pages (e.g. user preferences): they set
        // the top padding under the header and keep the body's libraryDocument styling.
        page.className = "page type-interior libraryPage noSecondaryNavPage mainAnimatedPage hide";
        page.setAttribute("data-title", "Jellyscribe");
        page.setAttribute("data-url", ROUTE);
        page.setAttribute("data-type", "custom");
        page.setAttribute("data-backbutton", "true");
        var content = document.createElement("div");
        content.setAttribute("data-role", "content");
        var slot = document.createElement("div");
        slot.className = "content-primary " + SLOT_CLASS;
        content.appendChild(slot);
        page.appendChild(content);
        host.appendChild(page);
        if (!document.getElementById(PAGE_ID + "-style")) {
            // The dashboard draws its own full-bleed layout: drop Jellyfin's content gutters and
            // bottom padding; the top is sized to the real header in fitUnderHeader().
            var style = document.createElement("style");
            style.id = PAGE_ID + "-style";
            style.textContent = "#" + PAGE_ID + " { padding-bottom: 0 !important; }" +
                " #" + PAGE_ID + " ." + SLOT_CLASS + " { padding: 0 !important; margin: 0 !important; max-width: none !important; }";
            document.head.appendChild(style);
        }
        return page;
    }

    // libraryPage's top padding reserves room for a tab row this page doesn't have, which left a
    // dark band under the header. Pad by the header actually on screen (Jellyfin 10.11's skin
    // header, or Jellyfin 12's toolbar) instead.
    function fitUnderHeader() {
        var page = document.getElementById(PAGE_ID);
        if (!page || !state.visible) return;
        var bottom = 0;
        Array.prototype.forEach.call(document.querySelectorAll(".skinHeader, .MuiAppBar-root"), function (h) {
            var rect = h.getBoundingClientRect();
            if (rect.height > 0 && getComputedStyle(h).display !== "none") bottom = Math.max(bottom, rect.bottom);
        });
        // Only the part of the header that overlaps the page: Jellyfin 12 already places its pages
        // below the toolbar, 10.11 starts them at the top of the window.
        var overlap = Math.max(0, Math.round(bottom - page.getBoundingClientRect().top));
        if (bottom > 0) page.style.setProperty("padding-top", overlap + "px", "important");
    }

    // A dashboard already in the document outside our page (a configuration-page view Jellyfin is
    // caching): mounting a second copy would make its element lookups resolve to the wrong one.
    function dashboardElsewhere() {
        var existing = document.getElementById(DASHBOARD_ID);
        return !!existing && !existing.closest("#" + PAGE_ID);
    }

    function dashboardUrl() {
        if (window.ApiClient && window.ApiClient.getUrl)
            return window.ApiClient.getUrl("web/ConfigurationPage", { name: "letterboxduser" });
        return basePath() + "/web/ConfigurationPage?name=letterboxduser";
    }

    // Fetch userPage.html as Jellyfin serves it for the configuration page, and mount its dashboard
    // into our page. Scripts inserted as markup never run, so its inline script is re-created.
    function mount(page) {
        var token = ++state.mounting;
        var headers = {};
        try { headers.Authorization = 'MediaBrowser Token="' + window.ApiClient.accessToken() + '"'; } catch (e) { }
        return fetch(dashboardUrl(), { headers: headers, credentials: "same-origin" })
            .then(function (r) {
                if (!r.ok) throw new Error("HTTP " + r.status);
                return r.text();
            })
            .then(function (html) {
                if (token !== state.mounting || !state.visible) return; // left (or re-opened) meanwhile
                var root = new DOMParser().parseFromString(html, "text/html").getElementById(DASHBOARD_ID);
                if (!root) throw new Error("dashboard markup missing");
                var code = [];
                Array.prototype.forEach.call(root.querySelectorAll("script"), function (s) {
                    code.push(s.textContent);
                    s.parentNode.removeChild(s);
                });
                // Our page element is the Jellyfin page; the dashboard is just its content here.
                root.removeAttribute("data-role");
                root.classList.remove("page", "type-interior");
                var slot = page.querySelector("." + SLOT_CLASS);
                slot.innerHTML = "";
                slot.appendChild(document.importNode(root, true));
                code.forEach(function (text) {
                    var s = document.createElement("script");
                    s.textContent = text;
                    slot.appendChild(s);
                });
            });
    }

    function show() {
        if (state.visible) return;
        if (dashboardElsewhere()) { openConfigPage(); return; }
        var page = pageElement();
        if (!page) { openConfigPage(); return; }

        state.visible = true;
        if (!onRoute())
            history.pushState({ page: "jellyscribe" }, "Jellyscribe", window.location.pathname + window.location.search + ROUTE);

        var active = document.querySelector(".mainAnimatedPage:not(.hide):not(#" + PAGE_ID + ")");
        if (active) {
            state.previous = active;
            active.classList.add("hide");
            fire(active, "viewhide", { type: "interior" });
        }
        hideHeaderTabs();
        page.classList.remove("hide");
        fire(page, "viewshow", { type: "custom", isRestored: false, options: {} });
        fire(page, "pageshow");
        fitUnderHeader();
        setTimeout(fitUnderHeader, 400); // after the header settles from hiding the tabs
        // Never remember our own title (or the router's "Page not found") as the one to restore.
        if (document.title !== "Jellyscribe" && document.title !== "Page not found") state.previousTitle = document.title;
        settleTitle();
        startWatcher();

        var attempt = state.mounting + 1; // the token mount() is about to take
        mount(page).catch(function (err) {
            // Left (or re-opened) while the fetch was in flight: that failure is no longer ours to act on.
            if (attempt !== state.mounting || !state.visible) return;
            if (window.console) console.warn("[Jellyscribe] in-app page failed, opening the settings page instead:", err && err.message);
            state.visible = false;
            openConfigPage();
        });
    }

    function hide() {
        if (!state.visible) return;
        state.visible = false;
        state.mounting++;
        stopWatcher();

        var page = document.getElementById(PAGE_ID);
        if (page) {
            page.classList.add("hide");
            var slot = page.querySelector("." + SLOT_CLASS);
            if (slot) slot.innerHTML = ""; // unmount: never leave a hidden copy behind
            try { delete window.WSU; } catch (e) { window.WSU = undefined; } // and drop the data it held
            fire(page, "viewhide", { type: "custom" });
        }
        restoreHeaderTabs();
        var shownByJellyfin = document.querySelector(".mainAnimatedPage:not(.hide):not(#" + PAGE_ID + ")");
        if (state.previous && !shownByJellyfin) {
            state.previous.classList.remove("hide");
            fire(state.previous, "viewshow", { type: "interior", isRestored: true });
        }
        settleTitle();
        state.previous = null;
    }

    // Jellyfin sets the tab title from its own page events, sometimes after ours (its "Page not
    // found" for our route, or Jellyfin 12 keeping the last title), so assert the right one again
    // once the page has settled.
    function settleTitle() {
        var apply = function () {
            if (state.visible) document.title = "Jellyscribe";
            else if (document.title === "Jellyscribe" || document.title === "Page not found") document.title = state.previousTitle || "Jellyfin";
        };
        apply();
        setTimeout(apply, 400);
        setTimeout(apply, 1500);
    }

    // A tab-less page: hide the previous page's header tabs (Home / Favorites) the way Jellyfin does
    // for its own, and put back exactly what was there when ours is left.
    function hideHeaderTabs() {
        var tabs = document.querySelector(".headerTabs");
        state.tabs = {
            el: tabs,
            wasHidden: !tabs || tabs.classList.contains("hide"),
            bodyHadTabs: document.body.classList.contains("withSectionTabs"),
        };
        if (tabs) tabs.classList.add("hide");
        document.body.classList.remove("withSectionTabs");
        document.body.classList.add("libraryDocument");
    }

    function restoreHeaderTabs() {
        var saved = state.tabs;
        state.tabs = null;
        if (!saved) return;
        if (saved.el && !saved.wasHidden) saved.el.classList.remove("hide");
        if (saved.bodyHadTabs) document.body.classList.add("withSectionTabs");
    }

    // Jellyfin's router navigates with pushState, which fires no event, so poll while shown.
    function startWatcher() {
        stopWatcher();
        state.watcher = setInterval(function () { if (!onRoute()) hide(); }, 300);
    }

    function stopWatcher() {
        if (state.watcher) { clearInterval(state.watcher); state.watcher = null; }
    }

    // Show once Jellyfin's page container exists and the user is signed in (a deep link or refresh
    // at #/jellyscribe arrives before either).
    function showWhenReady() {
        var tries = 0;
        (function attempt() {
            if (!onRoute() || state.visible) return;
            if (signedIn() && document.querySelector(".mainAnimatedPages")) { show(); return; }
            if (++tries < 100) setTimeout(attempt, 200);
        })();
    }

    // Capture phase on window, so this runs before Jellyfin's router: our route is handled here and
    // the event stopped (the router would otherwise render its "Page not found" for it); any other
    // route passes through untouched after our page is hidden.
    function interceptNavigation(e) {
        if (onRoute()) {
            e.stopImmediatePropagation();
            showWhenReady();
        } else {
            hide();
        }
    }

    window.addEventListener("resize", fitUnderHeader);
    window.addEventListener("hashchange", interceptNavigation, true);
    window.addEventListener("popstate", interceptNavigation, true);

    // Another page shown while ours is up. Real navigation changes the location first, so if the
    // location is still ours this is the router reacting to our route (its "Page not found", e.g.
    // on a cold load at #/jellyscribe): keep ours and hide that one. Otherwise the user left.
    document.addEventListener("viewshow", function (e) {
        var target = e.target;
        if (!state.visible || !target || target.id === PAGE_ID || !target.classList || !target.classList.contains("mainAnimatedPage"))
            return;
        if (onRoute()) { target.classList.add("hide"); settleTitle(); }
        else hide();
    }, true);

    function openFromEntry(e) {
        e.preventDefault();
        show();
    }

    // Jellyfin 10.11 (and the legacy drawer): before Settings in the sidebar.
    function addSidebarLink() {
        if (document.getElementById("lb-nav-link")) return;
        var settingsLink = document.querySelector(".btnSettings");
        if (!settingsLink) return;
        var link = document.createElement("a");
        link.id = "lb-nav-link";
        link.setAttribute("is", "emby-linkbutton");
        link.className = "navMenuOption lnkMediaFolder";
        link.href = "#";
        link.innerHTML = '<span class="material-icons navMenuOptionIcon movie_filter" aria-hidden="true"></span><span class="navMenuOptionText">Jellyscribe</span>';
        link.addEventListener("click", openFromEntry);
        settingsLink.parentElement.insertBefore(link, settingsLink);
    }

    // Jellyfin 12's layout has no drawer: add an item to the avatar menu, cloned from its Settings
    // item so it carries the menu's own styling.
    function addUserMenuLink() {
        if (document.getElementById("lb-user-menu-link")) return;
        var settingsItem = document.querySelector('#app-user-menu a[href="#/mypreferencesmenu"]');
        if (!settingsItem) return;
        var item = settingsItem.cloneNode(true);
        item.id = "lb-user-menu-link";
        item.href = "#";
        var icon = item.querySelector("svg");
        if (icon) {
            var replacement = document.createElement("span");
            replacement.className = "material-icons";
            replacement.setAttribute("aria-hidden", "true");
            replacement.textContent = "movie_filter";
            icon.parentNode.replaceChild(replacement, icon);
        }
        var text = item.querySelector(".MuiListItemText-primary");
        if (text) text.textContent = "Jellyscribe";
        item.addEventListener("click", function (e) {
            e.stopPropagation();
            var menu = document.getElementById("app-user-menu");
            var backdrop = menu && menu.querySelector(":scope > .MuiBackdrop-root");
            if (backdrop) backdrop.click(); // closes the popover; this is not a real MenuItem
            openFromEntry(e);
        });
        settingsItem.parentNode.insertBefore(item, settingsItem.nextSibling);
    }

    function addEntryPoints() {
        addSidebarLink();
        addUserMenuLink();
    }

    // The avatar menu is a popover rendered into <body> on open; watch only body's direct children.
    if (window.MutationObserver) {
        new MutationObserver(addUserMenuLink).observe(document.body || document.documentElement, { childList: true });
    }
    setInterval(addEntryPoints, 2000);
    if (document.readyState === "complete") {
        setTimeout(addEntryPoints, 500);
    } else {
        window.addEventListener("load", function () { setTimeout(addEntryPoints, 500); });
    }
    if (onRoute()) showWhenReady();
})();
