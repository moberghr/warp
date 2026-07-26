namespace Warp.Http.ClientObservability;

/// <summary>
/// The self-contained browser client shipped by Warp (§8.27), served at <c>GET {IngestPath}/client.js</c>.
/// Include it with a script tag carrying <c>data-key</c> (the DSN) and optional <c>data-endpoint</c> /
/// <c>data-release</c> / <c>data-sample-rate</c>; it auto-captures unhandled errors + Core Web Vitals, keeps a
/// breadcrumb trail, and exposes <c>window.warp.log(level, message, props)</c> / <c>window.warp.track(name,
/// props)</c>. Events batch and flush via <c>fetch(keepalive)</c> (or <c>sendBeacon</c>) on an interval and on
/// page hide. Sampling is per-session and consistent (a sampled-out session sends nothing).
/// </summary>
public static class WarpClientScript
{
    public const string Content = """
(function () {
  var s = document.currentScript;
  if (!s) { return; }
  var key = s.getAttribute('data-key');
  var endpoint = s.getAttribute('data-endpoint') || (s.src ? s.src.replace(/\/client\.js.*$/, '') : '');
  var release = s.getAttribute('data-release') || undefined;
  var sampleRate = parseFloat(s.getAttribute('data-sample-rate') || '1');
  if (!key || !endpoint) { return; }

  function rand() { return Math.random().toString(36).slice(2) + Date.now().toString(36); }
  var session;
  try {
    session = sessionStorage.getItem('warp.session') || rand();
    sessionStorage.setItem('warp.session', session);
  } catch (e) { session = rand(); }

  // Consistent per-session sampling: hash the session to a stable [0,1) and compare once.
  function hash(str) { var h = 2166136261; for (var i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619); } return ((h >>> 0) % 10000) / 10000; }
  var sampled = hash(session) < sampleRate;

  var crumbs = [];
  function crumb(type, data) { crumbs.push({ t: Date.now(), type: type, data: data }); if (crumbs.length > 20) { crumbs.shift(); } }

  var queue = [];
  var timer = null;
  function enqueue(evt) {
    if (!sampled) { return; }
    evt.ts = Date.now();
    if (!evt.url) { evt.url = location.pathname; }
    queue.push(evt);
    if (!timer) { timer = setTimeout(flush, 3000); }
  }

  function flush() {
    timer = null;
    if (!queue.length) { return; }
    var batch = { key: key, session: session, release: release, events: queue.splice(0, 100) };
    var body = JSON.stringify(batch);
    try {
      if (typeof fetch === 'function') {
        fetch(endpoint, { method: 'POST', headers: { 'content-type': 'application/json', 'x-warp-key': key }, body: body, keepalive: true, mode: 'cors' });
      } else if (navigator.sendBeacon) {
        navigator.sendBeacon(endpoint, new Blob([body], { type: 'application/json' }));
      }
    } catch (e) { /* lossy by design */ }
  }

  var warp = {
    log: function (level, message, props) { enqueue({ type: 'log', level: level || 'info', message: String(message), props: props }); },
    track: function (name, props) { enqueue({ type: 'event', name: String(name), props: props }); }
  };
  window.warp = warp;

  window.addEventListener('error', function (e) {
    enqueue({ type: 'error', name: (e.error && e.error.name) || 'Error', message: (e.message || (e.error && e.error.message)), stack: e.error && e.error.stack, props: { breadcrumbs: crumbs.slice() } });
  });
  window.addEventListener('unhandledrejection', function (e) {
    var r = e.reason || {};
    enqueue({ type: 'error', name: r.name || 'UnhandledRejection', message: String(r.message || r), stack: r.stack, props: { breadcrumbs: crumbs.slice() } });
  });

  document.addEventListener('click', function (e) { var t = e.target; crumb('click', t && t.tagName ? (t.tagName + (t.id ? '#' + t.id : '')) : 'node'); }, true);
  var pushState = history.pushState;
  history.pushState = function () { crumb('navigation', arguments[2]); return pushState.apply(this, arguments); };

  // Core Web Vitals via PerformanceObserver; reported once on page hide.
  var vitals = {};
  function observe(type, cb) { try { var po = new PerformanceObserver(cb); po.observe({ type: type, buffered: true }); return po; } catch (e) { return null; } }
  observe('largest-contentful-paint', function (l) { var es = l.getEntries(); vitals.LCP = es[es.length - 1].startTime; });
  observe('paint', function (l) { l.getEntries().forEach(function (en) { if (en.name === 'first-contentful-paint') { vitals.FCP = en.startTime; } }); });
  var cls = 0;
  observe('layout-shift', function (l) { l.getEntries().forEach(function (en) { if (!en.hadRecentInput) { cls += en.value; } }); vitals.CLS = cls; });
  var inp = 0;
  observe('event', function (l) { l.getEntries().forEach(function (en) { if (en.duration > inp) { inp = en.duration; } }); vitals.INP = inp; });
  try { var nav = performance.getEntriesByType('navigation')[0]; if (nav) { vitals.TTFB = nav.responseStart; } } catch (e) { }

  var reported = false;
  function reportVitals() {
    if (reported) { return; }
    reported = true;
    Object.keys(vitals).forEach(function (name) { if (typeof vitals[name] === 'number') { enqueue({ type: 'vital', name: name, value: vitals[name] }); } });
    flush();
  }

  addEventListener('visibilitychange', function () { if (document.visibilityState === 'hidden') { reportVitals(); flush(); } });
  addEventListener('pagehide', function () { reportVitals(); flush(); });
})();
""";
}
