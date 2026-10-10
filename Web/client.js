/* Channel Free — web client / LG helper.
 * While watching a Channel Free channel with the controls hidden:
 *   first Down press  -> small info banner (channel, title, episode, times, progress)
 *   Down/Up again     -> banner closes and Jellyfin's normal controls open
 *   Back              -> just closes the banner
 * Roku is not affected (its app handles the remote itself). */
(function () {
    'use strict';
    if (window.__cablePackage) return;
    window.__cablePackage = true;

    var BLUE = '#00A4DC';
    var SHOW_MS = 6000;
    var cable = null;       // last /QuasiTV/client/now answer for what's playing, or null
    var lastKey = '';       // what `cable` was looked up for
    var banner = null;
    var hideTimer = null;

    function api() { return window.ApiClient; }

    function videoPage() { return document.querySelector('#videoOsdPage:not(.hide)'); }

    function osdHidden() {
        var osd = document.querySelector('#videoOsdPage:not(.hide) .videoOsdBottom');
        return !osd || osd.classList.contains('videoOsdBottom-hidden');
    }

    // ---- what's playing (polled while on the video page)
    function refresh() {
        var a = api();
        if (!a || !videoPage()) { cable = null; lastKey = ''; return; }
        a.getSessions({ DeviceId: a.deviceId() }).then(function (sessions) {
            var s = (sessions || [])[0];
            var item = s && s.NowPlayingItem;
            if (!item || item.Type !== 'TvChannel') { cable = null; lastKey = ''; return; }
            var number = item.ChannelNumber || item.Number || '';
            var key = number + '|' + (item.Name || '');
            return lookup(number, item.Name).then(function (info) {
                cable = info && info.cable ? info : null;
                lastKey = key;
            });
        }).catch(function () { /* ignore */ });
    }

    function lookup(number, name) {
        var a = api();
        var url = a.getUrl('QuasiTV/client/now', { number: number || '', name: name || '' });
        return a.getJSON(url).then(function (info) {
            // clock difference between this device and the server, measured when the answer arrives
            if (info && info.nowUtc) info._skew = Date.now() - new Date(info.nowUtc).getTime();
            return info;
        });
    }

    // ---- banner
    function fmt(d) {
        return d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    }

    function el(tag, css, text) {
        var e = document.createElement(tag);
        e.style.cssText = css;
        if (text) e.textContent = text;
        return e;
    }

    function render(info) {
        removeBanner();
        var start = new Date(info.startUtc), end = new Date(info.endUtc);
        var now = Date.now() - (info._skew || 0); // server time
        var total = Math.max(1, end - start);
        var done = Math.min(1, Math.max(0, (now - start) / total));
        var left = Math.max(1, Math.ceil((end - now) / 60000));

        banner = el('div',
            'position:fixed;left:0;right:0;bottom:0;z-index:99999;padding:1.4em 3vw 1.6em;' +
            'background:linear-gradient(to top, rgba(0,0,0,.88), rgba(0,0,0,.72));border-top:3px solid ' + BLUE + ';' +
            'color:#fff;font-family:inherit;pointer-events:none;opacity:0;transition:opacity .25s ease;');
        banner.appendChild(el('div', 'color:' + BLUE + ';font-weight:700;font-size:1.05em;letter-spacing:.04em;',
            info.number + '  ·  ' + String(info.channel || '').toUpperCase()));
        banner.appendChild(el('div', 'font-weight:700;font-size:1.9em;margin-top:.15em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;',
            info.title || ''));
        if (info.sub) banner.appendChild(el('div', 'font-size:1.15em;opacity:.9;margin-top:.1em;', info.sub));
        banner.appendChild(el('div', 'font-size:1em;opacity:.8;margin-top:.45em;',
            fmt(start) + ' – ' + fmt(end) + '   ·   ' + left + ' min left' + (info.next ? '   ·   Next: ' + info.next : '')));
        var bar = el('div', 'margin-top:.7em;height:.45em;border-radius:.25em;background:rgba(255,255,255,.25);overflow:hidden;');
        bar.appendChild(el('div', 'height:100%;width:' + (done * 100).toFixed(1) + '%;background:' + BLUE + ';'));
        banner.appendChild(bar);
        document.body.appendChild(banner);
        requestAnimationFrame(function () { if (banner) banner.style.opacity = '1'; });
        clearTimeout(hideTimer);
        hideTimer = setTimeout(removeBanner, SHOW_MS);
    }

    function removeBanner() {
        clearTimeout(hideTimer);
        if (banner && banner.parentNode) banner.parentNode.removeChild(banner);
        banner = null;
    }

    function keyName(e) {
        if (e.key) return e.key;
        return { 38: 'ArrowUp', 40: 'ArrowDown', 8: 'Back', 27: 'Escape', 461: 'Back', 10009: 'Back' }[e.keyCode] || '';
    }

    // Capture on window runs before Jellyfin's own key handling.
    window.addEventListener('keydown', function (e) {
        if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
        var k = keyName(e);

        if (banner) {
            if (k === 'Back' || k === 'Escape' || k === 'GoBack') {
                removeBanner();
                e.preventDefault();
                e.stopImmediatePropagation();
                return;
            }
            // Down/Up (or anything else) while the banner is up: close it and let Jellyfin open its controls.
            removeBanner();
            return;
        }

        if ((k === 'ArrowDown' || k === 'Down') && cable && videoPage() && osdHidden()) {
            e.preventDefault();
            e.stopImmediatePropagation();
            render(cable); // instant, with what we know
            lookup(String(cable.number), cable.channel).then(function (info) {
                if (info && info.cable && banner) { cable = info; render(info); }
            }).catch(function () { /* keep the first version */ });
        }
    }, true);

    setInterval(refresh, 5000);
    document.addEventListener('viewshow', function () { setTimeout(refresh, 1500); });
})();
