import { useState, useMemo } from 'react';
import { ArrowRight, Zap } from 'lucide-react';
import api from '@/api/client';

// Mulberry32 seeded PRNG — deterministic so streak positions stay stable
// across re-renders (avoids layout jank) without persisting state.
function seeded(seed: number) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function LoginStreaks() {
  const streaks = useMemo(() => {
    const r = seeded(57);
    return Array.from({ length: 22 }, () => {
      const accent = r() > 0.82;
      return {
        top: r() * 100,
        len: 90 + r() * 280,
        delay: -r() * 14,
        dur: 7 + r() * 9,
        opacity: 0.06 + r() * 0.14,
        accent,
      };
    });
  }, []);

  return (
    <div className="pointer-events-none absolute inset-0 overflow-hidden">
      {streaks.map((s, i) => (
        <div
          key={i}
          className="absolute h-px"
          style={{
            top: `${s.top}%`,
            left: 0,
            width: `${s.len}px`,
            opacity: s.opacity,
            background: `linear-gradient(90deg, transparent, ${s.accent ? '#22c55e' : '#18181b'} 60%, transparent)`,
            animation: `warp-streak ${s.dur}s linear ${s.delay}s infinite`,
          }}
        />
      ))}
    </div>
  );
}

export default function LoginPage({ onLogin }: { onLogin: () => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(false);
    setLoading(true);

    try {
      const formData = new FormData();
      formData.append('username', username);
      formData.append('password', password);

      await api.post('/auth/login', formData);
      onLogin();
    } catch {
      setError(true);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="min-h-screen w-full bg-white text-zinc-900 grid grid-cols-1 lg:grid-cols-2">
      {/* Left / brand pane */}
      <aside className="relative hidden lg:flex flex-col justify-between overflow-hidden p-10 bg-gradient-to-br from-zinc-50 via-white to-zinc-100">
        <LoginStreaks />
        <div className="flex items-center gap-3 z-10">
          <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-emerald-500 text-white shadow-sm">
            <Zap className="h-5 w-5" strokeWidth={2.5} />
          </div>
          <div className="leading-tight">
            <div className="text-sm font-semibold tracking-wide">WARP</div>
            <div className="text-[10px] font-medium uppercase tracking-[0.18em] text-zinc-500">Job Engine</div>
          </div>
        </div>

        <div className="pointer-events-none absolute inset-0 flex items-center justify-center select-none">
          <span
            className="text-[18rem] font-black leading-none tracking-tighter text-transparent"
            style={{
              WebkitTextStroke: '1px rgba(24,24,27,0.18)',
              filter: 'drop-shadow(0 0 24px rgba(34,197,94,0.25)) drop-shadow(0 0 60px rgba(34,197,94,0.12))',
            }}
          >
            WARP
          </span>
        </div>

      </aside>

      {/* Right / form pane */}
      <main className="relative flex flex-col">
        <div className="flex flex-1 items-center justify-center px-6 py-12 sm:px-10">
          <div className="w-full max-w-sm">
            <div className="mb-2 flex items-center gap-2 text-[11px] font-semibold uppercase tracking-[0.2em] text-emerald-600">
              <span className="h-px w-6 bg-emerald-500" />
              Sign in
            </div>
            <h1 className="text-3xl font-semibold tracking-tight text-zinc-900 sm:text-4xl">Welcome back.</h1>

            {error && (
              <div className="mt-4 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                Invalid username or password.
              </div>
            )}

            <form onSubmit={handleSubmit} className="mt-8 space-y-5">
              <div>
                <label htmlFor="username" className="text-[10px] font-semibold uppercase tracking-[0.18em] text-zinc-500">
                  Username
                </label>
                <input
                  id="username"
                  className="mt-1.5 block w-full rounded-md border border-zinc-200 bg-white px-3.5 py-2.5 text-sm text-zinc-900 placeholder:text-zinc-400 shadow-sm transition focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                  placeholder="e.g. mreichl"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  autoComplete="username"
                  autoFocus
                />
              </div>

              <div>
                <label htmlFor="password" className="text-[10px] font-semibold uppercase tracking-[0.18em] text-zinc-500">
                  Password
                </label>
                <div className="relative mt-1.5">
                  <input
                    id="password"
                    type={showPassword ? 'text' : 'password'}
                    className="block w-full rounded-md border border-zinc-200 bg-white px-3.5 py-2.5 pr-14 text-sm text-zinc-900 placeholder:text-zinc-400 shadow-sm transition focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                    placeholder="••••••••••••"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="current-password"
                  />
                  <button
                    type="button"
                    onClick={() => setShowPassword((v) => !v)}
                    className="absolute inset-y-0 right-3 my-auto text-[10px] font-semibold uppercase tracking-[0.18em] text-zinc-500 hover:text-zinc-900"
                  >
                    {showPassword ? 'Hide' : 'Show'}
                  </button>
                </div>
              </div>

              <button
                type="submit"
                disabled={loading}
                className="group inline-flex w-full items-center justify-center gap-2 rounded-md bg-emerald-600 px-4 py-3 text-sm font-semibold text-white shadow-sm transition hover:bg-emerald-700 focus:outline-none focus:ring-2 focus:ring-emerald-500/40 disabled:opacity-60"
              >
                {loading ? 'Signing in...' : (
                  <>
                    Sign in
                    <ArrowRight className="h-4 w-4 transition-transform group-hover:translate-x-0.5" />
                  </>
                )}
              </button>
            </form>
          </div>
        </div>

        <footer className="flex items-center justify-between px-6 pb-6 text-[10px] font-medium uppercase tracking-[0.18em] text-zinc-400 sm:px-10">
          <span>warp v{import.meta.env.VITE_APP_VERSION}</span>
          <span>build {import.meta.env.VITE_APP_BUILD_DATE} · {import.meta.env.VITE_APP_COMMIT}</span>
        </footer>
      </main>
    </div>
  );
}
