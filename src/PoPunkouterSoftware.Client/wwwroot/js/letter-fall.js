/**
 * letter-fall — the home page lets go of its letters (owner's ask, 2026-11-23).
 *
 * After 30 s with no mouse, key, scroll or touch on the home page, or when the visitor presses
 * the × in the top bar (PoHeader renders it only on "/"), every letter visible in the viewport
 * drops to the bottom of the screen under real 2D physics: each glyph is its own Matter.js
 * body, so letters tumble, collide and pile up. A click, tap, key or resize puts them back —
 * each one flies home — and the idle clock starts again.
 *
 * HOW: the visible characters are measured with a Range per character, copied into a fixed
 * overlay as positioned spans (same font, size and colour), and the page's own text goes
 * `color: transparent` underneath (html.lf-fallen in modern-ui.css), so boxes, borders and
 * pictures stay where they are and only the text appears to fall. A Material icon falls as
 * one glyph, not as the letters of its ligature name ("sync" → s, y, n, c).
 *
 * Matter.js (vendored in lib/matter-js, MIT) is loaded only when the effect first fires — the
 * CSP's `script-src 'self'` rules out a CDN. The frame loop is a motionKit subscriber, like
 * every other animated layer, and unsubscribes once every body sleeps. Reduced motion or a
 * motionKit "minimal" device turns the whole thing off, × included (hidden in CSS).
 *
 * The overlay is aria-hidden: a screen reader keeps reading the real page underneath.
 */
