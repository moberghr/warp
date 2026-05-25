import { useState, useEffect, useCallback, lazy, Suspense } from 'react';
import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { ErrorBoundary } from 'react-error-boundary';
import MainLayout from '@/layouts/MainLayout';

const DashboardPage = lazy(() => import('@/pages/dashboard/DashboardPage'));
const JobListPage = lazy(() => import('@/pages/jobs/JobListPage'));
const MessagesPage = lazy(() => import('@/pages/messages/MessagesPage'));
const BatchesPage = lazy(() => import('@/pages/batches/BatchesPage'));
const RecurringPage = lazy(() => import('@/pages/recurring/RecurringPage'));
const RecurringDetailPage = lazy(() => import('@/pages/recurring/RecurringDetailPage'));
const ServersPage = lazy(() => import('@/pages/servers/ServersPage'));
const ServerDetailPage = lazy(() => import('@/pages/servers/ServerDetailPage'));
const CountersPage = lazy(() => import('@/pages/counters/CountersPage'));
const ConcurrencyLimitsPage = lazy(() => import('@/pages/concurrency/ConcurrencyLimitsPage'));
const RateLimitsPage = lazy(() => import('@/pages/ratelimits/RateLimitsPage'));
const SagasListPage = lazy(() => import('@/pages/sagas/SagasListPage'));
const SagaDetailPage = lazy(() => import('@/pages/sagas/SagaDetailPage'));
const BackgroundServicesList = lazy(() => import('@/pages/BackgroundServices/List'));
const BackgroundServiceDetail = lazy(() => import('@/pages/BackgroundServices/Detail'));
const WorkerDetailPage = lazy(() => import('@/pages/workers/WorkerDetailPage'));
const TracePage = lazy(() => import('@/pages/trace/TracePage'));
const DetailPage = lazy(() => import('@/pages/detail/DetailPage'));
const SettingsPage = lazy(() => import('@/pages/settings/SettingsPage'));
const LoginPage = lazy(() => import('@/pages/auth/LoginPage'));
const ExtensionPage = lazy(() => import('@/extensions/ExtensionPage'));
import { setOnUnauthorized } from '@/api/client';
import { loadExtensions } from '@/extensions/loader';
import { extensionRuntime } from '@/extensions/runtime';
import { getAuthStatus } from '@/api';
import { config } from '@/config';
import { queryClient } from '@/lib/queryClient';
import { Toaster } from '@/components/ui/sonner';
import { RootErrorFallback } from '@/components/RootErrorFallback';
import type { ExtensionManifest } from '@/extensions/types';

function AppRoutes() {
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
    // Now authenticated — load extensions. MainLayout's mount-effect re-runs
    // getAddons() and drives connectIfEnabled, so we don't duplicate it here.
    initExtensions();
  }, [initExtensions]);

  if (!authProbeDone) {
    return null;
  }

  if (needsLogin) {
    return (
      <Suspense fallback={null}>
        <LoginPage onLogin={handleLogin} />
      </Suspense>
    );
  }

  if (!extensionsLoaded) {
    return null;
  }

  const extensionPages = extensionRuntime.getPages();

  return (
    <BrowserRouter basename={config.basePath}>
      <Suspense fallback={null}>
        <Routes>
          <Route element={<MainLayout extensions={extensions} />}>
          <Route index element={<DashboardPage />} />
          <Route path="/detail/:id" element={<DetailPage />} />
          <Route path="/jobs/detail/:id" element={<DetailPage />} />
          <Route path="/jobs/:state" element={<JobListPage />} />
          <Route path="/messages/detail/:id" element={<DetailPage />} />
          <Route path="/messages/:state" element={<MessagesPage />} />
          <Route path="/messages" element={<MessagesPage />} />
          <Route path="/batches/detail/:id" element={<DetailPage />} />
          <Route path="/batches/:state" element={<BatchesPage />} />
          <Route path="/batches" element={<BatchesPage />} />
          <Route path="/recurring/:id" element={<RecurringDetailPage />} />
          <Route path="/recurring" element={<RecurringPage />} />
          <Route path="/trace/:traceId/:highlightId?" element={<TracePage />} />
          <Route path="/workers/:id" element={<WorkerDetailPage />} />
          <Route path="/servers/:id" element={<ServerDetailPage />} />
          <Route path="/servers" element={<ServersPage />} />
          <Route path="/counters" element={<CountersPage />} />
          <Route path="/concurrency" element={<ConcurrencyLimitsPage />} />
          <Route path="/ratelimits" element={<RateLimitsPage />} />
          <Route path="/sagas/:id" element={<SagaDetailPage />} />
          <Route path="/sagas" element={<SagasListPage />} />
          <Route path="/services/:name" element={<BackgroundServiceDetail />} />
          <Route path="/services" element={<BackgroundServicesList />} />
          <Route path="/settings" element={<SettingsPage />} />

          {extensionPages.map((page) => (
            <Route
              key={page.path}
              path={page.path}
              element={<ExtensionPage component={page.component} />}
            />
          ))}
          </Route>
        </Routes>
      </Suspense>
    </BrowserRouter>
  );
}

function App() {
  return (
    <ErrorBoundary FallbackComponent={RootErrorFallback}>
      <QueryClientProvider client={queryClient}>
        <AppRoutes />
        <Toaster />
      </QueryClientProvider>
    </ErrorBoundary>
  );
}

export default App;
