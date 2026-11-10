/**
 * GPU backdrop — the single compositing layer behind the whole app, and the capability
 * ladder that picks how to draw it.
 *
 * This file is the orchestrator only. Pixels belong to the renderer:
 *   js/gfx-webgl.js    WebGL2 (particles + dual-Kawase glass) with a WebGL1 field fallback
 *
 *      WebGL2  →  WebGL1  →  the CSS grid in modern-ui.css
 *
 * Each rung degrades to the next on any failure — missing WebGL2, a shader that will not
 * compile, a lost context. The bottom rung is not a rung at all: if nothing initialises,
 * `data-gpu-backdrop` is never set on <html> and the static CSS grid backdrop stays
 * visible, exactly as before this layer existed.
 *
 * There was a WebGPU rung on top (js/gfx-webgpu.js — 524 lines, compute-advected
 * particles, lazily fetched behind `navigator.gpu`). It was removed on 2026-09-05: it was
 * a second complete renderer, with its own shader language, for a decorative layer whose
 * WebGL2 rung draws the same backdrop at the same measured cost (~3ms dispatch on a
 * desktop, per the governor's own budget accounting). Two implementations of one
 * decoration is the most expensive kind of code to keep honest — a decorative GPU layer
 * has no failure mode that surfaces on its own, so each renderer has to be verified by
 * looking at pixels, and there were two of them. Adding it back also means re-adding the
 * canvas-swap it forced: `getContext` is sticky per element, so a canvas offered to WebGPU
 * can never yield a WebGL context afterwards, and the only way back down the ladder was to
 * clone and replace the node mid-init.
 *
 * ── What this file owns ─────────────────────────────────────────────────────────────────
 *   • Tier selection and fallback.
 *   • The accent colours, read from CSS custom properties and re-sampled on theme flip, so
 *     the backdrop can never drift from the palette.
 *   • The glass rect set (#6): the on-screen geometry of every `[data-glass]` surface,
 *     collected on scroll/resize and handed to the renderer, which blurs and refracts the
 *     backdrop beneath them. Collected at most once per two frames and skipped entirely
 *     when the signature has not changed — `getBoundingClientRect` is a forced layout, and
 *     doing it per element per frame is exactly the cost this layer exists to avoid.
 *   • The audio coupling (#4): one float per frame, PULLED from js/audio-kit.js. Pull, not
 *     push, so nothing is computed on frames the governor never serves, and the graphics
 *     have no reference to the audio graph.
 *   • The event effects — which moment triggers which renderer effect, and where on screen:
 *       scan start/progress  → radar sweep       (`app:refresh` DOM event, see below)
 *       scan end             → shockwave from [data-fx-origin="refresh"]; red + tear on failure
 *       scan end 'recovered' → particle bloom from [data-fx-origin="health"]
 *       [data-fx-attract=w]  → the particle field orbits each visible one (weight w, max 4)
 *       appFx.comets(n)      → n comet trails, one per recent sign-in on /users
 *       pointer / tilt       → specular light on the glass edges
 *
 *       command palette      → a lens toward it while it is open
 *
 * ── There is one effects set ───────────────────────────────────────────────────────────
 * Until 2026-11-07 /settings had an Effects choice whose "Full" added a fluid sim, the scars
 * that grew on it, per-service flocks, a hero health orb and the palette's hyperspace jump.
 * Few visitors ever turned it on, and it was most of the renderer. What is left is what
 * everyone saw: the field, particles, glass, bloom and the scan effects.
 *     The scan lifecycle arrives as an `app:refresh` event that js/audio-kit.js dispatches
 *     from the calls the dashboard already makes, BEFORE its own sound-enabled gate: the
 *     visuals must follow a scan whether or not the visitor turned sound on, and neither
 *     layer holds a reference to the other.
 *     Deferred effects run off `fxClock`, which only advances on drawn frames — a scan that
 *     ends in a background tab plays its shockwave when the visitor returns.
 *
 * ── What this file no longer owns ───────────────────────────────────────────────────────
 * The rAF loop, the frame cap and the hidden/blur/reduced-motion pauses all moved to
 * js/motion-kit.js. This layer is just a named subscriber.
 */
