import { lazy, Suspense, useState, useEffect, useCallback } from 'react';
import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { Toaster } from 'sonner';
import { queryClient } from '@/lib/queryClient';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import MainLayout from '@/layouts/MainLayout';
import ExtensionPage from '@/extensions/ExtensionPage';
import { appRoutes } from '@/appRoutes';

// Route pages are code-split: each becomes its own chunk loaded on navigation,
// so the initial bundle no longer carries every page (notably the trace graph's
// @xyflow/@dagrejs and the chart stack). MainLayout renders the Suspense boundary
// around <Outlet>, so the shell/nav stays put while a page chunk loads.
const DashboardPage = lazy(() => import('@/pages/dashboard/DashboardPage'));
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
            {appRoutes.map((route) => (
              <Route key={route.path} path={route.path} element={route.element} />
            ))}

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
