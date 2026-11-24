// One anonymous visit per browser per day → POST /api/visit → the fleet's UserSignedIn record,
// so this login-free site shows on /users (owner's ask, 2026-11-23). The id is random and lives
// only in this browser; nothing about the visitor is sent. Storage blocked (private mode, site
// data cleared) means no id and no record — a missed visit, never an error.
(function () {
    'use strict';
    var ID_KEY = 'po:visitor-id', DAY_KEY = 'po:visit-day';
    try {
        var id = localStorage.getItem(ID_KEY);
        if (!/^[a-f0-9]{16,32}$/.test(id || '')) {
            var bytes = new Uint8Array(8);
            crypto.getRandomValues(bytes);
            id = Array.prototype.map.call(bytes, function (b) { return b.toString(16).padStart(2, '0'); }).join('');
            localStorage.setItem(ID_KEY, id);
        }
        var today = new Date().toISOString().slice(0, 10); // UTC day
        if (localStorage.getItem(DAY_KEY) === today) return;
        fetch('/api/visit', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ visitorId: id, initials: null }),
            keepalive: true
        }).then(function (r) {
            // Only a recorded visit marks the day; a 429 or a network blip tries again next load.
            if (r.ok) localStorage.setItem(DAY_KEY, today);
        }).catch(function () { });
    } catch (e) { /* storage or crypto unavailable: skip */ }
})();
