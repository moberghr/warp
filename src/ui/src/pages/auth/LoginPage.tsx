import { useState, useMemo } from 'react';
import axios from 'axios';
import { ArrowRight, Zap, Sun, Moon } from 'lucide-react';
import api from '@/api/client';
import { useTheme } from '@/hooks/useTheme';

// Mulberry32 seeded PRNG — deterministic so streak positions stay stable
// across re-renders without persisting state.
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

function BackdropStreaks() {
  const streaks = useMemo(() => {
    const r = seeded(57);
    return Array.from({ length: 24 }, () => {
      const accent = r() > 0.78;
      return {
        top: r() * 100,
        len: 120 + r() * 360,
        delay: -r() * 14,
        dur: 7 + r() * 9,
        opacity: 0.05 + r() * 0.12,
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
            background: `linear-gradient(90deg, transparent, ${s.accent ? '#4338CA' : '#1F1708'} 60%, transparent)`,
            animation: `soft-streak ${s.dur}s linear ${s.delay}s infinite`,
          }}
        />
      ))}
    </div>
  );
}

function Supergraphic() {
  return (
    <div
      aria-hidden
      className="pointer-events-none absolute inset-0 flex items-center justify-center select-none"
    >
      <span
        style={{
          fontSize: 360,
          fontWeight: 700,
          letterSpacing: -16,
          lineHeight: 0.85,
          color: 'transparent',
          WebkitTextStroke: '1px rgba(31,23,8,0.07)',
          fontFamily: 'var(--font-sans)',
        }}
      >
        WARP
      </span>
    </div>
  );
}