(function () {
    'use strict';

    var IDLE_MS = 30000;
    var MAX_LETTERS = 2200;   // a dense viewport of the stack table is ~1,500; beyond this Matter stutters
    var ICON_SEL = '.material-symbols-outlined, .material-icons, .rz-icon';

    var reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
    var lastActivity = Date.now();
    var idleTimer = 0;
    var state = null;          // { layer, engine, items, sub, phase }
    var matterPromise = null;

    function enabled() {
        return !reduce.matches && !(window.motionKit && window.motionKit.minimal);
    }

    function onHome() {
        return location.pathname === '/' && !!document.querySelector('.portfolio-stack');
    }

    // ── Idle clock ──────────────────────────────────────────────────────────────────────
    function schedule(ms) {
        clearTimeout(idleTimer);
        idleTimer = setTimeout(checkIdle, ms);
    }

    function checkIdle() {
        var left = IDLE_MS - (Date.now() - lastActivity);
        if (left > 50) { schedule(left); return; }
        if (!state && !document.hidden && onHome() && !document.querySelector('[role="dialog"]')) {
            drop();
        }
        if (!state) { lastActivity = Date.now(); schedule(IDLE_MS); }
    }

    function activity() {
        if (state) return;     // while fallen, the overlay's own listeners decide
        lastActivity = Date.now();
    }

    ['pointermove', 'pointerdown', 'keydown', 'wheel', 'touchstart', 'scroll'].forEach(function (t) {
        window.addEventListener(t, activity, { capture: true, passive: true });
    });
    document.addEventListener('visibilitychange', function () {
        if (!document.hidden) { lastActivity = Date.now(); schedule(IDLE_MS); }
    });
    document.addEventListener('click', function (e) {
        var btn = e.target && e.target.closest && e.target.closest('[data-letter-fall]');
        if (btn && !state) { e.preventDefault(); drop(); }
    });
    schedule(IDLE_MS);

    // ── Matter.js, on first use ─────────────────────────────────────────────────────────
    function loadMatter() {
        if (window.Matter) return Promise.resolve(window.Matter);
        if (!matterPromise) {
            matterPromise = new Promise(function (resolve, reject) {
                var s = document.createElement('script');
                s.src = new URL('lib/matter-js/matter.min.js', document.baseURI).href;
                s.onload = function () { resolve(window.Matter); };
                s.onerror = function () { matterPromise = null; reject(new Error('matter-js failed to load')); };
                document.head.appendChild(s);
            });
        }
        return matterPromise;
    }

    // ── Measuring the page ──────────────────────────────────────────────────────────────
    /** The box an element's content is visible in: the viewport, cut by every clipping ancestor. */
    function clipBox(el, cache) {
        if (!el || el === document.body || el === document.documentElement) {
            return { l: 0, t: 0, r: window.innerWidth, b: window.innerHeight };
        }
        if (cache.has(el)) return cache.get(el);
        var box = clipBox(el.parentElement, cache);
        var cs = getComputedStyle(el);
        if (cs.overflowX !== 'visible' || cs.overflowY !== 'visible') {
            var r = el.getBoundingClientRect();
            box = { l: Math.max(box.l, r.left), t: Math.max(box.t, r.top), r: Math.min(box.r, r.right), b: Math.min(box.b, r.bottom) };
        }
        cache.set(el, box);
        return box;
    }

    function inside(rect, box) {
        var cx = rect.left + rect.width / 2, cy = rect.top + rect.height / 2;
        return cx > box.l && cx < box.r && cy > box.t && cy < box.b;
    }

    function textStyle(el, styles, fallbackColor) {
        if (styles.has(el)) return styles.get(el);
        var cs = getComputedStyle(el);
        var color = cs.webkitTextFillColor && cs.webkitTextFillColor !== cs.color ? cs.webkitTextFillColor : cs.color;
        if (/rgba\([^)]*,\s*0\)$/.test(color) || color === 'transparent') color = fallbackColor;
        var s = {
            font: cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily,
            color: color,
            transform: cs.textTransform,
            settings: cs.fontVariationSettings,
            visible: cs.visibility === 'visible' && parseFloat(cs.opacity) > 0.05
        };
        styles.set(el, s);
        return s;
    }

    function collect() {
        var out = [];
        var clips = new Map(), styles = new Map(), icons = new Set();
        var fallbackColor = getComputedStyle(document.body).color;
        var range = document.createRange();
        var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
            acceptNode: function (n) {
                if (!/\S/.test(n.nodeValue)) return NodeFilter.FILTER_REJECT;
                var p = n.parentElement;
                if (!p || p.closest('script,style,noscript,template,[hidden],[aria-hidden="true"].app-sr-only,.lf-layer')) return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        for (var node = walker.nextNode(); node && out.length < MAX_LETTERS; node = walker.nextNode()) {
            var parent = node.parentElement;
            if (parent.checkVisibility && !parent.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) continue;
            var style = textStyle(parent, styles, fallbackColor);
            if (!style.visible) continue;
            var box = clipBox(parent, clips);
            if (box.r - box.l < 2 || box.b - box.t < 2) continue;

            // A ligature icon falls whole.
            var icon = parent.closest(ICON_SEL);
            if (icon) {
                if (icons.has(icon)) continue;
                icons.add(icon);
                var ir = icon.getBoundingClientRect();
                if (ir.width < 2 || ir.height < 2 || !inside(ir, box)) continue;
                out.push({ text: icon.textContent.trim(), rect: ir, style: style, icon: true });
                continue;
            }

            var text = node.nodeValue, offset = 0;
            var chars = Array.from(text);   // code points, so an emoji is one body
            for (var i = 0; i < chars.length && out.length < MAX_LETTERS; i++) {
                var ch = chars[i], len = ch.length;
                if (/\s/.test(ch)) { offset += len; continue; }
                range.setStart(node, offset);
                range.setEnd(node, offset + len);
                offset += len;
                var rects = range.getClientRects();
                var r = null;
                for (var k = 0; k < rects.length; k++) { if (rects[k].width > 0.5) { r = rects[k]; break; } }
                if (!r || r.height < 2 || !inside(r, box)) continue;
                if (style.transform === 'uppercase') ch = ch.toUpperCase();
                else if (style.transform === 'lowercase') ch = ch.toLowerCase();
                out.push({ text: ch, rect: r, style: style, icon: false });
            }
        }
        range.detach && range.detach();
        return out;
    }

    // ── The fall ────────────────────────────────────────────────────────────────────────
    function drop() {
        if (state || !enabled() || !onHome()) return;
        state = { phase: 'loading' };
        loadMatter().then(start, function (err) {
            console.warn('letter-fall:', err);
            state = null;
        });
    }

    function start(Matter) {
        if (!onHome()) { state = null; return; }
        var letters = collect();
        if (!letters.length) { state = null; return; }

        var vw = window.innerWidth, vh = window.innerHeight;
        var layer = document.createElement('div');
        layer.className = 'lf-layer';
        layer.setAttribute('aria-hidden', 'true');

        var engine = Matter.Engine.create({ enableSleeping: true });
        engine.gravity.y = 1.1;
        var T = 400;   // wall thickness: thick, so a fast glyph cannot tunnel through
        Matter.Composite.add(engine.world, [
            Matter.Bodies.rectangle(vw / 2, vh + T / 2, vw + 2 * T, T, { isStatic: true, friction: 0.8 }),
            Matter.Bodies.rectangle(-T / 2, vh / 2 - vh, T, vh * 4, { isStatic: true }),
            Matter.Bodies.rectangle(vw + T / 2, vh / 2 - vh, T, vh * 4, { isStatic: true })
        ]);

        var frag = document.createDocumentFragment();
        var items = letters.map(function (l) {
            var w = Math.max(l.rect.width, 3), h = Math.max(l.rect.height, 4);
            var cx = l.rect.left + w / 2, cy = l.rect.top + h / 2;
            // The body hugs the glyph a little tighter than its line box, so piles look packed.
            var body = Matter.Bodies.rectangle(cx, cy, w, l.icon ? h : h * 0.72, {
                restitution: 0.12, friction: 0.5, frictionAir: 0.012, density: 0.0015, sleepThreshold: 45
            });
            Matter.Body.setStatic(body, true);

            var el = document.createElement('span');
            el.className = l.icon ? 'lf-glyph material-symbols-outlined' : 'lf-glyph';
            el.textContent = l.text;
            el.style.width = w + 'px';
            el.style.height = h + 'px';
            el.style.lineHeight = h + 'px';
            el.style.font = l.style.font;
            if (l.icon) el.style.fontSize = Math.round(h) + 'px';
            if (l.style.settings && l.style.settings !== 'normal') el.style.fontVariationSettings = l.style.settings;
            el.style.setProperty('color', l.style.color, 'important');
            el.style.setProperty('-webkit-text-fill-color', l.style.color, 'important');
            el.style.transform = 'translate(' + (cx - w / 2) + 'px,' + (cy - h / 2) + 'px)';
            frag.appendChild(el);

            return {
                body: body, el: el, w: w, h: h, home: { x: cx, y: cy },
                // Lower letters let go first, with a little jitter, so the pile builds from the floor up.
                release: (1 - cy / vh) * 260 + Math.random() * 180
            };
        });
        Matter.Composite.add(engine.world, items.map(function (it) { return it.body; }));
        layer.appendChild(frag);
        document.body.appendChild(layer);
        document.documentElement.classList.add('lf-fallen');

        state = { phase: 'falling', layer: layer, engine: engine, items: items, Matter: Matter, t: 0, still: 0, sub: 0 };

        layer.addEventListener('pointerdown', restore);
        layer.addEventListener('wheel', function (e) { e.preventDefault(); }, { passive: false });
        window.addEventListener('keydown', restore, true);
        window.addEventListener('resize', restore);

        state.sub = window.motionKit
            ? window.motionKit.subscribe('letter-fall', step, { fps: 60 })
            : rafLoop(step);
    }

    /** Fallback when motionKit is missing: the same callback signature on a plain rAF. */
    function rafLoop(fn) {
        var id = 0, last = performance.now(), alive = true;
        function frame(now) {
            if (!alive) return;
            fn(Math.min(0.05, (now - last) / 1000));
            last = now;
            id = requestAnimationFrame(frame);
        }
        id = requestAnimationFrame(frame);
        return { cancel: function () { alive = false; cancelAnimationFrame(id); } };
    }

    function stopLoop(s) {
        if (!s.sub) return;
        if (typeof s.sub === 'number') window.motionKit.unsubscribe(s.sub);
        else s.sub.cancel();
        s.sub = 0;
    }

    function step(dt) {
        var s = state;
        if (!s || s.phase !== 'falling') return;
        var M = s.Matter;
        s.t += dt * 1000;

        for (var i = 0; i < s.items.length; i++) {
            var it = s.items[i];
            if (it.release !== null && s.t >= it.release) {
                it.release = null;
                M.Body.setStatic(it.body, false);
                M.Body.setAngularVelocity(it.body, (Math.random() - 0.5) * 0.08);
                M.Body.setVelocity(it.body, { x: (Math.random() - 0.5) * 1.2, y: Math.random() * 0.5 });
            }
        }

        // Fixed sub-steps: Matter is only stable at a constant delta.
        var steps = Math.max(1, Math.min(3, Math.round(dt * 60)));
        for (var n = 0; n < steps; n++) M.Engine.update(s.engine, 1000 / 60);

        var moving = 0;
        for (var j = 0; j < s.items.length; j++) {
            var b = s.items[j].body, el = s.items[j].el;
            if (s.items[j].release !== null) { moving++; continue; }
            if (b.isSleeping && el._slept) continue;
            el._slept = b.isSleeping;
            if (!b.isSleeping) moving++;
            el.style.transform = 'translate(' + (b.position.x - s.items[j].w / 2) + 'px,' +
                (b.position.y - s.items[j].h / 2) + 'px) rotate(' + b.angle + 'rad)';
        }
        // Everything asleep for half a second: stop asking for frames until restore.
        s.still = moving ? 0 : s.still + dt;
        if (s.still > 0.5) { s.phase = 'resting'; stopLoop(s); }
    }

    // ── Putting the page back ───────────────────────────────────────────────────────────
    function restore(e) {
        var s = state;
        if (!s || (s.phase !== 'falling' && s.phase !== 'resting')) return;
        if (e && e.type === 'keydown') { e.preventDefault(); e.stopPropagation(); }
        if (e && e.type === 'pointerdown') e.preventDefault();
        stopLoop(s);
        s.phase = 'returning';
        window.removeEventListener('keydown', restore, true);
        window.removeEventListener('resize', restore);

        if (e && e.type === 'resize') { finish(); return; }

        var from = s.items.map(function (it) {
            return { x: it.body.position.x, y: it.body.position.y, a: it.body.angle };
        });
        var t = 0, DUR = 0.7;
        function back(dt) {
            t += dt;
            for (var i = 0; i < s.items.length; i++) {
                var it = s.items[i], f = from[i];
                // A per-letter delay, so they don't all leave the pile at once.
                var p = Math.min(1, Math.max(0, (t - (i % 23) * 0.006) / DUR));
                var q = 1 - Math.pow(1 - p, 3);
                var x = f.x + (it.home.x - f.x) * q, y = f.y + (it.home.y - f.y) * q, a = f.a * (1 - q);
                it.el.style.transform = 'translate(' + (x - it.w / 2) + 'px,' + (y - it.h / 2) + 'px) rotate(' + a + 'rad)';
            }
            if (t >= DUR + 0.15) { stopLoop(s); finish(); }
        }
        s.sub = window.motionKit ? window.motionKit.subscribe('letter-fall-return', back, { fps: 60 }) : rafLoop(back);
    }

    function finish() {
        var s = state;
        if (!s) return;
        document.documentElement.classList.remove('lf-fallen');
        if (s.layer) s.layer.remove();
        if (s.engine && s.Matter) s.Matter.Engine.clear(s.engine);
        state = null;
        lastActivity = Date.now();
        schedule(IDLE_MS);
    }
})();
