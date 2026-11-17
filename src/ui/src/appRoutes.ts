import { createElement, lazy } from 'react';
import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';

// The dashboard's page routes, below MainLayout, code-split like the rest (see App.tsx). Exported so tests resolve
// links against the real table rather than a copy that cannot drift from it. The index route (the dashboard itself)
// and the extension pages stay in App.tsx. Plain .ts with createElement: a .tsx exporting this array beside the lazy
// page components would break fast refresh (react-refresh/only-export-components).

const JobListPage = lazy(() => import('@/pages/jobs/JobListPage'));
const JobsByTypePage = lazy(() => import('@/pages/jobs/JobsByTypePage'));
const MessagesPage = lazy(() => import('@/pages/messages/MessagesPage'));
const BatchesPage = lazy(() => import('@/pages/batches/BatchesPage'));
const RecurringPage = lazy(() => import('@/pages/recurring/RecurringPage'));
const RecurringDetailPage = lazy(() => import('@/pages/recurring/RecurringDetailPage'));
const ApplicationsPage = lazy(() => import('@/pages/applications/ApplicationsPage'));
const ApplicationDetailPage = lazy(() => import('@/pages/applications/ApplicationDetailPage'));
const ApplicationInstanceDetailPage = lazy(() => import('@/pages/applications/ApplicationInstanceDetailPage'));
const ServerDetailPage = lazy(() => import('@/pages/servers/ServerDetailPage'));
const CountersPage = lazy(() => import('@/pages/counters/CountersPage'));
const ConcurrencyLimitsPage = lazy(() => import('@/pages/concurrency/ConcurrencyLimitsPage'));
const RateLimitsPage = lazy(() => import('@/pages/ratelimits/RateLimitsPage'));
const SagasListPage = lazy(() => import('@/pages/sagas/SagasListPage'));
const SagaDetailPage = lazy(() => import('@/pages/sagas/SagaDetailPage'));
const BackgroundServicesList = lazy(() => import('@/pages/BackgroundServices/List'));
const BackgroundServiceDetail = lazy(() => import('@/pages/BackgroundServices/Detail'));
const AdaptersPage = lazy(() => import('@/pages/adapters/AdaptersPage'));
const AdapterDetailPage = lazy(() => import('@/pages/adapters/AdapterDetailPage'));
const AdapterCallDetailPage = lazy(() => import('@/pages/adapters/AdapterCallDetailPage'));
const EndpointsPage = lazy(() => import('@/pages/endpoints/EndpointsPage'));
const EndpointDetailPage = lazy(() => import('@/pages/endpoints/EndpointDetailPage'));
const EndpointCallDetailPage = lazy(() => import('@/pages/endpoints/EndpointCallDetailPage'));
const ClientPage = lazy(() => import('@/pages/client/ClientPage'));
const ClientSessionPage = lazy(() => import('@/pages/client/ClientSessionPage'));
const ClientEventDetailPage = lazy(() => import('@/pages/client/ClientEventDetailPage'));
const IssuesPage = lazy(() => import('@/pages/issues/IssuesPage'));
const IssueDetailPage = lazy(() => import('@/pages/issues/IssueDetailPage'));
const SloPage = lazy(() => import('@/pages/slo/SloPage'));
const SloDetailPage = lazy(() => import('@/pages/slo/SloDetailPage'));
const MetricsPage = lazy(() => import('@/pages/metrics/MetricsPage'));
const MetricDetailPage = lazy(() => import('@/pages/metrics/MetricDetailPage'));
const MetricBoardPage = lazy(() => import('@/pages/metrics/MetricBoardPage'));
const WebhooksPage = lazy(() => import('@/pages/webhooks/WebhooksPage'));
const WebhookGroupDetailPage = lazy(() => import('@/pages/webhooks/WebhookGroupDetailPage'));
const WebhookDetailPage = lazy(() => import('@/pages/webhooks/WebhookDetailPage'));
const WorkerDetailPage = lazy(() => import('@/pages/workers/WorkerDetailPage'));
const TracePage = lazy(() => import('@/pages/trace/TracePage'));
const DetailPage = lazy(() => import('@/pages/detail/DetailPage'));

