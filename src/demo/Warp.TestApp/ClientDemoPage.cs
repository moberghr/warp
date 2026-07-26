namespace Warp.TestApp;

/// <summary>
/// A tiny self-contained demo SPA (served at <c>/client-demo</c>) that loads the shipped Warp browser client
/// and exercises all four client-event types (§8.27) — an unhandled error, an unhandled rejection, a
/// <c>warp.log</c>, a <c>warp.track</c>, plus Core Web Vitals — so the dashboard Client page shows real
/// browser data flowing browser → ingest → DB → dashboard in the running Aspire cluster.
/// </summary>
internal static class ClientDemoPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Warp client observability demo</title>
  <script src="/warp/ingest/client.js" data-key="pk_demo_spa" data-release="1.0.0"></script>
  <style>
    body { font-family: system-ui, sans-serif; max-width: 640px; margin: 3rem auto; padding: 0 1rem; }
    button { display: block; margin: 0.5rem 0; padding: 0.6rem 1rem; font-size: 1rem; cursor: pointer; }
    code { background: #f0f0f0; padding: 0.1rem 0.3rem; border-radius: 3px; }
  </style>
</head>
<body>
  <h1>Warp client observability demo</h1>
  <p>This page reports as application <code>warp-demo-spa</code>. Open the dashboard <a href="/warp/client">Client</a> page to watch events arrive.</p>

  <button onclick="throw new Error('demo error from button')">Throw an unhandled error</button>
  <button onclick="Promise.reject(new Error('demo unhandled rejection'))">Reject a promise</button>
  <button onclick="window.warp.log('warn', 'demo warning from button', { via: 'button' })">warp.log a warning</button>
  <button onclick="window.warp.track('demo_clicked', { via: 'button' })">warp.track an event</button>

  <script>
    // Fire one of each on load so the demo shows data without any interaction. Core Web Vitals are captured
    // automatically by client.js and flushed on page hide.
    setTimeout(function () { window.warp && window.warp.track('page_loaded', { page: 'client-demo' }); }, 300);
    setTimeout(function () { window.warp && window.warp.log('info', 'client demo page loaded'); }, 400);
    setTimeout(function () {
      var err = new Error('auto demo error on load');
      window.dispatchEvent(new ErrorEvent('error', { error: err, message: err.message }));
    }, 800);
  </script>
</body>
</html>
""";
}