export default function LoginPage({ onLogin }: { onLogin: () => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [usernameFocused, setUsernameFocused] = useState(false);
  const [passwordFocused, setPasswordFocused] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const { theme, toggle: toggleTheme } = useTheme();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setLoading(true);

    try {
      const formData = new FormData();
      formData.append('username', username);
      formData.append('password', password);

      await api.post('/auth/login', formData);
      onLogin();
    } catch (e) {
      if (axios.isAxiosError(e) && e.response?.status === 401) {
        setError('Invalid username or password.');
      } else if (axios.isAxiosError(e) && !e.response) {
        setError("Can't reach Warp API — is the backend running?");
      } else {
        setError('Login failed. Please try again.');
      }
    } finally {
      setLoading(false);
    }
  };

  const buildDate = import.meta.env.VITE_APP_BUILD_DATE;
  const commit = import.meta.env.VITE_APP_COMMIT;
  const versionLeft = buildDate ? `build ${buildDate}` : 'dev build';
  const versionRight = commit ? commit : null;

  return (
    <div
      className="relative min-h-screen w-full overflow-hidden bg-background text-foreground"
      style={{
        backgroundImage:
          theme === 'dark'
            ? 'linear-gradient(135deg, var(--panel) 0%, var(--background) 55%, var(--panel) 100%)'
            : 'linear-gradient(135deg, #FBF7F0 0%, #FFFFFF 55%, #FBF4EC 100%)',
      }}
    >
      {/* Warm radial accent washes */}
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0"
        style={{
          background:
            'radial-gradient(70% 60% at 10% 0%, rgba(67,56,202,0.08), transparent 60%), radial-gradient(60% 60% at 100% 100%, rgba(124,58,237,0.04), transparent 60%)',
        }}
      />

      <Supergraphic />
      <BackdropStreaks />

      {/* Theme toggle (top-right) */}
      <button
        type="button"
        onClick={toggleTheme}
        className="absolute right-4 top-4 z-20 inline-flex h-9 w-9 items-center justify-center rounded-md text-text-dim hover:bg-paper transition-colors"
        aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
        title={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
      >
        {theme === 'dark' ? (
          <Sun className="h-4 w-4" />
        ) : (
          <Moon className="h-4 w-4" />
        )}
      </button>

      {/* Card */}
      <main className="relative z-10 flex min-h-screen items-center justify-center px-6 py-12">
        <div
          className="w-full max-w-[400px] rounded-2xl bg-panel"
          style={{
            border: '1px solid var(--border)',
            padding: '36px 36px 32px',
            boxShadow:
              theme === 'dark'
                ? '0 1px 2px rgba(0,0,0,0.4), 0 24px 60px rgba(0,0,0,0.5), 0 8px 22px rgba(0,0,0,0.35)'
                : '0 1px 2px rgba(40,28,10,0.04), 0 24px 60px rgba(40,28,10,0.10), 0 8px 22px rgba(40,28,10,0.06)',
          }}
        >
          {/* Brand row */}
          <div className="flex items-center gap-3">
            <div
              className="flex h-[34px] w-[34px] items-center justify-center rounded-[10px] text-white shrink-0"
              style={{
                background:
                  'linear-gradient(135deg, var(--brand-bright), var(--brand))',
                boxShadow:
                  '0 6px 18px var(--brand-soft), inset 0 1px 0 rgba(255,255,255,0.4)',
              }}
            >
              <Zap className="h-[17px] w-[17px]" strokeWidth={2.4} />
            </div>
            <div className="leading-tight">
              <div
                className="font-bold tracking-tight text-foreground"
                style={{ fontSize: 18 }}
              >
                WARP
              </div>
              <div className="mt-1 mono text-[10px] font-semibold uppercase tracking-[0.16em] text-text-mute">
                Job Engine
              </div>
            </div>
          </div>

          {/* Eyebrow */}
          <div className="mt-7 mb-3 inline-flex items-center gap-2 mono text-[10.5px] font-semibold uppercase tracking-[0.16em] text-text-mute">
            <span className="h-px w-[18px] bg-brand" aria-hidden />
            Sign in
          </div>

          {/* Title */}
          <h1
            className="m-0 font-semibold leading-[1.05] tracking-tight text-foreground"
            style={{ fontSize: 32, letterSpacing: '-1px' }}
          >
            Welcome back.
          </h1>

          {error && (
            <div className="mt-4 rounded-md border border-warp-red/30 bg-warp-red-soft px-3 py-2 text-sm text-warp-red">
              {error}
            </div>
          )}

          <form onSubmit={handleSubmit} className="mt-7 space-y-3.5">
            <div>
              <label
                htmlFor="username"
                className="block mb-1.5 mono text-[10.5px] font-semibold uppercase tracking-[0.12em] text-text-mute"
              >
                Username
              </label>
              <div
                className="relative flex items-center rounded-[10px] border bg-panel transition"
                style={{
                  borderColor: usernameFocused
                    ? 'var(--brand)'
                    : 'var(--border-hi)',
                  boxShadow: usernameFocused
                    ? '0 0 0 3px var(--brand-soft)'
                    : 'none',
                }}
              >
                <input
                  id="username"
                  className="block w-full bg-transparent px-3.5 py-3 text-[14px] text-foreground placeholder:text-ink-light outline-none"
                  placeholder="e.g. mreichl"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  onFocus={() => setUsernameFocused(true)}
                  onBlur={() => setUsernameFocused(false)}
                  autoComplete="username"
                  autoFocus
                />
              </div>
            </div>

            <div>
              <label
                htmlFor="password"
                className="block mb-1.5 mono text-[10.5px] font-semibold uppercase tracking-[0.12em] text-text-mute"
              >
                Password
              </label>
              <div
                className="relative flex items-center rounded-[10px] border bg-panel transition"
                style={{
                  borderColor: passwordFocused
                    ? 'var(--brand)'
                    : 'var(--border-hi)',
                  boxShadow: passwordFocused
                    ? '0 0 0 3px var(--brand-soft)'
                    : 'none',
                }}
              >
                <input
                  id="password"
                  type={showPassword ? 'text' : 'password'}
                  className="block w-full bg-transparent px-3.5 py-3 text-[14px] text-foreground placeholder:text-ink-light outline-none"
                  placeholder="••••••••••••"
                  style={{
                    fontFamily: showPassword ? undefined : 'var(--font-mono)',
                    letterSpacing: showPassword ? undefined : 2,
                  }}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  onFocus={() => setPasswordFocused(true)}
                  onBlur={() => setPasswordFocused(false)}
                  autoComplete="current-password"
                />
                <button
                  type="button"
                  onClick={(e) => {
                    e.preventDefault();
                    setShowPassword((v) => !v);
                  }}
                  className="h-full px-3 mono text-[11.5px] font-semibold tracking-[0.06em] text-text-dim hover:text-foreground"
                >
                  {showPassword ? 'HIDE' : 'SHOW'}
                </button>
              </div>
            </div>

            <button
              type="submit"
              disabled={loading}
              className="group inline-flex w-full items-center justify-center gap-2 rounded-[10px] py-3 text-[14px] font-semibold text-white disabled:opacity-60"
              style={{
                background:
                  'linear-gradient(180deg, var(--brand-bright), var(--brand))',
                border: '1px solid var(--brand)',
                boxShadow:
                  '0 6px 18px var(--brand-soft), inset 0 1px 0 rgba(255,255,255,0.3)',
              }}
            >
              {loading ? (
                'Signing in...'
              ) : (
                <>
                  Sign in
                  <ArrowRight
                    className="h-[14px] w-[14px] transition-transform group-hover:translate-x-0.5"
                    strokeWidth={2.4}
                  />
                </>
              )}
            </button>
          </form>
        </div>
      </main>

      {/* Footer (full-screen edges) */}
      <footer className="absolute bottom-5 left-6 right-6 z-10 flex items-center justify-between mono text-[11px] text-text-mute">
        <span>{versionLeft}</span>
        {versionRight && <span className="truncate max-w-[40%]">{versionRight}</span>}
      </footer>
    </div>
  );
}
