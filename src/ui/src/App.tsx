import { lazy, Suspense, useState, useEffect, useCallback } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { Toaster } from 'sonner';
import { queryClient } from '@/lib/queryClient';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import MainLayout from '@/layouts/MainLayout';
import ExtensionPage from '@/extensions/ExtensionPage';

// Route pages are code-split: each becomes its own chunk loaded on navigation,
// so the initial bundle no longer carries every page (notably the trace graph's
// @xyflow/@dagrejs and the chart stack). MainLayout renders the Suspense boundary
// around <Outlet>, so the shell/nav stays put while a page chunk loads.
const DashboardPage = lazy(() => import('@/pages/dashboard/DashboardPage'));
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
const QueuesPage = lazy(() => import('@/pages/queues/QueuesPage'));
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
const WebhooksPage = lazy(() => import('@/pages/webhooks/WebhooksPage'));
const WebhookGroupDetailPage = lazy(() => import('@/pages/webhooks/WebhookGroupDetailPage'));
const WebhookDetailPage = lazy(() => import('@/pages/webhooks/WebhookDetailPage'));
const WorkerDetailPage = lazy(() => import('@/pages/workers/WorkerDetailPage'));
const TracePage = lazy(() => import('@/pages/trace/TracePage'));
const DetailPage = lazy(() => import('@/pages/detail/DetailPage'));
const LoginPage = lazy(() => import('@/pages/auth/LoginPage'));
import { setOnUnauthorized } from '@/api/client';
import { loadExtensions } from '@/extensions/loader';
import { extensionRuntime } from '@/extensions/runtime';
import { getAuthStatus } from '@/api';
import { config } from '@/config';
import type { ExtensionManifest } from '@/extensions/types';

function App() {
  const [needsLogin, setNeedsLogin] = useState(false);
  const [extensions, setExtensions] = useState<ExtensionManifest[]>([]);
  const [extensionsLoaded, setExtensionsLoaded] = useState(false);
  // Cold-boot gate so we don't fire any other API calls before we know whether
  // the user is authenticated. Skipped entirely when the built-in login addon
  // isn't enabled — those deployments have no 401 problem.
  const [authProbeDone, setAuthProbeDone] = useState(!config.hasBuiltInLogin);

  const initExtensions = useCallback(() => {
    loadExtensions().then((manifests) => {
      setExtensions(manifests);
      setExtensionsLoaded(true);
    });
  }, []);

  useEffect(() => {
    if (config.hasBuiltInLogin) {
      // Keep the 401 interceptor as the fallback for session-expired scenarios
      // mid-session; the cold-boot path no longer relies on it.
      setOnUnauthorized(() => setNeedsLogin(true));

      getAuthStatus()
        .then((s) => {
          if (s.authenticated) {
            initExtensions();
          } else {
            setNeedsLogin(true);
          }
        })
        .catch(() => {
          // Probe failed (network, server down). Treat as unauthenticated so the
          // login page renders; the user can retry from there.
          setNeedsLogin(true);
        })
        .finally(() => setAuthProbeDone(true));
    } else {
      initExtensions();
    }

    return () => extensionRuntime.stop();
  }, [initExtensions]);

  const handleLogin = useCallback(() => {
    setNeedsLogin(false);
    // Now authenticated — load extensions. MainLayout's mount-effect re-runs getAddons()
    // and drives both nav-visibility and connectIfEnabled, so we don't duplicate the
    // request here.
    initExtensions();
  }, [initExtensions]);

  const extensionPages = extensionsLoaded ? extensionRuntime.getPages() : [];

  const body = () => {
    if (!authProbeDone) {
      return null;
    }
    if (needsLogin) {
      return <LoginPage onLogin={handleLogin} />;
    }
    if (!extensionsLoaded) {
      return null;
    }

    return (
      <BrowserRouter basename={config.basePath}>
        <Routes>
          <Route element={<MainLayout extensions={extensions} />}>
            <Route index element={<DashboardPage />} />
            <Route path="/detail/:id" element={<DetailPage />} />
            <Route path="/jobs/detail/:id" element={<DetailPage />} />
            <Route path="/jobs/by-type/:type" element={<JobsByTypePage />} />
            <Route path="/jobs/:state" element={<JobListPage />} />
            <Route path="/messages/detail/:id" element={<DetailPage />} />
            <Route path="/messages/:state" element={<MessagesPage />} />
            <Route path="/batches/detail/:id" element={<DetailPage />} />
            <Route path="/batches/:state" element={<BatchesPage />} />
            <Route path="/recurring/:id" element={<RecurringDetailPage />} />
            <Route path="/recurring" element={<RecurringPage />} />
            <Route path="/trace/:traceId/:highlightId?" element={<TracePage />} />
            <Route path="/workers/:id" element={<WorkerDetailPage />} />
            {/* Server-instance detail keeps its own route (worker groups / pause live here); the
                Applications list links server instances straight to it. */}
            <Route path="/servers/:id" element={<ServerDetailPage />} />
            {/* Old /servers bookmarks land on the renamed Applications page. */}
            <Route path="/servers" element={<Navigate to="/applications" replace />} />
            <Route path="/applications/:id/instances/:instanceId" element={<ApplicationInstanceDetailPage />} />
            <Route path="/applications/:id" element={<ApplicationDetailPage />} />
            <Route path="/applications" element={<ApplicationsPage />} />
            <Route path="/counters" element={<CountersPage />} />
            <Route path="/queues" element={<QueuesPage />} />
            <Route path="/concurrency" element={<ConcurrencyLimitsPage />} />
            <Route path="/ratelimits" element={<RateLimitsPage />} />
            <Route path="/sagas/:id" element={<SagaDetailPage />} />
            <Route path="/sagas" element={<SagasListPage />} />
            <Route path="/services/:name" element={<BackgroundServiceDetail />} />
            <Route path="/services" element={<BackgroundServicesList />} />
            <Route path="/adapters/:name/calls/:callId" element={<AdapterCallDetailPage />} />
            <Route path="/adapters/:name" element={<AdapterDetailPage />} />
            <Route path="/adapters" element={<AdaptersPage />} />
            <Route path="/endpoints/:id/calls/:callId" element={<EndpointCallDetailPage />} />
            <Route path="/endpoints/:id" element={<EndpointDetailPage />} />
            <Route path="/endpoints" element={<EndpointsPage />} />
            <Route path="/client" element={<ClientPage />} />
            <Route path="/client/sessions/:id" element={<ClientSessionPage />} />
            <Route path="/client/events/:id" element={<ClientEventDetailPage />} />
            <Route path="/issues/:fingerprint" element={<IssueDetailPage />} />
            <Route path="/issues" element={<IssuesPage />} />
            <Route path="/slo/:id" element={<SloDetailPage />} />
            <Route path="/slo" element={<SloPage />} />
            <Route path="/webhooks/group/:dim/:key" element={<WebhookGroupDetailPage />} />
            <Route path="/webhooks/:id" element={<WebhookDetailPage />} />
            <Route path="/webhooks" element={<WebhooksPage />} />

            {/* Extension pages */}
            {extensionPages.map((page) => (
              <Route
                key={page.path}
                path={page.path}
                element={<ExtensionPage component={page.component} />}
              />
            ))}
          </Route>
        </Routes>
      </BrowserRouter>
    );
  };

  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <Suspense fallback={null}>{body()}</Suspense>
        <Toaster position="bottom-right" richColors closeButton />
      </QueryClientProvider>
    </ErrorBoundary>
  );
}

export default App;
