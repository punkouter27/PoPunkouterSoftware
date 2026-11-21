/**
 * Trigger a browser file download with the given text content.
 * @param {string} filename  - suggested filename (e.g. "azure-report-2026-05-14.json")
 * @param {string} content   - file content (JSON string)
 * @param {string} mimeType  - MIME type (default: application/json)
 */
window.downloadTextFile = function (filename, content, mimeType) {
    mimeType = mimeType || 'application/json';
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

/* ═════════════════════════════════════════════════════════════════════════════
   Topbar behaviour.

   MainLayout is statically server-rendered (`<Routes />` carries no render mode;
   only the page components are InteractiveWebAssembly). Its OnAfterRenderAsync
   therefore never runs, so the previous `JS.InvokeVoidAsync("initNavbarScroll")`
   was never invoked and the floating navbar was inert. It now self-initialises
   from this file, which the host page always loads. (A mobile hamburger drawer
   lived here too until 2026-10-19; a phone header is now just the brand.)
   ═════════════════════════════════════════════════════════════════════════════ */

/**
 * Track page scroll state to float/hide navbar navigation bar.
 *
 * The listener is registered passive so the compositor never waits on it, and the
 * class writes are coalesced into one rAF callback and only issued when the state
 * actually flips. The header carries `backdrop-filter` plus a `box-shadow`
 * transition, so an unconditional classList write per scroll event forced a blur
 * repaint on every frame.
 */
window.initNavbarScroll = function () {
    const header = document.querySelector('.rz-header');
    if (!header) return;
    if (window.navbarScrollHeader === header) return;

    if (window.navbarScrollHandler) {
        window.removeEventListener('scroll', window.navbarScrollHandler);
    }

    window.navbarScrollHeader = header;
    let lastScrollY = window.scrollY;
    let hidden = false;
    let scrolled = false;
    let queued = false;

    const apply = () => {
        queued = false;
        if (window.navbarScrollHeader !== header) return;

        const currentScrollY = window.scrollY;
        const nextHidden = currentScrollY > lastScrollY && currentScrollY > 80;
        const nextScrolled = currentScrollY > 20;

        if (nextHidden !== hidden) {
            header.classList.toggle('nav-hidden', nextHidden);
            hidden = nextHidden;
        }
        if (nextScrolled !== scrolled) {
            header.classList.toggle('nav-scrolled', nextScrolled);
            scrolled = nextScrolled;
        }
        lastScrollY = currentScrollY;
    };

    window.navbarScrollHandler = () => {
        if (queued) return;
        queued = true;
        requestAnimationFrame(apply);
    };
    window.addEventListener('scroll', window.navbarScrollHandler, { passive: true });
};

/**
 * One-tap copy clipboard helper. Returns a promise resolving to true/false so the
 * caller (C#) can show honest feedback — the previous version swallowed failures
 * (the UI said "Copied" with nothing on the clipboard) and threw synchronously on
 * non-secure origins where navigator.clipboard is undefined, which crashed the page
 * through the Blazor error boundary. The single toast is now owned by the C# side.
 */
window.copyToClipboard = function (text) {
    if (!navigator.clipboard) {
        console.error('Clipboard API unavailable (non-secure context?)');
        return Promise.resolve(false);
    }
    return navigator.clipboard.writeText(text)
        .then(() => true)
        .catch(err => {
            console.error('Clipboard copy failed: ', err);
            return false;
        });
};

/**
 * Scroll the first element matching `selector` to the middle of its scroll containers — every
 * ancestor, so it also works inside the uptime grid's own sideways scroller. Used by
 * the ?focus= deep link that outage notifications carry.
 */
window.appRevealFocus = function (selector) {
    const el = document.querySelector(selector);
    if (!el) return;
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: reduce ? 'auto' : 'smooth' });
};

/**
 * Force a cache-busting reload when the server BuildId no longer matches the loaded
 * WASM bundle. Replaces a JS `eval` string that was assembled in C#.
 */
window.appHardReload = function () {
    if (window.location.search.indexOf('reload=1') === -1) {
        window.location.replace(window.location.pathname + '?reload=1&t=' + Date.now());
    } else {
        window.location.reload();
    }
};

