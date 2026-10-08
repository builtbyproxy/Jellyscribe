/*
 * Code shared by both Jellyscribe dashboards: the admin settings page (configPage.html, window.WS) and the
 * user page (userPage.html, window.WSU, also mounted in-app by sidebar.js). Served by SidebarController
 * with ?v=<plugin version>. Static: it holds no user or server data.
 *
 * It registers one factory per build, under the version stamped in at build time. A page asks for the
 * factory of its own version, so a page served after an upgrade never runs against an older copy of this
 * file still loaded in the same tab. The factory takes the page's root element and returns methods the
 * page mixes into its own object (the page's own methods win). Every lookup goes through that root.
 *
 * Each page supplies: getJson(path) (a promise of the parsed reply, rejected on any failure);
 * post(path, body) (a promise of { ok, status, json() } for any HTTP reply, rejected only when the server
 * could not be reached); noActivityText, overviewWhat, retryable, modalIds and historyBodies; and the
 * page-specific actions it wires up (runSync, runWatchlist, saveAccount, deleteAccount, verifyAccount,
 * populateReviewAccountDropdown, and onSection(sec) when a nav section needs loading).
 */
(function () {
    var registry = window.JellyscribeShared = window.JellyscribeShared || {};
    registry['@@JELLYSCRIBE_VERSION@@'] = function (root) {
        function byId(id) { return root.querySelector('#' + id); }
        return {
            byId: byId,

            ensureViewport: function () {
                try {
                    var vp = document.querySelector('meta[name="viewport"]');
                    if (!vp) { vp = document.createElement('meta'); vp.setAttribute('name', 'viewport'); (document.head || document.documentElement).appendChild(vp); }
                    if (!/width\s*=\s*device-width/i.test(vp.getAttribute('content') || '')) vp.setAttribute('content', 'width=device-width, initial-scale=1');
                } catch (e) {}
            },
            /* Follow Jellyfin's theme rather than the OS: the light palette when the background Jellyfin's
               theme paints is light. The theme paints its .backgroundContainer (10.11 keeps a fixed dark
               colour on the document itself until the app has loaded), else the nearest painted element
               behind this page (Jellyfin 12's dashboard paints the document). Outside Jellyfin's web
               client nothing is painted, so the OS setting decides there, and the browser's default body
               margin is dropped (that document is this page alone). */
            applyTheme: function () {
                var light = null, behind = [document.querySelector('.backgroundContainer')];
                for (var el = root.parentElement; el; el = el.parentElement) behind.push(el);
                for (var i = 0; i < behind.length; i++) {
                    var c = behind[i] && /rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?/.exec(getComputedStyle(behind[i]).backgroundColor || '');
                    if (c && (c[4] === undefined || parseFloat(c[4]) >= 0.5)) { light = 0.2126 * c[1] + 0.7152 * c[2] + 0.0722 * c[3] > 140; break; }
                }
                if (light === null) {
                    light = !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches);
                    if (root.parentElement === document.body) document.body.style.margin = '0';
                }
                root.classList.toggle('ws-light', light);
            },
            // Themes now, again once Jellyfin's theme stylesheet has loaded (a cold start loads it late), and
            // on every return to the page.
            followTheme: function () {
                var self = this;
                self.applyTheme();
                [1000, 4000].forEach(function (ms) { setTimeout(function () { if (root.isConnected) self.applyTheme(); }, ms); });
                root.addEventListener('viewshow', function () { self.applyTheme(); });
            },

            esc: function (s) { return String(s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); },
            escAttr: function (s) { return this.esc(s); },
            // A short date for the title line on phones, where the When column is hidden.
            shortDate: function (d) {
                var opts = { day: 'numeric', month: 'short' };
                if (d.getFullYear() !== new Date().getFullYear()) opts.year = 'numeric';
                return d.toLocaleDateString([], opts);
            },
            // Today in the viewer's own time zone, as yyyy-mm-dd (toISOString would give the UTC date).
            localDate: function () {
                var d = new Date(), pad = function (n) { return (n < 10 ? '0' : '') + n; };
                return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
            },
            errorHtml: function (what, retry) {
                return '<span class="ws-red">Couldn\'t load ' + what + '.</span> <button type="button" class="ws-btn" data-retry="' + retry + '">Retry</button>';
            },
            errorRow: function (what, retry) {
                return '<tr><td colspan="5" style="padding:2em;text-align:center;">' + this.errorHtml(what, retry) + '</td></tr>';
            },
            emptyRow: function (msg) {
                return '<tr><td colspan="5" style="padding:2em;text-align:center;" class="ws-muted">' + msg + '</td></tr>';
            },
            unreachable: function () { return '<span class="ws-red">Couldn\'t reach the server. Try again.</span>'; },
            // The server's reason for a failed request: its JSON error, else the HTTP status.
            errText: function (err) {
                if (!err) return Promise.resolve('unknown error');
                if (err.json) return err.json().then(function (d) { return (d && d.error) || err.statusText || ('HTTP ' + err.status); }, function () { return err.statusText || ('HTTP ' + err.status); });
                return Promise.resolve(err.message || String(err));
            },
            saveFailed: function (err, statusId, what) {
                var self = this;
                self.errText(err).then(function (msg) {
                    byId(statusId || 'mStatus').innerHTML = '<span class="ws-red">' + (what || 'Save failed') + ': ' + self.esc(msg) + '</span>';
                });
            },

            /* ===== Wiring both pages share ===== */
            wireCommon: function () {
                var self = this, nav = byId('wsNav');
                nav.addEventListener('click', function (e) {
                    var a = e.target.closest('button[data-sec]'); if (!a) return;
                    nav.querySelectorAll('button[data-sec]').forEach(function (x) { x.classList.remove('active'); x.removeAttribute('aria-current'); });
                    a.classList.add('active'); a.setAttribute('aria-current', 'page');
                    var sec = a.getAttribute('data-sec');
                    root.querySelectorAll('.ws-view').forEach(function (v) { v.classList.toggle('show', v.getAttribute('data-sec') === sec); });
                    if (self.onSection) self.onSection(sec);
                });
                byId('ovFilters').addEventListener('click', function (e) {
                    var c = e.target.closest('.ws-chip'); if (!c) return;
                    this.querySelectorAll('.ws-chip').forEach(function (x) { x.classList.remove('on'); }); c.classList.add('on');
                    self.ovFilter = c.getAttribute('data-f');
                    self.ovPage = 0;
                    self.renderOverview();
                    self.syncLabels();
                });
                byId('ovStatusFilters').addEventListener('click', function (e) {
                    var c = e.target.closest('.ws-chip'); if (!c) return;
                    this.querySelectorAll('.ws-chip').forEach(function (x) { x.classList.remove('on'); }); c.classList.add('on');
                    self.ovStatus = c.getAttribute('data-s');
                    self.ovPage = 0;
                    self.renderOverview();
                });
                byId('historySearch').addEventListener('input', function () { self.ovQuery = this.value.trim().toLowerCase(); self.ovPage = 0; self.renderOverview(); });
                byId('historyMore').addEventListener('click', function () { self.loadMore(); });
                byId('historyPrev').addEventListener('click', function () { if ((self.ovPage || 0) > 0) { self.ovPage--; self.renderOverview(); } });
                byId('historyNext').addEventListener('click', function () { self.ovPage = (self.ovPage || 0) + 1; self.renderOverview(); });
                byId('runSyncBtn').addEventListener('click', function () { self.runSync(); });
                byId('runWatchlistBtn').addEventListener('click', function () { self.runWatchlist(); });
                byId('mCancelBtn').addEventListener('click', function () { self.closeModal('acctModal'); });
                byId('mSaveBtn').addEventListener('click', function () { self.saveAccount(); });
                byId('mDeleteBtn').addEventListener('click', function () { self.confirmRemove(true); });
                byId('mConfirmKeepBtn').addEventListener('click', function () { self.confirmRemove(false); });
                byId('mConfirmRemoveBtn').addEventListener('click', function () { self.deleteAccount(); });
                byId('mVerifyBtn').addEventListener('click', function () { self.verifyAccount(); });
                byId('mSvc').addEventListener('change', function () { self.onSvcChange(); });
                byId('reviewCancel').addEventListener('click', function () { self.closeModal('reviewModal'); });
                byId('reviewSubmit').addEventListener('click', function () { self.submitReview(); });
                byId('reviewRewatch').addEventListener('change', function () {
                    byId('rewatchDateRow').style.display = this.checked ? '' : 'none';
                });
                // Close any modal via the ✕ button, the dimmed backdrop, or Escape (the dialog's own). The
                // backdrop counts only when the press started there too: selecting text in a modal and
                // releasing outside it must not throw the draft away.
                self.modalIds.forEach(function (id) {
                    var ov = byId(id), pressedBackdrop = false;
                    ov.addEventListener('pointerdown', function (e) { pressedBackdrop = e.target === ov; });
                    ov.addEventListener('click', function (e) { if (e.target === ov && pressedBackdrop) self.closeModal(id); pressedBackdrop = false; });
                    ov.addEventListener('close', function () {
                        // A closed account dialog keeps no typed password or cookies in the page.
                        if (id === 'acctModal') { byId('mPassword').value = ''; byId('mAuthBlob').value = ''; }
                        self.restoreFocus(id);
                    });
                });
                root.querySelectorAll('.ws-modal-x').forEach(function (x) {
                    x.addEventListener('click', function () { var ov = this.closest('.ws-modal-ov'); if (ov) self.closeModal(ov.id); });
                });
                // As a configuration page Jellyfin hides this page (keeping it cached) when another is
                // opened: never leave a dialog up over that one.
                root.addEventListener('viewhide', function () { self.modalIds.forEach(function (id) { self.closeModal(id); }); });
                root.addEventListener('click', function (e) {
                    var r = e.target.closest('[data-retry]'), fn = r && r.getAttribute('data-retry');
                    // Only the loaders a Retry button can name.
                    if (self.retryable.indexOf(fn) >= 0) self[fn]();
                });
                self.historyBodies.forEach(function (id) {
                    byId(id).addEventListener('click', function (e) {
                        var g = e.target.closest('.ws-grp-btn');
                        if (g) { self.toggleGroup(this, g); return; }
                        var b = e.target.closest('.ws-review-btn'); if (!b) return;
                        self.openReview(b.getAttribute('data-svc'), parseInt(b.getAttribute('data-tmdb'), 10) || 0, b.getAttribute('data-title'),
                            b.getAttribute('data-slug'), b.getAttribute('data-season'), b.getAttribute('data-episode'));
                    });
                });
                // The rating is a slider: arrows move it by half a star, Home clears it, End is five stars.
                byId('starRating').addEventListener('keydown', function (e) {
                    var v = parseFloat(byId('reviewRating').value) || 0;
                    var step = { ArrowRight: 0.5, ArrowUp: 0.5, ArrowLeft: -0.5, ArrowDown: -0.5, PageUp: 1, PageDown: -1 }[e.key];
                    if (e.key === 'Home') v = 0; else if (e.key === 'End') v = 5; else if (step) v = Math.min(5, Math.max(0, v + step)); else return;
                    e.preventDefault();
                    self.paintStars(v || null);
                });
                root.querySelectorAll('#starRating .ws-star').forEach(function (star) {
                    star.addEventListener('click', function (e) {
                        var val = parseFloat(this.getAttribute('data-val'));
                        if (e.offsetX < this.offsetWidth / 2) val -= 0.5;
                        self.paintStars(val);
                    });
                });
            },

            /* ===== Overview: stats, the merged activity list of both diaries ===== */
            loadOverview: function () {
                var self = this, LB = 'Jellyfin.Plugin.LetterboxdSync', SZ = 'Jellyfin.Plugin.LetterboxdSync/Serializd';
                self.histSrc = { letterboxd: { base: LB, loaded: 0, total: 0, oldest: null }, serializd: { base: SZ, loaded: 0, total: 0, oldest: null } };
                var gen = self.histGen = (self.histGen || 0) + 1;
                Promise.all([self.getJson(LB + '/Stats'), self.getJson(SZ + '/Stats'), self.fetchHistory('letterboxd'), self.fetchHistory('serializd')])
                    .then(function (res) {
                        if (gen !== self.histGen) return;
                        var fs = res[0] || {}, ts = res[1] || {};
                        byId('statFilm').textContent = fs.total || 0;
                        byId('statTv').textContent = ts.total || 0;
                        var fw = fs.watchlist, tw = ts.watchlist;
                        byId('statWatchlist').textContent = (fw == null && tw == null) ? '\u2014' : ((fw || 0) + (tw || 0));
                        byId('statWatchlistSub').textContent = (fw == null && tw == null) ? 'run a watchlist sync' : ((tw || 0) + ' shows · ' + (fw || 0) + ' films');
                        byId('statSynced').textContent = (fs.success || 0) + (ts.success || 0);
                        byId('statRewatches').textContent = (fs.rewatches || 0) + (ts.rewatches || 0);
                        byId('statFailed').textContent = (fs.failed || 0) + (ts.failed || 0);
                        self.merged = []; self.seen = {};
                        self.addEvents(res[2].concat(res[3]));
                        self.renderOverview();
                        self.renderSparks();
                        self.renderMore();
                    }).catch(function () {
                        if (gen !== self.histGen) return;
                        byId('historyBody').innerHTML = self.errorRow(self.overviewWhat, 'loadOverview');
                        byId('historyPagination').style.display = 'none';
                        byId('historyMoreRow').style.display = 'none';
                    });
            },
            // The History endpoints page by offset. The server caps a page at 250; ask for 200
            // per page and read the server's total to know whether more is left.
            histChunk: 200,
            byNewest: function (events) { return events.sort(function (a, b) { return new Date(b.Timestamp) - new Date(a.Timestamp); }); },
            fetchHistory: function (svc) {
                var self = this, h = self.histSrc[svc];
                return self.getJson(h.base + '/History?count=' + self.histChunk + '&offset=' + h.loaded).then(function (d) {
                    var events = (d && d.events) || [];
                    h.loaded += events.length;
                    h.total = events.length ? Math.max((d && d.total) || 0, h.loaded) : h.loaded;
                    if (events.length) h.oldest = new Date(events[events.length - 1].Timestamp).getTime();
                    return events.map(function (e) { e._svc = svc; return e; });
                });
            },
            // Offset paging: an event logged between two loads shifts the older ones down, so the
            // next page starts with rows already shown. Keep one copy of each.
            eventKey: function (e) { return [e._svc, e.Timestamp, e.UserId || e.Username || '', e.Account || '', e.FilmTitle, e.Status, e.Source, e.TmdbId].join('|'); },
            addEvents: function (events) {
                var self = this, fresh = events.filter(function (e) {
                    var k = self.eventKey(e);
                    if (self.seen[k]) return false;
                    self.seen[k] = true; return true;
                });
                self.merged = self.byNewest(self.merged.concat(fresh));
            },
            // Each service pages on its own, so past the oldest loaded event of a service that has
            // more, the merged list would skip that service's rows. Show only what is complete.
            visibleEvents: function (only) {
                var cut = null, src = this.histSrc || {};
                Object.keys(src).forEach(function (svc) {
                    var h = src[svc];
                    if ((!only || only === 'all' || only === svc) && h.loaded < h.total && h.oldest != null && (cut == null || h.oldest > cut)) cut = h.oldest;
                });
                return cut == null ? this.merged : this.merged.filter(function (e) { return new Date(e.Timestamp).getTime() >= cut; });
            },
            // Search and filters cover the loaded events only; say so while older ones are left.
            noMatchText: function () {
                var src = this.histSrc || {}, more = Object.keys(src).some(function (k) { return src[k].loaded < src[k].total; });
                return more ? 'Nothing matches in the loaded history. Load older history to look further back.' : 'No items match these filters.';
            },
            renderMore: function () {
                var lb = this.histSrc.letterboxd, sz = this.histSrc.serializd;
                var more = lb.loaded < lb.total || sz.loaded < sz.total;
                byId('historyMoreRow').style.display = more ? 'flex' : 'none';
                byId('historyMoreInfo').textContent = (lb.loaded + sz.loaded) + ' of ' + (lb.total + sz.total) + ' events loaded';
            },
            loadMore: function () {
                var self = this, btn = byId('historyMore'), info = byId('historyMoreInfo'), jobs = [], gen = self.histGen;
                if (btn.disabled) return;
                Object.keys(self.histSrc).forEach(function (svc) {
                    var h = self.histSrc[svc];
                    if (h.loaded >= h.total) return;
                    // A reload since this started replaced the list; its pages would not line up.
                    jobs.push(self.fetchHistory(svc).then(function (events) { if (gen === self.histGen) self.addEvents(events); }));
                });
                btn.disabled = true;
                info.textContent = 'Loading…';
                Promise.all(jobs).then(function () {
                    btn.disabled = false; if (gen !== self.histGen) return; self.renderOverview(); self.renderMore();
                }, function () {
                    btn.disabled = false; if (gen !== self.histGen) return; self.renderOverview(); self.renderMore();
                    info.innerHTML = '<span class="ws-red">Couldn\'t load older history. Try again.</span>';
                });
            },
            renderSparks: function () {
                var self = this, days = 14, now = Date.now(), dayMs = 86400000;
                function build(svc, stroke, fill) {
                    var buckets = new Array(days).fill(0);
                    self.merged.forEach(function (e) {
                        var idx = days - 1 - Math.floor((now - new Date(e.Timestamp).getTime()) / dayMs);
                        // Rating pushes (status 5) aren't logged watches; keep them off the trend line.
                        if (e._svc === svc && e.Status !== 5 && e.Status !== 'Rated' && idx >= 0 && idx < days) buckets[idx]++;
                    });
                    var max = Math.max.apply(null, buckets.concat([1])), w = 120, h = 26, step = w / (days - 1);
                    var pts = buckets.map(function (v, i) { return [Math.round(i * step), Math.round(h - 2 - (v / max) * (h - 5))]; });
                    var line = pts.map(function (p, i) { return (i ? 'L' : 'M') + p[0] + ' ' + p[1]; }).join(' ');
                    return '<path d="' + line + '" fill="none" stroke="' + stroke + '" stroke-width="1.6"/>' +
                        '<path d="' + line + ' L' + w + ' ' + h + ' L0 ' + h + ' Z" fill="' + fill + '"/>';
                }
                var sf = byId('sparkFilm'), st = byId('sparkTv');
                if (sf) sf.innerHTML = build('letterboxd', 'var(--ws-film)', 'var(--ws-film-soft)');
                if (st) st.innerHTML = build('serializd', 'var(--ws-tv)', 'var(--ws-tv-soft)');
            },
            statusOf: function (e) {
                if (e.Source === 'review') return 'Reviewed';
                return typeof e.Status === 'string' ? e.Status : (['Success', 'Skipped', 'Failed', 'Rewatch', 'Requested', 'Rated'][e.Status] || 'Success');
            },
            matchesStatus: function (e) {
                var s = this.ovStatus || 'all';
                if (s === 'all') return true;
                var st = this.statusOf(e);
                if (s === 'hide-skipped') return st !== 'Skipped';
                return st === s;
            },
            renderOverview: function () {
                var self = this, f = this.ovFilter || 'all', q = this.ovQuery || '';
                var all = this.visibleEvents(f).filter(function (e) {
                    return (f === 'all' || e._svc === f) && self.matchesStatus(e) && (!q || (e.FilmTitle || '').toLowerCase().indexOf(q) >= 0);
                });
                var units = this.groupRuns(all);
                var ps = this.ovPageSize || 40, total = units.length, pages = Math.max(1, Math.ceil(total / ps));
                if ((this.ovPage || 0) >= pages) this.ovPage = pages - 1;
                var start = (this.ovPage || 0) * ps;
                byId('historyBody').innerHTML = units.length ? this.unitsHtml(units.slice(start, start + ps)) : this.emptyRow(this.merged.length ? this.noMatchText() : this.noActivityText);
                var pag = byId('historyPagination');
                if (total > ps) {
                    pag.style.display = 'flex';
                    byId('historyPageInfo').textContent = (start + 1) + '–' + Math.min(start + ps, total) + ' of ' + total;
                    byId('historyPrev').disabled = (this.ovPage || 0) === 0;
                    byId('historyNext').disabled = (this.ovPage || 0) >= pages - 1;
                } else { pag.style.display = 'none'; }
            },
            whenOf: function (e) {
                var ts = new Date(e.Timestamp);
                return ts.toLocaleDateString() + ' ' + ts.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
            },
            pillHtml: function (status, reason) {
                status = this.esc(status);
                if (!reason) return '<span class="ws-pill ' + status + '">' + status + '</span>';
                return '<span class="ws-pill ' + status + ' has-reason" title="' + this.esc(reason) + '">' + status + ' <span class="ws-info-i">i</span></span>';
            },
            // src names the diary for an event without its own _svc.
            rowHtml: function (e, src, trAttrs) {
                var self = this, source = e._svc || src, svc = source === 'serializd' ? 'tv' : 'film';
                var status = self.statusOf(e), raw = self.statusOf({ Status: e.Status });
                var title = (e.FilmTitle || '').split(' · '), t = title[0], m = title[1] || '';
                // A Letterboxd review needs the film's slug; rows without one (a paused or failed lookup) can't be reviewed.
                var canReview = (raw === 'Success' || raw === 'Rewatch' || raw === 'Skipped') && (source === 'serializd' || !!e.FilmSlug);
                var seM = (source === 'serializd') ? (m || '').match(/S(\d+)E(\d+)/) : null;
                var btn = (canReview && e.TmdbId) ? '<button type="button" class="ws-review-btn" data-svc="' + self.esc(source) + '" data-tmdb="' + self.esc(e.TmdbId) +
                    '" data-title="' + self.esc(t) + '" data-slug="' + self.esc(e.FilmSlug) + '" data-season="' + (seM ? seM[1] : '') + '" data-episode="' + (seM ? seM[2] : '') + '">Review</button>' : '';
                return '<tr' + (trAttrs || '') + '><td><div class="ws-titlecell ws-tc"><span class="ws-badge ' + svc + '">' + (svc === 'tv' ? 'TV' : 'MV') + '</span><div><div class="t">' + self.esc(t) + '</div><div class="m">' + self.esc(m) + '<span class="ws-mwhen">' + (m ? ' · ' : '') + self.shortDate(new Date(e.Timestamp)) + '</span></div></div></div></td><td>' + self.pillHtml(status, e.Error) + '</td><td class="ws-src">' + self.esc(e.Source) + '</td><td class="ws-when">' + self.whenOf(e) + '</td><td>' + btn + '</td></tr>';
            },
            // Consecutive episodes of one show collapse into one expandable row, so a binge
            // doesn't push everything else off the page. A run of one stays a plain row.
            groupRuns: function (events) {
                var units = [], episode = /^S\d+E\d+$/;
                events.forEach(function (e) {
                    var parts = (e.FilmTitle || '').split(' · '), last = units[units.length - 1];
                    var isEpisode = e._svc === 'serializd' && episode.test(parts[1] || '');
                    // One run is one show on one account; another account's watch starts a new row.
                    var owner = (e.UserId || e.Username || '') + '|' + (e.Account || '');
                    if (isEpisode && last && last.show === parts[0] && last.owner === owner) { last.events.push(e); return; }
                    units.push({ show: isEpisode ? parts[0] : null, owner: owner, events: [e] });
                });
                return units;
            },
            unitsHtml: function (units) {
                var self = this;
                return units.map(function (u, i) {
                    if (u.events.length < 2) return self.rowHtml(u.events[0]);
                    var attrs = ' class="ws-grp-item" data-grp-of="' + i + '" hidden';
                    return self.groupHtml(u, i) + u.events.map(function (e) { return self.rowHtml(e, null, attrs); }).join('');
                }).join('');
            },
            groupHtml: function (u, id) {
                var self = this, newest = u.events[0];
                // The range runs from the lowest to the highest episode, whatever order they were watched in.
                var eps = u.events.map(function (e) { var m = /^S(\d+)E(\d+)$/.exec((e.FilmTitle || '').split(' · ')[1] || ''); return { s: +m[1], e: +m[2] }; })
                    .sort(function (a, b) { return a.s - b.s || a.e - b.e; });
                var epName = function (x) { return 'S' + x.s + 'E' + x.e; }, first = eps[0], lastEp = eps[eps.length - 1];
                var range = epName(first) === epName(lastEp) ? epName(first) : epName(first) + '–' + epName(lastEp);
                var uniq = function (fn) { return u.events.map(fn).filter(function (v, i, a) { return a.indexOf(v) === i; }); };
                var statuses = uniq(function (e) { return self.statusOf(e); }), sources = uniq(function (e) { return e.Source || ''; });
                // A failure inside a run must show on its folded row, not only once it is opened.
                var failed = u.events.filter(function (e) { return self.statusOf(e) === 'Failed'; }).length;
                var pill = statuses.length === 1 ? self.pillHtml(statuses[0]) : (failed ? '<span class="ws-pill Failed">' + failed + ' failed</span>' : '<span class="ws-pill Skipped">Mixed</span>');
                return '<tr class="ws-grp"><td><button type="button" class="ws-grp-btn ws-tc" data-grp="' + id + '" aria-expanded="false"><span class="ws-badge tv">TV</span><div><div class="t">' + self.esc(u.show) +
                    ' <span class="ws-grp-caret" aria-hidden="true">▸</span></div><div class="m">' + u.events.length + ' episodes · ' + self.esc(range) + '<span class="ws-mwhen"> · ' + self.shortDate(new Date(newest.Timestamp)) + '</span></div></div></button></td><td>' + pill +
                    '</td><td class="ws-src">' + (sources.length === 1 ? self.esc(sources[0]) : '') + '</td><td class="ws-when">' + self.whenOf(newest) + '</td><td></td></tr>';
            },
            toggleGroup: function (body, btn) {
                var open = btn.getAttribute('aria-expanded') !== 'true';
                btn.setAttribute('aria-expanded', String(open));
                body.querySelectorAll('[data-grp-of="' + btn.getAttribute('data-grp') + '"]').forEach(function (r) { r.hidden = !open; });
            },

            /* ===== Sync buttons and the live progress watcher ===== */
            syncLabels: function () {
                var f = this.ovFilter, svc = f === 'serializd' ? 'TV' : (f === 'letterboxd' ? 'film' : 'all');
                byId('runSyncBtn').textContent = f === 'all' ? 'Sync all now' : 'Sync ' + svc + ' now';
                byId('runWatchlistBtn').textContent = f === 'all' ? 'Sync all watchlists' : 'Sync ' + svc + ' watchlist';
            },
            setSyncBusy: function (busy) { byId('runSyncBtn').disabled = busy; byId('runWatchlistBtn').disabled = busy; },
            getProgress: function () { return this.getJson('Jellyfin.Plugin.LetterboxdSync/Progress').catch(function () { return null; }); },
            startWatcher: function (label) {
                var self = this, w = self.watcher || (self.watcher = {});
                if (w.timer) clearInterval(w.timer);
                w.sawRunning = false; w.ticks = 0; w.failures = 0;
                byId('jwTitle').textContent = label + '…';
                byId('jwPhase').textContent = 'Starting…';
                byId('jwCounts').textContent = '';
                byId('jwElapsed').textContent = '';
                var fill = byId('jwFill'); fill.style.width = '0%'; fill.classList.add('indet');
                byId('jwSpin').classList.remove('done'); byId('jwSpin').innerHTML = '';
                byId('jobWatcher').style.display = 'block';
                self.setSyncBusy(true);
                w.timer = setInterval(function () { self.pollProgress(); }, 1000);
                self.pollProgress();
            },
            pollProgress: function () {
                var self = this, w = self.watcher;
                // The page has left the document (the in-app page was left, or Jellyfin dropped the cached
                // configuration page): stop polling.
                if (!root.isConnected) { clearInterval(w.timer); w.timer = null; return; }
                w.ticks++;
                self.getProgress().then(function (p) {
                    if (!w.timer) return; // finished while this poll was in flight
                    // A failed poll counts too: after several in a row, stop and say so rather than spin forever.
                    if (!p) { if (++w.failures > 6) self.finishWatcher('unknown'); return; }
                    w.failures = 0;
                    if (p.isRunning) {
                        w.sawRunning = true;
                        byId('jwTitle').textContent = p.taskName || 'Syncing';
                        byId('jwPhase').textContent = p.phase || '';
                        byId('jwElapsed').textContent = (p.elapsedSeconds || 0) + 's';
                        var fill = byId('jwFill');
                        if (p.totalItems > 0) {
                            fill.classList.remove('indet');
                            fill.style.width = Math.min(100, Math.round(p.processedItems / p.totalItems * 100)) + '%';
                            byId('jwCounts').textContent = p.processedItems + ' / ' + p.totalItems;
                        } else { fill.classList.add('indet'); byId('jwCounts').textContent = ''; }
                    } else if (w.sawRunning || w.ticks > 6) { self.finishWatcher(w.sawRunning); }
                });
            },
            // state: true (it ran), false (nothing ran) or 'unknown' (progress could not be read).
            finishWatcher: function (state) {
                var self = this, w = self.watcher, completed = state === true, unknown = state === 'unknown';
                if (w.timer) { clearInterval(w.timer); w.timer = null; }
                var fill = byId('jwFill'); fill.classList.remove('indet'); fill.style.width = unknown ? '0%' : '100%';
                var spin = byId('jwSpin'); spin.classList.add('done'); spin.innerHTML = completed ? '✓' : (unknown ? '!' : '');
                byId('jwTitle').textContent = completed ? 'Done' : (unknown ? 'Progress unavailable' : 'Nothing to sync');
                byId('jwPhase').textContent = completed ? 'Sync complete' : (unknown ? 'Couldn\'t read the sync\'s progress. It may still be running: check Recent activity in a minute.' : 'Everything was already up to date');
                self.setSyncBusy(false);
                setTimeout(function () { if (!root.isConnected) return; byId('jobWatcher').style.display = 'none'; self.loadOverview(); }, completed ? 2600 : (unknown ? 8000 : 1600));
            },

            /* ===== Account dialog ===== */
            feat: function (label, on) { return '<span class="ws-feat ' + (on ? 'on' : '') + '">' + label + '</span>'; },
            // rows: [label, checkbox id, checked, description]. The days box under "Only sync recently played" follows them.
            checksHtml: function (rows, dateOn, days) {
                return rows.map(function (r) {
                    return '<label><input type="checkbox" id="' + r[1] + '"' + (r[2] ? ' checked' : '') + ' /><span>' + r[0] + '<span class="desc">' + r[3] + '</span></span></label>';
                }).join('') +
                    '<div class="ws-row" id="dateDaysRow" style="display:' + (dateOn ? 'block' : 'none') + ';margin-top:4px;"><label class="ws-field" for="chkDateDays">Days to look back</label><input type="number" class="ws-input" id="chkDateDays" min="1" max="365" value="' + (days || 7) + '" /></div>';
            },
            // Stored ids for libraries this page listed are replaced by what is ticked; ids for libraries
            // it did not list (one this user cannot see, or a failed or partial list) are kept, so an
            // exclusion is never silently cleared. Ids of deleted libraries are kept too: they match nothing.
            mergeExcluded: function (stored, ticked) {
                var shown = (this.libraries || []).map(function (l) { return l.id; });
                return stored.filter(function (id) { return shown.indexOf(id) < 0; }).concat(ticked);
            },
            libsHtml: function (excluded) {
                var self = this;
                return (self.libraries || []).map(function (l) {
                    return '<label><input type="checkbox" class="mLibChk" data-id="' + self.esc(l.id) + '"' + (excluded.indexOf(l.id) >= 0 ? ' checked' : '') + ' /><span>' + self.esc(l.name) + '<span class="desc">' + ({ movies: 'Films', tvshows: 'TV', mixed: 'Mixed' }[l.collectionType] || 'Mixed') + '</span></span></label>';
                }).join('');
            },
            onSvcChange: function () {
                var svc = byId('mSvc').value;
                var watchBox = byId('chkWatch'), watchDesc = watchBox && watchBox.closest('label').querySelector('.desc');
                if (watchDesc) watchDesc.textContent = this.watchDesc(svc);
                byId('mUserLabel').textContent = svc === 'serializd' ? 'Email or username' : 'Letterboxd username';
                byId('mAuthBlobRow').style.display = svc === 'letterboxd' ? 'block' : 'none';
                byId('mUserAgentRow').style.display = svc === 'letterboxd' ? 'block' : 'none';
                var ratingsBox = byId('chkRatings');
                if (ratingsBox) ratingsBox.closest('label').style.display = svc === 'letterboxd' ? '' : 'none';
                var uname = byId('mUsername').value.trim();
                byId('mWatchName').placeholder = svc === 'serializd' ? 'Serializd Watchlist' : ('Letterboxd Watchlist' + (uname ? ' (' + uname + ')' : ''));
            },
            /* Saved secrets never come back from the server (only has* flags), so a secret field always
               starts empty: blank on save keeps the saved value, typing replaces it. */
            fillSecret: function (id, saved, keepText, emptyText) {
                var el = byId(id), clearRow = byId(id + 'ClearRow');
                el.value = '';
                el.placeholder = saved ? keepText : emptyText;
                byId(id + 'Saved').hidden = !saved;
                // A "Remove saved ..." checkbox, where the field has one, shows only when something is saved.
                if (clearRow) { clearRow.style.display = saved ? 'flex' : 'none'; byId(id + 'Clear').checked = false; }
            },
            // The dialog's fields; userId is the admin page's Jellyfin user picker (undefined on the user page).
            collectModal: function () {
                var g = byId, user = g('mUser');
                return { svc: g('mSvc').value, userId: user ? user.value : undefined,
                    username: g('mUsername').value.trim(), password: g('mPassword').value, cookies: g('mAuthBlob').value, clearCookies: g('mAuthBlobClear').checked,
                    userAgent: g('mUserAgent').value.trim(), watchName: g('mWatchName').value.trim(),
                    enabled: g('chkEnabled').checked, fav: g('chkFav').checked, ratings: g('chkRatings') ? g('chkRatings').checked : true, date: g('chkDate').checked, days: parseInt(g('chkDateDays').value, 10) || 7,
                    primary: g('chkPrimary').checked, watch: g('chkWatch').checked, seerr: g('chkSeerr').checked,
                    backfill: g('chkBackfill').checked, mirror: g('chkMirror').checked, skip: g('chkSkip').checked, stop: g('chkStop').checked, imp: g('chkImport').checked,
                    libs: Array.prototype.map.call(root.querySelectorAll('#mLibs .mLibChk:checked'), function (x) { return x.getAttribute('data-id'); }) };
            },
            // The checks both pages make before saving an account; false when the form was refused.
            validAccountForm: function (m) {
                if (!m.username) { byId('mStatus').innerHTML = '<span class="ws-red">Username/email is required.</span>'; return false; }
                if (m.svc === 'letterboxd' && m.username.indexOf('@') >= 0) { byId('mStatus').innerHTML = '<span class="ws-red">Letterboxd no longer accepts an email address to sign in. Use your Letterboxd username, the name in letterboxd.com/&lt;username&gt;/.</span>'; return false; }
                return true;
            },
            setModalBusy: function (busy) {
                this.modalBusy = busy;
                ['mSaveBtn', 'mDeleteBtn', 'mConfirmRemoveBtn', 'mConfirmKeepBtn'].forEach(function (id) { byId(id).disabled = busy; });
            },
            confirmRemove: function (show) {
                byId('mConfirmRow').style.display = show ? 'flex' : 'none';
                byId('mDeleteBtn').style.display = show ? 'none' : '';
                byId('mStatus').textContent = '';
            },
            verifyMessage: function (ok, d) {
                var esc = function (s) { return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); };
                d = d || {};
                if (!ok && d.retryAfterSeconds) return '<span class="ws-red">' + esc(d.error) + '</span>';
                if (ok && d.via === 'api') return '<span class="ws-green">Login OK (official API).</span>';
                if (ok) return '<span class="ws-green">Login OK via the website login.</span> <span class="ws-muted">The official API refused it: ' + esc(d.apiError) + '</span>';
                var parts = [];
                if (d.error && d.error !== 'Login failed.') parts.push(esc(d.error));
                if (d.apiError) parts.push('Official API: ' + esc(d.apiError));
                if (d.websiteError) parts.push('Website: ' + esc(d.websiteError));
                return '<span class="ws-red">Login failed.' + (parts.length ? ' ' + parts.join(' · ') : '') + '</span>';
            },

            /* Modals are native dialogs: showModal() keeps focus inside, makes the page behind inert
               and closes on Escape. Focus starts on the first field and goes back on close to
               whatever opened the dialog. */
            openModal: function (id) {
                var ov = byId(id);
                if (ov.hasAttribute('open')) return;
                this.returnFocus[id] = document.activeElement;
                if (typeof ov.showModal === 'function') ov.showModal(); else ov.setAttribute('open', '');
                var first = Array.prototype.filter.call(ov.querySelectorAll('select, input, textarea'), function (x) { return !x.disabled && x.offsetParent !== null; })[0];
                if (first) first.focus();
            },
            // This page's own dialog, found through its root: Jellyfin may already have taken the page
            // out of the document when it reports the page hidden.
            closeModal: function (id) {
                var ov = byId(id);
                if (!ov.hasAttribute('open')) return;
                if (typeof ov.close === 'function') ov.close(); else { ov.removeAttribute('open'); this.restoreFocus(id); }
            },
            restoreFocus: function (id) {
                var to = this.returnFocus[id];
                this.returnFocus[id] = null;
                if (to && to.isConnected && typeof to.focus === 'function') to.focus();
            },

            /* ===== Review dialog ===== */
            /* Paint the star widget to a 0.5-step value (null/0 clears). Single writer for reviewRating
               and ratingLabel so click, reset, and pre-fill can't disagree. */
            paintStars: function (val) {
                byId('reviewRating').value = val || '';
                byId('ratingLabel').textContent = val ? val + ' / 5' : 'No rating';
                byId('starRating').setAttribute('aria-valuenow', val || 0);
                byId('starRating').setAttribute('aria-valuetext', val ? val + ' out of 5 stars' : 'No rating');
                root.querySelectorAll('#starRating .ws-star').forEach(function (s) {
                    var sv = parseFloat(s.getAttribute('data-val'));
                    s.classList.toggle('filled', !!val && sv <= val);
                    s.classList.toggle('half', !!val && sv - 0.5 === val);
                });
            },
            openReview: function (svc, tmdbId, title, slug, season, episode) {
                this.reviewSvc = svc; this.reviewTmdb = tmdbId; this.reviewSlug = slug || '';
                this.reviewTitleText = title || '';
                this.reviewSeason = season ? parseInt(season, 10) : null;
                this.reviewEpisode = episode ? parseInt(episode, 10) : null;
                var scope = (this.reviewSeason && this.reviewEpisode) ? (' · S' + this.reviewSeason + 'E' + this.reviewEpisode) : '';
                byId('reviewTitle').textContent = 'Review: ' + title + scope;
                byId('reviewText').value = ''; byId('reviewSpoilers').checked = false;
                byId('reviewRewatch').checked = false;
                byId('rewatchDateRow').style.display = 'none';
                byId('reviewDate').value = this.localDate();
                this.paintStars(null);
                byId('reviewStatus').textContent = '';
                byId('reviewSubmit').disabled = false; byId('reviewCancel').disabled = false;
                var isLetterboxd = svc !== 'serializd';
                byId('reviewRewatchRow').style.display = isLetterboxd ? '' : 'none';
                byId('reviewAccountRow').style.display = 'none';
                if (isLetterboxd) this.populateReviewAccountDropdown();
                this.openModal('reviewModal');
                this.prefillRating(svc, tmdbId);
            },
            /* Pre-fill the stars from the rating already stored in Jellyfin. Fail-soft by design: any
               error or null response just leaves "No rating", so the modal never blocks on this lookup.
               The sequence token discards a late response if another item's modal was opened meanwhile. */
            prefillRating: function (svc, tmdbId) {
                this.reviewOpenSeq = (this.reviewOpenSeq || 0) + 1;
                var seq = this.reviewOpenSeq, self = this;
                if (!tmdbId) return;
                var q = 'Jellyfin.Plugin.LetterboxdSync/ItemRating?tmdbId=' + tmdbId;
                if (svc === 'serializd') {
                    q += '&isShow=true';
                    if (this.reviewSeason && this.reviewEpisode) q += '&seasonNumber=' + this.reviewSeason + '&episodeNumber=' + this.reviewEpisode;
                }
                this.getJson(q).then(function (d) {
                    if (seq === self.reviewOpenSeq && d && d.stars) self.paintStars(d.stars);
                }).catch(function () { });
            },
            submitReview: function () {
                var self = this, text = byId('reviewText').value.trim();
                var rv = byId('reviewRating').value, rating = rv ? parseFloat(rv) : null;
                var isRewatch = self.reviewSvc !== 'serializd' && byId('reviewRewatch').checked;
                if (!text && !rating && !isRewatch) { byId('reviewStatus').innerHTML = '<span class="ws-red">Write a review, set a rating, or mark as rewatch.</span>'; return; }
                var postBtn = byId('reviewSubmit'), cancelBtn = byId('reviewCancel'), st = byId('reviewStatus');
                if (postBtn.disabled) return;
                st.textContent = 'Posting…';
                // Both stay disabled while posting: a second press would post a second diary entry.
                postBtn.disabled = true; cancelBtn.disabled = true;
                var url, body;
                if (self.reviewSvc === 'serializd') { url = 'Jellyfin.Plugin.LetterboxdSync/Serializd/Review'; body = { tmdbId: self.reviewTmdb, rating: rating ? Math.round(rating * 2) : null, reviewText: text || null, containsSpoilers: byId('reviewSpoilers').checked, seasonNumber: self.reviewSeason || null, episodeNumber: self.reviewEpisode || null, title: self.reviewTitleText || null }; }
                else {
                    var date = byId('reviewDate').value;
                    var lbUser = (byId('reviewAccount').value || '').trim();
                    url = 'Jellyfin.Plugin.LetterboxdSync/Review';
                    body = { filmSlug: self.reviewSlug, tmdbId: self.reviewTmdb || null, reviewText: text || null, containsSpoilers: byId('reviewSpoilers').checked, isRewatch: isRewatch, date: isRewatch && date ? date : null, rating: rating, letterboxdUsername: lbUser || null, title: self.reviewTitleText || null };
                }
                self.post(url, body).then(function (r) {
                    return r.json().then(function (d) { return { r: r, d: d || {} }; }, function () { return { r: r, d: {} }; });
                }).then(function (x) {
                    cancelBtn.disabled = false;
                    st.innerHTML = self.reviewResult(x.r.ok, x.d, x.r.status);
                    var failed = (x.d.accounts || []).filter(function (a) { return a.success === false; });
                    // Fully posted: close. Partly posted: stay open with Post disabled, since posting
                    // again would duplicate it on the accounts that took it. Not posted: allow a retry.
                    // A note from the server stays up long enough to read.
                    if (x.r.ok && !failed.length) setTimeout(function () { self.closeModal('reviewModal'); }, st.querySelector('.ws-review-note') ? 6000 : 1200);
                    else if (!x.r.ok) postBtn.disabled = false;
                }, function () { postBtn.disabled = false; cancelBtn.disabled = false; st.innerHTML = self.unreachable(); });
            },
            // The result line: what landed, then each account it did not land on with the server's reason,
            // then a line for each account the server said more about.
            reviewResult: function (ok, d, status) {
                var self = this, failed = (d.accounts || []).filter(function (a) { return a.success === false; });
                var perAccount = failed.map(function (a) { return self.esc(a.letterboxdUsername) + ': ' + self.esc(a.error || 'failed'); }).join('; ');
                var line;
                if (ok) {
                    var done = '<span class="ws-green">' + (d.ratedOnly ? 'Rating saved to Letterboxd.' : 'Review posted!') + '</span>';
                    line = failed.length ? done + ' <span class="ws-red">Not posted to ' + perAccount + '</span>' : done;
                } else if (failed.length > 1) line = '<span class="ws-red">Failed to post. ' + perAccount + '</span>';
                else line = '<span class="ws-red">Failed to post: ' + self.esc(d.error || ('HTTP ' + status)) + '</span>';
                return line + self.reviewAccountNotes(d.accounts || []);
            },
            // Per account, when the server says so: the review went onto the diary entry already there
            // (addedToEntry) and any note it sent, as plain text. Nothing for accounts without either.
            reviewAccountNotes: function (accounts) {
                var self = this;
                return accounts.filter(function (a) { return a.addedToEntry === true || (a.note && String(a.note).trim()); }).map(function (a) {
                    var what = a.addedToEntry === true ? 'Added to the existing diary entry.' : '';
                    return '<span class="ws-review-note" style="display:block;margin-top:4px;"><b>' + self.esc(a.letterboxdUsername) + ':</b> ' + what +
                        (a.note ? '<span class="ws-muted" style="display:block;">' + self.esc(a.note) + '</span>' : '') + '</span>';
                }).join('');
            }
        };
    };
})();
