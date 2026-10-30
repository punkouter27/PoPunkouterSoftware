/**
 * Global Command Palette (Cmd+K / Ctrl+K)
 * Provides instantaneous keyboard-driven navigation and actions across the portfolio.
 */
(function () {
    'use strict';

    var overlay = null;
    var input = null;
    var list = null;
    var selectedIndex = 0;
    var isOpen = false;

    var COMMANDS = [
        { id: 'nav-apps', title: 'Go to Apps Portfolio', icon: 'home', category: 'Navigation', action: function () { navigate('/'); } },
        { id: 'nav-azure', title: 'Go to Azure Ops Dashboard', icon: 'monitor_heart', category: 'Navigation', action: function () { navigate('/azure'); } },
        { id: 'nav-users', title: 'Go to User Sign-Ins', icon: 'group', category: 'Navigation', action: function () { navigate('/users'); } },
        // Theme, density, sound and effects live on /settings and nowhere else: a control that
        // exists twice is two places to keep in step, and the deleted 2026-10 page proved it.
        { id: 'nav-settings', title: 'Go to Settings (theme, density, sound, effects)', icon: 'settings', category: 'Navigation', action: function () { navigate('/settings'); } },
        { id: 'act-reload', title: 'Hard Reload Application', icon: 'refresh', category: 'System', action: function () { window.location.reload(); } }
    ];

    // Per-app launcher rows, built from /api/portfolio the first time the palette opens.
    // Fetched lazily and once per page: the palette is a convenience, and a portfolio request
    // on every page load for a palette most visits never open would be a tax on all of them.
    var appCommands = [];
    var appsRequested = false;

    function loadApps() {
        if (appsRequested) return;
        appsRequested = true;
        Promise.all([
            fetch('/api/portfolio').then(function (r) { return r.ok ? r.json() : { apps: [] }; }),
            fetch('/api/config').then(function (r) { return r.ok ? r.json() : {}; })
        ]).then(function (res) {
            var cmds = [];
            (res[0].apps || []).forEach(function (app) {
                if (app.url) {
                    cmds.push({ id: 'open-' + app.id, title: 'Open ' + app.name, icon: 'open_in_new', category: 'Apps', app: true,
                        action: function () { window.open(app.url, '_blank', 'noopener'); } });
                    cmds.push({ id: 'copy-' + app.id, title: 'Copy ' + app.name + ' link', icon: 'link', category: 'Apps', app: true,
                        action: function () { if (navigator.clipboard) navigator.clipboard.writeText(app.url); } });
                }
                cmds.push({ id: 'focus-' + app.id, title: 'Show ' + app.name + ' uptime', icon: 'my_location', category: 'Apps', app: true,
                    action: function () { navigate('/azure?focus=' + encodeURIComponent(app.name)); } });
                // Repo and Actions straight from the catalog's githubRepo — Details rows, listed
                // only under /settings → Details → Full (see getFilteredCommands). No Azure portal row:
                // a portal deep link carries the subscription id, which every anonymous
                // endpoint here masks (see /api/diag/report).
                if (app.githubRepo) {
                    var repo = 'https://github.com/' + app.githubRepo;
                    cmds.push({ id: 'repo-' + app.id, title: 'Open ' + app.name + ' repo', icon: 'data_object', category: 'Apps', app: true, detail: true,
                        action: function () { window.open(repo, '_blank', 'noopener'); } });
                    cmds.push({ id: 'actions-' + app.id, title: 'Open ' + app.name + ' GitHub Actions', icon: 'history', category: 'Apps', app: true, detail: true,
                        action: function () { window.open(repo + '/actions', '_blank', 'noopener'); } });
                }
            });
            // Same gate as the Rescan and Sync buttons: shown only where the server will accept it.
            if (res[1].managementActionsEnabled) {
                cmds.push({ id: 'act-rescan', title: 'Rescan Azure now', icon: 'sync', category: 'Actions',
                    action: function () {
                        fetch('/api/diag/refresh', { method: 'POST' }).finally(function () { navigate('/azure'); });
                    } });
            }
            appCommands = cmds;
            if (isOpen) renderList();
        }).catch(function () { /* offline or API down: the static commands still work */ });
    }

    /** Tell the visual layers (gpu-backdrop.js) what the palette did. */
    function fx(phase) {
        var r = overlay && overlay.firstChild ? overlay.firstChild.getBoundingClientRect() : null;
        var u = r && r.width ? (r.left + r.width / 2) / window.innerWidth : 0.5;
        var v = r && r.width ? (r.top + r.height / 2) / window.innerHeight : 0.35;
        try { document.dispatchEvent(new CustomEvent('app:palette', { detail: { phase: phase, u: u, v: v } })); }
        catch (e) { /* visuals only */ }
    }

    /**
     * Enhanced navigation when Blazor is up, like any other in-app link — wrapped in a View
     * Transition so the header brand morphs into the new page's h1. The old snapshot names
     * the brand (the only header link there is), the new one names the heading; the browser
     * animates between the two boxes. Skipped under reduced motion or without the API: a plain navigation, same URL.
     */
    function navigate(url) {
        fx('jump');
        // For the jump's ~second the backdrop canvas screens over the page, so the streaks
        // are seen across the content and not only in the gutters between cards. Full
        // effects only (/settings): without the jump there are no streaks to screen.
        var root = document.documentElement;
        if (window.appFx && window.appFx.full) {
            root.classList.add('is-palette-jump');
            setTimeout(function () { root.classList.remove('is-palette-jump'); }, 900);
        }
        if (!window.Blazor || !window.Blazor.navigateTo) { window.location.href = url; return; }
        var go = function () { window.Blazor.navigateTo(url); };
        if (!document.startViewTransition || window.matchMedia('(prefers-reduced-motion: reduce)').matches) { go(); return; }

        var link = document.querySelector('.app-topbar-brand');
        if (link) link.style.viewTransitionName = 'app-jump';
        var heading = null;
        var vt = document.startViewTransition(function () {
            return new Promise(function (resolve) {
                var done = function () {
                    if (!resolve) return;
                    window.Blazor.removeEventListener && window.Blazor.removeEventListener('enhancedload', done);
                    if (link) link.style.viewTransitionName = '';
                    heading = document.querySelector('main h1, h1');
                    if (heading) heading.style.viewTransitionName = 'app-jump';
                    var r = resolve; resolve = null; r();
                };
                if (window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', done);
                go();
                setTimeout(done, 1200);   // an in-app page change that never fires enhancedload
            });
        });
        vt.finished.finally(function () {
            if (heading) heading.style.viewTransitionName = '';
            if (link) link.style.viewTransitionName = '';
        });
    }

    function createPalette() {
        if (overlay) return;

        overlay = document.createElement('div');
        overlay.className = 'app-palette-backdrop';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Command Palette');

        var modal = document.createElement('div');
        modal.className = 'app-palette-modal';

        var header = document.createElement('div');
        header.className = 'app-palette-header';

        var searchIcon = document.createElement('span');
        searchIcon.className = 'material-symbols-outlined app-palette-search-icon';
        searchIcon.textContent = 'search';
        searchIcon.setAttribute('aria-hidden', 'true');

        input = document.createElement('input');
        input.type = 'text';
        input.className = 'app-palette-input';
        input.placeholder = 'Type a command or an app name (e.g. "azure", "theme", "meme")...';
        input.setAttribute('aria-autocomplete', 'list');

        var kbd = document.createElement('kbd');
        kbd.className = 'app-palette-kbd';
        kbd.textContent = 'ESC';

        header.appendChild(searchIcon);
        header.appendChild(input);
        header.appendChild(kbd);

        list = document.createElement('div');
        list.className = 'app-palette-list';
        list.setAttribute('role', 'listbox');

        modal.appendChild(header);
        modal.appendChild(list);
        overlay.appendChild(modal);

        document.body.appendChild(overlay);

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) close();
        });

        input.addEventListener('input', function () {
            selectedIndex = 0;
            renderList();
        });

        input.addEventListener('keydown', function (e) {
            var items = getFilteredCommands();
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                selectedIndex = (selectedIndex + 1) % Math.max(1, items.length);
                renderList();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                selectedIndex = (selectedIndex - 1 + items.length) % Math.max(1, items.length);
                renderList();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (items[selectedIndex]) {
                    close();
                    items[selectedIndex].action();
                }
            } else if (e.key === 'Escape') {
                close();
            }
        });
    }

    /**
     * Fuzzy subsequence score: every query character must appear in order. Consecutive hits
     * and hits at a word start score extra, so "pmv" finds "Open PoMemeVideo" and "azure"
     * still ranks "Go to Azure Ops Dashboard" first. -1 means no match. Hand-written rather
     * than Fuse.js: twenty lines, and the CSP's script-src 'self' rules out a CDN anyway.
     */
    function fuzzyScore(query, text) {
        var t = text.toLowerCase(), score = 0, ti = 0, run = 0;
        for (var qi = 0; qi < query.length; qi++) {
            var c = query.charAt(qi);
            if (c === ' ') continue;
            var found = t.indexOf(c, ti);
            if (found === -1) return -1;
            run = found === ti ? run + 1 : 0;
            var wordStart = found === 0 || /[\s\-_.]/.test(t.charAt(found - 1)) || text.charAt(found) !== t.charAt(found);
            score += 1 + run * 2 + (wordStart ? 3 : 0);
            ti = found + 1;
        }
        return score - t.length * 0.01;
    }

    function getFilteredCommands() {
        var query = (input ? input.value : '').toLowerCase().trim();
        // With no query the list stays short — the per-app rows only appear once you type.
        if (!query) return COMMANDS.concat(appCommands.filter(function (c) { return !c.app; }));
        // Details rows (repo / Actions) only when the visitor chose /settings → Details → Full.
        var full = document.documentElement.getAttribute('data-app-details') === 'full';
        var ranked = COMMANDS.concat(appCommands.filter(function (c) { return full || !c.detail; }))
            .map(function (cmd) { return { cmd: cmd, score: Math.max(fuzzyScore(query, cmd.title), fuzzyScore(query, cmd.category) - 5) }; })
            .filter(function (x) { return x.score >= 0; })
            .sort(function (a, b) { return b.score - a.score; });
        // A subsequence matches almost anything once the letters are spread out ("pomeme"
        // finds "Show PoMode uptime"), so rows far below the best match are noise, not hits.
        var floor = ranked.length ? ranked[0].score * 0.6 : 0;
        return ranked
            .filter(function (x) { return x.score >= floor; })
            .slice(0, 30)
            .map(function (x) { return x.cmd; });
    }

    function renderList() {
        if (!list) return;
        list.innerHTML = '';
        var items = getFilteredCommands();

        if (items.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'app-palette-empty';
            empty.textContent = 'No commands match your query.';
            list.appendChild(empty);
            return;
        }

        items.forEach(function (cmd, idx) {
            var el = document.createElement('div');
            el.className = 'app-palette-item' + (idx === selectedIndex ? ' is-active' : '');
            el.setAttribute('role', 'option');
            el.setAttribute('aria-selected', idx === selectedIndex ? 'true' : 'false');

            var icon = document.createElement('span');
            icon.className = 'material-symbols-outlined app-palette-item-icon';
            icon.textContent = cmd.icon;
            icon.setAttribute('aria-hidden', 'true');

            var label = document.createElement('span');
            label.className = 'app-palette-item-title';
            label.textContent = cmd.title;

            var category = document.createElement('span');
            category.className = 'app-palette-item-category';
            category.textContent = cmd.category;

            el.appendChild(icon);
            el.appendChild(label);
            el.appendChild(category);

            el.addEventListener('click', function () {
                close();
                cmd.action();
            });

            el.addEventListener('mouseenter', function () {
                selectedIndex = idx;
                renderList();
            });

            list.appendChild(el);
        });

        var activeEl = list.children[selectedIndex];
        if (activeEl && activeEl.scrollIntoViewIfNeeded) {
            activeEl.scrollIntoViewIfNeeded(false);
        }
    }

    function open() {
        createPalette();
        loadApps();
        isOpen = true;
        overlay.classList.add('is-open');
        input.value = '';
        selectedIndex = 0;
        renderList();
        setTimeout(function () { input.focus(); }, 50);
        if (window.audioKit) window.audioKit.play('open');
        fx('open');
    }

    function close() {
        if (!isOpen) return;
        isOpen = false;
        if (overlay) overlay.classList.remove('is-open');
        if (window.audioKit) window.audioKit.play('close');
        fx('close');
    }

    document.addEventListener('keydown', function (e) {
        if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            if (isOpen) close(); else open();
        } else if (e.key === 'Escape' && isOpen) {
            close();
        }
    });
})();
