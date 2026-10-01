(() => {
    const storageKey = 'nsx-online-visitor-id';
    const firstPingDelayMs = 8000;
    const pingIntervalMs = 30000;
    const minimumGapMs = 12000;
    let visitorId = '';
    let lastPingAt = 0;

    try {
        visitorId = localStorage.getItem(storageKey) || '';
        if (!visitorId) {
            visitorId = window.crypto && typeof window.crypto.randomUUID === 'function'
                ? window.crypto.randomUUID()
                : `v-${Date.now()}-${Math.random().toString(16).slice(2)}`;
            localStorage.setItem(storageKey, visitorId);
        }
    } catch {
        visitorId = `v-${Date.now()}-${Math.random().toString(16).slice(2)}`;
    }

    const ping = (force = false) => {
        if (document.visibilityState === 'hidden') return;
        const now = Date.now();
        if (!force && now - lastPingAt < minimumGapMs) return;
        lastPingAt = now;

        fetch('/presence/ping', {
            method: 'POST',
            credentials: 'same-origin',
            cache: 'no-store',
            keepalive: true,
            headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
            body: JSON.stringify({
                visitorId,
                pagePath: `${location.pathname}${location.search}`,
                pageTitle: document.title,
                referrer: document.referrer
            })
        }).catch(() => { });
    };

    const start = () => {
        window.setTimeout(() => ping(true), firstPingDelayMs);
        window.setInterval(() => ping(false), pingIntervalMs);
    };

    if (document.readyState === 'complete') start();
    else window.addEventListener('load', start, { once: true });

    window.addEventListener('focus', () => ping(false), { passive: true });
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') ping(false);
    }, { passive: true });
})();
