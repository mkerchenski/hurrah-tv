// rum.js — Real User Monitoring beacon (#201, part of #200).
//
// After the page loads, reads Navigation + Resource Timing and POSTs a phase breakdown to
// /api/telemetry, but ONLY for slow loads (> threshold) or a small random sample — normal-fast
// loads send nothing, keeping volume tiny. Loaded as a classic early script (not a Blazor ES
// module) so it still fires when a slow WASM boot is itself the problem.
//
// Prod-host-gated: the beacon only sends on hurrah.tv. Every payload is env-tagged so staging
// could be opted in later by relaxing the guard. Wrapped so RUM can never throw into the page.
(function () {
    'use strict';

    // mirror the service-worker guard's idiom (index.html): know what dev/staging are and treat
    // everything else as prod — resilient to apex/www/CDN host aliases that a hardcoded
    // hostname === 'hurrah.tv' check would silently drop.
    var hostname = location.hostname;
    var env = /^(localhost|127\.0\.0\.1)$/.test(hostname) ? 'local'
        : (hostname.indexOf('staging') >= 0 ? 'staging' : 'prod');
    if (env !== 'prod') return;

    var THRESHOLD_MS = 3000;
    var SAMPLE_RATE = 0.01;

    function serverTimingMs(nav) {
        // the app;dur=<ms> emitted by ResponseTimingMiddleware on the navigation response
        if (!nav.serverTiming) return 0;
        for (var i = 0; i < nav.serverTiming.length; i++) {
            if (nav.serverTiming[i].name === 'app') return Math.round(nav.serverTiming[i].duration);
        }
        return 0;
    }

    function bundleTransferMs() {
        // slowest _framework/* resource is a good proxy for the WASM bundle download cost
        var max = 0;
        var res = performance.getEntriesByType('resource');
        for (var i = 0; i < res.length; i++) {
            if (res[i].name.indexOf('_framework/') >= 0) {
                var d = res[i].responseEnd - res[i].startTime;
                if (d > max) max = d;
            }
        }
        return Math.round(max);
    }

    function report() {
        try {
            var nav = performance.getEntriesByType('navigation')[0];
            if (!nav) return;

            var total = nav.loadEventEnd - nav.startTime;
            if (!(total > 0)) return;

            var slow = total > THRESHOLD_MS;
            if (!slow && Math.random() > SAMPLE_RATE) return;

            var sample = {
                env: env,
                url: location.pathname + location.search,
                slow: slow,
                totalMs: Math.round(total),
                ttfbMs: Math.round(nav.responseStart - nav.startTime),
                serverMs: serverTimingMs(nav),
                downloadMs: Math.round(nav.responseEnd - nav.responseStart),
                domMs: Math.round(nav.domContentLoadedEventEnd - nav.responseEnd),
                bundleMs: bundleTransferMs()
            };

            navigator.sendBeacon('/api/telemetry',
                new Blob([JSON.stringify(sample)], { type: 'application/json' }));
        } catch (e) { /* never let RUM break the page */ }
    }

    // run after the load event so loadEventEnd and resource timings are final
    if (document.readyState === 'complete') setTimeout(report, 0);
    else window.addEventListener('load', function () { setTimeout(report, 0); });
})();
