/**
 * ui-physics — physics applied to the DOM itself, not to a canvas.
 *
 * One effect, wired by observation so no Blazor component needs an interop call, an
 * OnAfterRender hook or a disposal path — the same contract audio-kit.js follows for
 * `[data-sfx]`:
 *
 *   ODOMETER  a KPI value (`.app-kpis strong`) counts up to its number on a damped spring
 *             the first time it appears. Only the text node's value changes; if Blazor
 *             writes the node mid-count the count stops and Blazor's value stands.
 *
 * (A TILT effect leaned catalogue cards toward the mouse; it went with the cards when the
 * home page became the stack matrix, 2026-11-03 — a table row that tilts reads as a glitch.)
 *
 * Bars springing into place (MetricBars) are CSS — a `linear()` spring curve on a keyframe
 * in modern-ui.css — because a native animation needs no script at all.
 *
 * Every per-frame effect is a named motionKit subscriber that unsubscribes when it settles,
 * so an idle page holds no subscription and the governor's gate stays closed. Reduced motion
 * and `motionKit.minimal` skip it; the page is identical without it.
 */
(function () {
    'use strict';

    var reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
    function off() { return reduce.matches || !window.motionKit || window.motionKit.minimal; }

    // ── Odometer ────────────────────────────────────────────────────────────────────────
    var odos = [], odoSub = 0;

    function format(v, o) {
        var s = v.toFixed(o.dec);
        if (o.commas) s = s.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
        return o.pre + s + o.post;
    }

    function tickOdos(dt) {
        for (var i = 0; i < odos.length; i++) {
            var o = odos[i];
            // Blazor rewrote the node: its value is the truth, stop counting.
            if (o.node.nodeValue !== o.last || !o.node.isConnected) { odos.splice(i--, 1); continue; }
            o.t += dt;
            var p = Math.min(1, o.t / 0.9);
            var e = 1 - Math.exp(-6 * p) * Math.cos(9 * p);
            o.last = o.node.nodeValue = p >= 1 ? o.final : format(o.target * e, o);
            if (p >= 1) odos.splice(i--, 1);
        }
        if (!odos.length && odoSub) { window.motionKit.unsubscribe(odoSub); odoSub = 0; }
    }

    function startOdo(el) {
        var node = el.firstChild;
        if (!node || node.nodeType !== 3 || node.nextSibling) return;
        var m = /^(\D*?)(-?[\d,]*\.?\d+)(.*)$/.exec(node.nodeValue);
        if (!m) return;
        var target = parseFloat(m[2].replace(/,/g, ''));
        if (!isFinite(target) || target === 0) return;
        var r = el.getBoundingClientRect();
        if (r.bottom < 0 || r.top > window.innerHeight) return;   // nobody would see it count
        odos.push({
            node: node, pre: m[1], post: m[3], target: target, final: node.nodeValue,
            dec: (m[2].split('.')[1] || '').length, commas: m[2].indexOf(',') >= 0,
            t: 0, last: node.nodeValue
        });
        if (!odoSub) odoSub = window.motionKit.subscribe('ui-odometer', tickOdos, { fps: 60 });
    }

    function scanOdos() {
        if (off()) return;
        var els = document.querySelectorAll('.app-kpis strong:not([data-odo])');
        for (var i = 0; i < els.length; i++) { els[i].setAttribute('data-odo', ''); startOdo(els[i]); }
    }

    // ── Wiring ──────────────────────────────────────────────────────────────────────────
    // One observer for the odometer: KPI tiles arrive whenever the WASM page finishes its
    // fetch, which no navigation event announces. The callback is a single selector query.
    function boot() {
        scanOdos();
        new MutationObserver(scanOdos).observe(document.body, { childList: true, subtree: true });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true });
    else boot();
})();