(function () {
    'use strict';

    var MAX_GLASS_RECTS = 24;
    var GLASS_INTERVAL_MS = 66;      // at most ~15 collections/second

    /** sRGB → Display-P3, same colour. Both spaces share the sRGB transfer curve. */
    function srgbToP3(c) {
        var lin = c.map(function (v) { return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); });
        var p = [
            0.8225 * lin[0] + 0.1774 * lin[1],
            0.0332 * lin[0] + 0.9669 * lin[1],
            0.0171 * lin[0] + 0.0724 * lin[1] + 0.9108 * lin[2]
        ];
        return p.map(function (v) { return v <= 0.0031308 ? v * 12.92 : 1.055 * Math.pow(v, 1 / 2.4) - 0.055; });
    }

    /**
     * Read a CSS custom property as [r,g,b] in 0..1, in the canvas's colour space. The
     * accent tokens are `color(display-p3 …)` on wide-gamut screens (see modern-ui.css), and
     * the generic number-grabbing branch below would read the "3" out of "p3" as red.
     * A P3 token on an sRGB canvas is used as-is: that only happens on a P3 screen whose
     * browser lacks `drawingBufferColorSpace`, and the result is merely a little duller.
     */
    function readColor(name, fallback, p3) {
        var raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        if (!raw) return fallback;
        var wide = raw.match(/^color\(display-p3\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)/);
        if (wide) return [+wide[1], +wide[2], +wide[3]];
        var c;
        if (raw[0] === '#') {
            var hex = raw.slice(1);
            if (hex.length === 3) hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
            if (hex.length < 6) return fallback;
            c = [
                parseInt(hex.slice(0, 2), 16) / 255,
                parseInt(hex.slice(2, 4), 16) / 255,
                parseInt(hex.slice(4, 6), 16) / 255
            ];
        } else {
            var m = raw.match(/-?\d+(\.\d+)?/g);
            if (!m || m.length < 3) return fallback;
            c = [m[0] / 255, m[1] / 255, m[2] / 255];
        }
        return p3 ? srgbToP3(c) : c;
    }

    function palette(p3) {
        return [
            readColor('--app-accent-strong', [0.357, 0.486, 0.839], p3),
            readColor('--app-accent-warm', [0.945, 0.714, 0.388], p3),
            // The third band reuses the plain `--app-accent` token rather than requiring a
            // new CSS variable — it already exists in both themes, so no theme can forget it.
            readColor('--app-accent', [0.576, 0.706, 0.961], p3)
        ];
    }

    // A visitor who picked Effects → Full before it was removed still has the key; clear it
    // so nothing stale lingers in their storage.
    try { localStorage.removeItem('pops:fx'); } catch (e) { }

    /** Viewport-uv centre of the first element matching `selector`, or null. */
    function centreOf(selector) {
        var el = document.querySelector(selector);
        if (!el) return null;
        var r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) return null;
        return [(r.left + r.width / 2) / window.innerWidth, (r.top + r.height / 2) / window.innerHeight];
    }

    function start() {
        var canvas = document.getElementById('app-gpu-backdrop');
        if (!canvas) return;

        // Same contract as reduced-motion: never set `data-gpu-backdrop` on <html>, so the
        // static CSS grid in modern-ui.css stays visible and the [data-glass] translucency
        // (which only applies under that attribute) never kicks in. Degrading, not failing.
        if (window.motionKit && window.motionKit.minimal) {
            canvas.dataset.gpu = 'minimal';
            return;
        }

        // Records why the layer is (not) running. Read it in devtools or in tests:
        // webgl2 | webgl2-field | webgl1 | reduced-motion | no-gpu | context-lost | disposed
        var status = function (s) { canvas.dataset.gpu = s; };

        var motion = window.matchMedia('(prefers-reduced-motion: reduce)');
        var renderer = null;
        var subscription = 0;
        var qualitySub = 0;
        var building = false;

        // ── Glass rects ─────────────────────────────────────────────────────────────────
        var glassBuf = new Float32Array(MAX_GLASS_RECTS * 5);
        var glassSig = '';
        var glassAt = 0;

        function collectGlass(nowMs) {
            if (!renderer || !renderer.setGlass) return;
            if (nowMs - glassAt < GLASS_INTERVAL_MS) return;
            glassAt = nowMs;
            collectAttractors();

            var nodes = document.querySelectorAll('[data-glass]');
            var vw = window.innerWidth, vh = window.innerHeight;
            var n = 0, sig = '';
            for (var i = 0; i < nodes.length && n < MAX_GLASS_RECTS; i++) {
                var r = nodes[i].getBoundingClientRect();
                // Off-screen surfaces contribute nothing and would waste an SDF slot that a
                // visible one needs — the cap is 24 and a long catalog has far more cards.
                if (r.bottom < 0 || r.top > vh || r.width < 2 || r.height < 2) continue;
                // Cached per element. getBoundingClientRect reads already-computed layout,
                // but getComputedStyle forces a style resolution, and doing that for every
                // visible surface fifteen times a second was the single largest contributor
                // to the governor's dispatch cost. A corner radius is a design token; it does
                // not change between frames.
                var radius = nodes[i]._glassRadius;
                if (radius === undefined) {
                    radius = parseFloat(getComputedStyle(nodes[i]).borderTopLeftRadius) || 0;
                    nodes[i]._glassRadius = radius;
                }
                glassBuf[n * 5] = r.left + r.width / 2;
                glassBuf[n * 5 + 1] = r.top + r.height / 2;
                glassBuf[n * 5 + 2] = r.width / 2;
                glassBuf[n * 5 + 3] = r.height / 2;
                glassBuf[n * 5 + 4] = Math.min(radius, Math.min(r.width, r.height) / 2);
                sig += (r.left | 0) + ',' + (r.top | 0) + ',' + (r.width | 0) + ',' + (r.height | 0) + ';';
                n++;
            }
            if (sig === glassSig) return;      // nothing moved — skip the upload entirely
            glassSig = sig;
            renderer.setGlass(glassBuf, n, vw, vh);
        }

        // ── Attractors: live findings the particle field bends toward ───────────────────
        // Same cadence as the glass rects (it rides collectGlass's rate limit), no signature
        // guard: setAttractors is a 12-float copy, not a GPU upload.
        var attractBuf = new Float32Array(12);
        function collectAttractors() {
            var nodes = document.querySelectorAll('[data-fx-attract]');
            var vw = window.innerWidth, vh = window.innerHeight, n = 0;
            for (var i = 0; i < nodes.length && n < 4; i++) {
                var r = nodes[i].getBoundingClientRect();
                if (r.bottom < 0 || r.top > vh || r.width < 1) continue;
                attractBuf[n * 3] = (r.left + r.width / 2) / vw;
                attractBuf[n * 3 + 1] = (r.top + r.height / 2) / vh;
                attractBuf[n * 3 + 2] = parseFloat(nodes[i].getAttribute('data-fx-attract')) || 1;
                n++;
            }
            renderer.setAttractors(attractBuf, n);
        }

        // ── Event effects ───────────────────────────────────────────────────────────────
        var fxClock = 0;
        var queue = [];                    // { at, fn } — fired from the frame, never a timer
        var scan = { active: false, target: 0, shown: 0, strength: 0, indeterminate: false };
        var light = { at: [0.5, 0.3], goal: [0.5, 0.3], level: 0, want: 0 };
        var lens = { at: [0.5, 0.4], level: 0, want: 0 };

        function applyColors() {
            if (!renderer) return;
            renderer.setColors(palette(renderer.p3));
        }

        function later(delay, fn) { queue.push({ at: fxClock + delay, fn: fn }); }

        function ease(from, to, rate, dt) { return from + (to - from) * Math.min(1, dt * rate); }

        function tickFx(dt) {
            fxClock += dt;
            for (var i = 0; i < queue.length; i++) {
                if (queue[i].at > fxClock) continue;
                var job = queue.splice(i--, 1)[0];
                if (renderer) job.fn();
            }

            // Indeterminate (hub down, no percent arriving): an honest slow orbit, one lap per
            // ~8s, the same "working, no idea how far" the bar and the drone's tremolo give.
            if (scan.indeterminate) scan.shown = (scan.shown + dt * 0.12) % 1;
            else scan.shown = ease(scan.shown, scan.target, 4, dt);
            scan.strength = ease(scan.strength, scan.active ? 1 : 0, 2.5, dt);
            renderer.setSweep(scan.shown, scan.strength);

            light.at[0] = ease(light.at[0], light.goal[0], 6, dt);
            light.at[1] = ease(light.at[1], light.goal[1], 6, dt);
            light.level = ease(light.level, light.want, 3, dt);
            renderer.setLight(light.at[0], light.at[1], light.level);

            lens.level = ease(lens.level, lens.want, 5, dt);
            renderer.setWarp(lens.at[0], lens.at[1], lens.level);
        }

        function finishScan(outcome) {
            if (!renderer || outcome === 'cancelled') return;
            var at = centreOf('[data-fx-origin="refresh"]') || [0.5, 0.5];
            var p3 = renderer.p3;
            if (outcome === 'failure') {
                renderer.shock(at[0], at[1], readColor('--app-danger-soft', [0.949, 0.533, 0.533], p3), 1);
            } else if (outcome === 'timeout') {
                renderer.shock(at[0], at[1], readColor('--app-accent-warm', [0.945, 0.714, 0.388], p3), 0.35);
            } else {
                renderer.shock(at[0], at[1], readColor('--app-accent', [0.576, 0.706, 0.961], p3), 0);
            }
            if (outcome === 'recovered') {
                // Red → all green is the one moment on an ops page that has earned a flourish.
                var o = centreOf('[data-fx-origin="health"]') || [0.5, 0.35];
                later(0.25, function () { renderer.burst(o[0], o[1], 1); });
                later(0.45, function () { renderer.burst(o[0] - 0.2, o[1] + 0.08, 0.6); });
                later(0.60, function () { renderer.burst(o[0] + 0.2, o[1] + 0.08, 0.6); });
            }
        }

        document.addEventListener('app:refresh', function (e) {
            var d = e.detail || {};
            if (d.phase === 'start') {
                scan.active = true; scan.indeterminate = false; scan.target = scan.shown = 0;
            } else if (d.phase === 'progress') {
                scan.indeterminate = false;
                scan.target = Math.max(0, Math.min(1, (d.percent || 0) / 100));
            } else if (d.phase === 'indeterminate') {
                scan.indeterminate = true;
            } else if (d.phase === 'end') {
                scan.active = false;
                finishScan(d.outcome);
            }
        });

        /** One comet: five small bursts along a diagonal, 70ms apart — a wake, not a dot. */
        function comet(delay) {
            var u = 0.08 + Math.random() * 0.55, v = 0.02 + Math.random() * 0.15;
            var du = 0.07 + Math.random() * 0.04, dv = 0.05 + Math.random() * 0.03;
            later(delay, function () { if (window.audioKit) window.audioKit.play('arrive'); });
            for (var k = 0; k < 5; k++) {
                (function (k) {
                    later(delay + k * 0.07, function () { renderer.burst(u + du * k, v + dv * k, 0.55); });
                })(k);
            }
        }

        window.appFx.comets = function (n) {
            n = Math.min(6, Math.max(0, n | 0));
            if (!renderer) { fallbackComets(n); return; }
            for (var c = 0; c < n; c++) comet(0.6 + c * 0.9);
        };

        // Pointer light. Mouse leaving the window drops it; a touch screen gets it from tilt
        // instead. iOS gates deviceorientation behind a permission prompt, and a decorative
        // highlight is not worth asking for one, so there it simply never fires.
        window.addEventListener('pointermove', function (e) {
            var u = e.clientX / window.innerWidth, v = e.clientY / window.innerHeight;
            light.goal = [u, v];
            light.want = 1;
        }, { passive: true });

        // Command palette: a lens toward it while open. Navigating closes it.
        document.addEventListener('app:palette', function (e) {
            var d = e.detail || {};
            if (d.phase === 'open') { lens.at = [d.u, d.v]; lens.want = 0.6; }
            else if (d.phase === 'close' || d.phase === 'jump') lens.want = 0;
        });
        document.documentElement.addEventListener('mouseleave', function () { light.want = 0; });
        if (window.matchMedia('(pointer: coarse)').matches) {
            window.addEventListener('deviceorientation', function (e) {
                if (e.gamma == null || e.beta == null) return;
                var clamp = function (x) { return x < -1 ? -1 : (x > 1 ? 1 : x); };
                light.goal = [0.5 + clamp(e.gamma / 40) * 0.45, 0.5 + clamp((e.beta - 45) / 40) * 0.45];
                light.want = 0.8;
            }, { passive: true });
        }

        // ── Sizing ──────────────────────────────────────────────────────────────────────
        function applySize() {
            if (!renderer) return;
            var q = window.motionKit ? window.motionKit.quality : { scale: 0.5 };
            renderer.resize(
                Math.max(1, Math.floor(window.innerWidth * q.scale)),
                Math.max(1, Math.floor(window.innerHeight * q.scale)));
            glassSig = '';                     // force a re-upload against the new resolution
        }

        // ── Build / teardown ────────────────────────────────────────────────────────────
        function attach(r) {
            renderer = r;
            applyColors();
            if (window.motionKit) {
                renderer.setQuality(window.motionKit.quality);
                qualitySub = window.motionKit.onQuality(function (q) {
                    if (!renderer) return;
                    renderer.setQuality(q);
                    applySize();
                });
            }
            applySize();

            document.documentElement.setAttribute('data-gpu-backdrop', '');
            canvas.classList.add('is-ready');
            status(renderer.tier);

            subscription = window.motionKit.subscribe('app-gpu-backdrop', function (dt, elapsed) {
                var energy = window.audioKit ? window.audioKit.energy() : 0;
                collectGlass(performance.now());
                tickFx(dt);
                renderer.frame(dt, elapsed, energy);
            }, { fps: 30 });

            canvas.addEventListener('webglcontextlost', onContextLost);
        }

        function onContextLost(e) {
            e.preventDefault();
            teardown();
            status('context-lost');
        }

        function teardown() {
            if (subscription) { window.motionKit.unsubscribe(subscription); subscription = 0; }
            if (qualitySub) { window.motionKit.offQuality(qualitySub); qualitySub = 0; }
            canvas.removeEventListener('webglcontextlost', onContextLost);
            if (renderer) { try { renderer.dispose(); } catch (err) { } renderer = null; }
            canvas.classList.remove('is-ready');
            document.documentElement.removeAttribute('data-gpu-backdrop');
        }

        /**
         * Walk the ladder. Deliberately deferred while reduced-motion matches: building a
         * GPU context, compiling shaders and allocating particle buffers for a layer that
         * is never going to draw a frame is pure waste, so construction waits for the
         * opt-out rather than merely pausing afterwards.
         */
        function build() {
            if (renderer || building) return;
            if (motion.matches) { status('reduced-motion'); return; }
            if (!window.motionKit) { status('no-gpu'); return; }

            // Synchronous now that WebGL2 is the top rung: gfxWebgl.create() either returns
            // a renderer or null, and the WebGL1 field fallback is chosen inside it. The
            // `building` flag stays because build() is reachable from both the reduced-motion
            // listener and the initial call, and gfxWebgl.create() compiles shaders.
            building = true;
            var gl = window.gfxWebgl ? window.gfxWebgl.create(canvas) : null;
            building = false;
            if (!gl) { status('no-gpu'); return; }
            attach(gl);
        }

        // ── Signals ─────────────────────────────────────────────────────────────────────
        var resizeQueued = false;
        window.addEventListener('resize', function () {
            if (resizeQueued) return;
            resizeQueued = true;
            requestAnimationFrame(function () { resizeQueued = false; applySize(); });
        }, { passive: true });

        // Scroll only invalidates the glass geometry; the collector itself is rate-limited
        // and signature-guarded, so this listener costs a single timestamp comparison.
        window.addEventListener('scroll', function () { glassAt = 0; }, { passive: true });

        var scheme = window.matchMedia('(prefers-color-scheme: dark)');
        if (scheme.addEventListener) {
            scheme.addEventListener('change', function () { if (renderer) applyColors(); });
        }
        // An explicit header toggle changes the tokens without any media query changing, so
        // it needs its own signal — before this the backdrop kept the old theme's accents.
        document.addEventListener('app:theme', function () { if (renderer) applyColors(); });

        // Registered whether or not the query matches at load — the previous version
        // returned early before reaching this line, so a visitor who arrived with
        // reduced-motion on could never get the backdrop back by turning it off.
        if (motion.addEventListener) {
            motion.addEventListener('change', function () {
                if (motion.matches) { teardown(); status('reduced-motion'); }
                else build();
            });
        }

        build();

        // Diagnostics for devtools and the E2E assertions.
        window.gpuBackdrop = {
            get tier() { return renderer ? renderer.tier : null; },
            get status() { return canvas.dataset.gpu || null; },
            get glassRects() { return glassSig ? glassSig.split(';').length - 1 : 0; },
            get sweep() { return Math.round(scan.strength * 100) / 100; }
        };
    }

    /** No GPU layer (reduced motion, minimal device, no WebGL): the arrivals are still heard. */
    function fallbackComets(n) {
        if (n > 0 && window.audioKit) window.audioKit.play('arrive');
    }

    // Defined before start() so a page can call it whatever the layer ends up doing;
    // start() swaps in the real one once a renderer exists.
    window.appFx = { comets: fallbackComets };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
