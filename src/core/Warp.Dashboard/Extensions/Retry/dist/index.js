/**
 * Warp Retry UI Extension
 *
 * Demonstrates the UI extension system by adding a "Retry Configuration" card
 * to the job detail page for jobs that have retry metadata.
 *
 * Uses window.Warp SDK — no bundled React or component library.
 */

const { React, ReactDOM, api, components } = window.Warp;
const { createElement: h, useState, useEffect } = React;
const { Card, CardContent, CardHeader, CardTitle } = components;

/**
 * Retry info card — shows retry configuration and progress for a job.
 * Mounted after the "Details" card on the job detail page.
 */
function RetryCard(props) {
  const jobId = props.jobId;
  const [job, setJob] = useState(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!jobId) return;
    setLoading(true);
    api.get('/detail/' + jobId)
      .then(function (r) { setJob(r.data); })
      .catch(function () { /* ignore */ })
      .finally(function () { setLoading(false); });
  }, [jobId]);

  // Don't render if no retry metadata
  if (loading || !job) return null;

  const metadata = job.metadata;
  if (!metadata) return null;

  const maxRetries = metadata.MaxRetries;
  if (!maxRetries) return null;

  const retriedTimes = metadata.RetriedTimes || 0;
  const retryDelays = metadata.RetryDelays;

  // Progress percentage
  var pct = maxRetries > 0 ? Math.round((retriedTimes / maxRetries) * 100) : 0;

  // Color based on progress
  var barColor = retriedTimes >= maxRetries
    ? 'bg-red-500'
    : retriedTimes > 0
      ? 'bg-yellow-500'
      : 'bg-blue-500';

  var statusText = retriedTimes >= maxRetries
    ? 'Retries exhausted'
    : retriedTimes > 0
      ? 'Retrying...'
      : 'Retry policy active';

  var statusColor = retriedTimes >= maxRetries
    ? 'text-red-600 dark:text-red-400'
    : retriedTimes > 0
      ? 'text-yellow-600 dark:text-yellow-400'
      : 'text-blue-600 dark:text-blue-400';

  return h(Card, null,
    h(CardHeader, { className: 'pb-2' },
      h(CardTitle, { className: 'text-sm flex items-center gap-2' },
        h('span', null, 'Retry'),
        h('span', { className: 'text-xs font-normal ' + statusColor }, statusText)
      )
    ),
    h(CardContent, { className: 'space-y-3' },
      // Progress bar
      h('div', { className: 'space-y-1' },
        h('div', { className: 'flex justify-between text-xs text-muted-foreground' },
          h('span', null, 'Attempts'),
          h('span', null, retriedTimes + ' / ' + maxRetries)
        ),
        h('div', { className: 'h-2 bg-muted rounded-full overflow-hidden' },
          h('div', {
            className: 'h-full transition-all rounded-full ' + barColor,
            style: { width: pct + '%' }
          })
        )
      ),

      // Details
      h('div', { className: 'space-y-1 text-sm' },
        retryDelays && retryDelays.length > 0 &&
          h('div', null,
            h('span', { className: 'text-muted-foreground' }, 'Delays: '),
            retryDelays.map(function (d) { return d + 's'; }).join(', ')
          ),
        retriedTimes > 0 && retryDelays && retryDelays.length > 0 &&
          h('div', null,
            h('span', { className: 'text-muted-foreground' }, 'Next delay: '),
            (retryDelays[Math.min(retriedTimes, retryDelays.length - 1)] || retryDelays[retryDelays.length - 1]) + 's'
          )
      )
    )
  );
}

/**
 * Extension install function — called by the Warp extension loader.
 */
export function install(warp) {
  // Add retry card after the Details section on the job detail page
  warp.insertAfter('[data-warp-slot="detail.details"]', RetryCard);
}