/* ═════════════════════════════════════════════════════════════════════════════
   Radzen markup repairs — accessibility only, no visual effect.

   Two defects in what Radzen 10.4.5 renders itself. Neither can be fixed from this
   repository's markup, because in both cases the library emits the wrong thing:

   1. ICON GLYPHS ARE REAL TEXT. Radzen renders an icon as
      `<i class="notranslate rzi">refresh</i>` — the ligature NAME, in the text flow,
      with no aria-hidden. So the accessible name of every Radzen icon button is the
      glyph name followed by its label, measured on the running app as
      "refresh Rescan Azure" and "graphic_eq Listen", and the two /users disclosures
      announce as "timelineSign-ins over time" and "historyMost recent sign-ins".
      Sixteen on "/", four on "/azure", two on "/users".

      The hand-written `<span class="material-symbols-outlined" aria-hidden="true">`
      glyphs in MainLayout were already correct, which is exactly why this went
      unnoticed for so long: it only affects the path that goes through Radzen.

   2. `aria-disabled` IS EMITTED UNEVALUATED. Chart legend items ship
      `aria-disabled="False.ToString().ToLowerInvariant()"` — a literal, from an
      expression that never ran. The value is neither "true" nor "false", so nothing
      can tell whether the item is usable — and a legend item IS a control
      (`role="button"`, `tabindex="0"`, clicking it hides that series).

   WHY A MutationObserver AND NOT 22 CALL SITES. aria-hidden cannot be set from CSS,
   and RadzenButton gives no way to reach the <i> it renders inside itself. Doing this
   per call site — an aria-label on every button, an aria-hidden wrapper span around
   every standalone icon — is 22 places to remember, in markup whose whole point is
   that adding a Radzen control works with no interop hook. This is the same reasoning
   as the delegated click listeners: survive any amount of DOM replacement, and ask
   nothing of the component rendering the markup.

   Cost is bounded: one observer on documentElement, one attribute write per icon, and
   a no-op for any added node carrying neither. Attribute writes do not produce
   childList records, so this cannot feed itself.
   ═════════════════════════════════════════════════════════════════════════════ */
window.appRepairRadzenA11y = (function () {
    'use strict';

    function hideIcons(node) {
        if (node.classList && node.classList.contains('rzi')) {
            node.setAttribute('aria-hidden', 'true');
        }
        if (!node.querySelectorAll) return;
        const icons = node.querySelectorAll('.rzi');
        for (let i = 0; i < icons.length; i++) {
            icons[i].setAttribute('aria-hidden', 'true');
        }
    }

    function fixAriaDisabled(node) {
        if (!node.querySelectorAll) return;
        const candidates = [];
        if (node.hasAttribute && node.hasAttribute('aria-disabled')) candidates.push(node);
        candidates.push.apply(candidates, node.querySelectorAll('[aria-disabled]'));
        for (let i = 0; i < candidates.length; i++) {
            const value = candidates[i].getAttribute('aria-disabled');
            // Only the malformed value is removed. A real "true"/"false" is Radzen
            // (or this app) working correctly and is left exactly as found.
            if (value !== 'true' && value !== 'false') {
                candidates[i].removeAttribute('aria-disabled');
            }
        }
    }

    function repair(node) {
        if (!node || node.nodeType !== 1) return;
        hideIcons(node);
        fixAriaDisabled(node);
    }

    function start() {
        if (!document.body) return;
        // The static SSR payload is already parsed, so sweep it once — the observer
        // below only sees what Blazor adds afterwards.
        repair(document.body);
        new MutationObserver(function (records) {
            for (const record of records) {
                for (const added of record.addedNodes) repair(added);
            }
        }).observe(document.documentElement, { childList: true, subtree: true });
    }

    return { start: start, repair: repair };
})();

/* Self-initialise the topbar. The header is present in the static SSR payload, so
   it exists by the time this script executes; the DOMContentLoaded guard covers the
   case where the script is ever moved into <head>. */
(function bootstrapTopbar() {
    const start = () => {
        window.initNavbarScroll();
        window.appRepairRadzenA11y.start();
    };
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();

/* ═════════════════════════════════════════════════════════════════════════════
   Scan lifecycle → `app:refresh` on document, which js/gpu-backdrop.js draws (sweep while
   scanning, shockwave at the end). Called by AzureDashboard. It lived in js/audio-kit.js,
   ahead of that file's sound gate, until sound was fixed Off and the kit deleted (2026-11-17).
   ═════════════════════════════════════════════════════════════════════════════ */
(function () {
    function announce(detail) {
        try { document.dispatchEvent(new CustomEvent('app:refresh', { detail: detail })); }
        catch (e) { /* the backdrop is decoration; never break the scan over it */ }
    }

    window.appScan = {
        start: function () { announce({ phase: 'start' }); },
        progress: function (percent) { announce({ phase: 'progress', percent: percent }); },
        indeterminate: function () { announce({ phase: 'indeterminate' }); },
        end: function (outcome) { announce({ phase: 'end', outcome: outcome }); }
    };
})();
