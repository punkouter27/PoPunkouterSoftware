/*
 * Visitor preferences are FIXED for everyone (owner's call, 2026-11-17):
 *   Theme   = System — follows the device. App.razor scopes Radzen's two sheets with
 *             `media="(prefers-color-scheme: …)"` and po-tokens.css keys its palette off the
 *             same media query, so no script is involved.
 *   Density = Compact — the base `--app-density` in po-tokens.css.
 *   Sound   = Off — there is no sound code at all; js/audio-kit.js was deleted.
 *   Details = Full — every uptime chip, card score/trend and Ctrl+K repo/Actions row always shows.
 *
 * Until then /settings carried a toggle for each, stored in localStorage and applied here
 * (`window.appSettings`, `window.themeKit`, a `data-app-theme` override with a circular
 * view-transition reveal). All of that is gone. What remains is this cleanup: a returning
 * visitor's old choices are deleted so nothing can resurrect them, and nothing reads them.
 */
(function () {
    'use strict';

    var LEGACY_KEYS = [
        'popunkoutersoftware:theme',
        'popunkoutersoftware:density',
        'popunkoutersoftware:sound',
        'popunkoutersoftware:details',
        'pops:theme',
        'poshared-theme'
    ];

    try {
        for (var i = 0; i < LEGACY_KEYS.length; i++) localStorage.removeItem(LEGACY_KEYS[i]);
    } catch (e) {
        // Storage disabled (private mode): there is nothing stored to clear.
    }
})();