export const appRoutes: { path: string; element: ReactNode }[] = [
  { path: '/detail/:id', element: createElement(DetailPage) },
  { path: '/jobs/detail/:id', element: createElement(DetailPage) },
  { path: '/jobs/by-type/:type', element: createElement(JobsByTypePage) },
  { path: '/jobs/:state', element: createElement(JobListPage) },
  { path: '/messages/detail/:id', element: createElement(DetailPage) },
  { path: '/messages/:state', element: createElement(MessagesPage) },
  { path: '/batches/detail/:id', element: createElement(DetailPage) },
  { path: '/batches/:state', element: createElement(BatchesPage) },
  { path: '/recurring/:id', element: createElement(RecurringDetailPage) },
  { path: '/recurring', element: createElement(RecurringPage) },
  { path: '/trace/:traceId/:highlightId?', element: createElement(TracePage) },
  { path: '/workers/:id', element: createElement(WorkerDetailPage) },
  // Server-instance detail keeps its own route (worker groups / pause live here); the Applications list links server instances straight to it.
  { path: '/servers/:id', element: createElement(ServerDetailPage) },
  // Old /servers bookmarks land on the renamed Applications page.
  { path: '/servers', element: createElement(Navigate, { to: '/applications', replace: true }) },
  { path: '/applications/:id/instances/:instanceId', element: createElement(ApplicationInstanceDetailPage) },
  { path: '/applications/:id', element: createElement(ApplicationDetailPage) },
  { path: '/applications', element: createElement(ApplicationsPage) },
  // The standalone Queues page folded into the Counters queues family in 6.0.
  { path: '/queues', element: createElement(Navigate, { to: '/counters/queues', replace: true }) },
  { path: '/counters', element: createElement(CountersPage) },
  { path: '/counters/:family', element: createElement(CountersPage) },
  { path: '/concurrency', element: createElement(ConcurrencyLimitsPage) },
  { path: '/ratelimits', element: createElement(RateLimitsPage) },
  { path: '/sagas/:id', element: createElement(SagaDetailPage) },
  { path: '/sagas', element: createElement(SagasListPage) },
  { path: '/services/:name', element: createElement(BackgroundServiceDetail) },
  { path: '/services', element: createElement(BackgroundServicesList) },
  { path: '/adapters/:name/calls/:callId', element: createElement(AdapterCallDetailPage) },
  { path: '/adapters/:name', element: createElement(AdapterDetailPage) },
  { path: '/adapters', element: createElement(AdaptersPage) },
  { path: '/endpoints/:id/calls/:callId', element: createElement(EndpointCallDetailPage) },
  { path: '/endpoints/:id', element: createElement(EndpointDetailPage) },
  { path: '/endpoints', element: createElement(EndpointsPage) },
  { path: '/client', element: createElement(ClientPage) },
  { path: '/client/sessions/:id', element: createElement(ClientSessionPage) },
  { path: '/client/events/:id', element: createElement(ClientEventDetailPage) },
  { path: '/issues/:fingerprint', element: createElement(IssueDetailPage) },
  { path: '/issues', element: createElement(IssuesPage) },
  { path: '/slo/:id', element: createElement(SloDetailPage) },
  { path: '/slo', element: createElement(SloPage) },
  { path: '/metrics/board', element: createElement(MetricBoardPage) },
  { path: '/metrics/:id', element: createElement(MetricDetailPage) },
  { path: '/metrics', element: createElement(MetricsPage) },
  { path: '/webhooks/group/:dim/:key', element: createElement(WebhookGroupDetailPage) },
  { path: '/webhooks/:id', element: createElement(WebhookDetailPage) },
  { path: '/webhooks', element: createElement(WebhooksPage) },
];
