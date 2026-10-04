(function() {
    function addLbLink() {
        if (document.getElementById("lb-nav-link")) return;
        var settingsLink = document.querySelector(".btnSettings");
        if (!settingsLink) return;
        var link = document.createElement("a");
        link.id = "lb-nav-link";
        link.setAttribute("is", "emby-linkbutton");
        link.className = "navMenuOption lnkMediaFolder";
        link.href = "#";
        link.innerHTML = '<span class="material-icons navMenuOptionIcon movie_filter" aria-hidden="true"></span><span class="navMenuOptionText">Jellyscribe</span>';
        link.addEventListener("click", function(e) {
            e.preventDefault();
            e.stopPropagation();
            // Keep any server base URL (e.g. /jellyfin) in front of /web/. Only a plain path prefix
            // is accepted, and the URL is built on this page's own origin, so an odd pathname can
            // never turn this into a navigation to another host.
            var path = window.location.pathname;
            var webAt = path.lastIndexOf("/web/");
            var base = webAt > 0 ? path.substring(0, webAt) : "";
            if (!/^(\/[A-Za-z0-9._~-]+)*$/.test(base)) base = "";
            window.location.assign(window.location.origin + base + "/web/configurationpage?name=letterboxduser");
        });
        settingsLink.parentElement.insertBefore(link, settingsLink);
    }
    setInterval(addLbLink, 2000);
    if (document.readyState === "complete") {
        setTimeout(addLbLink, 500);
    } else {
        window.addEventListener("load", function() { setTimeout(addLbLink, 500); });
    }
})();
