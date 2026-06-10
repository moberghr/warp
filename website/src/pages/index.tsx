import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';
import styles from './index.module.css';

// ─── Primitives ──────────────────────────────────────────────────────────────

function Terminal({ label, lines }: { label?: string; lines: string }) {
  return (
    <div className={styles.terminal}>
      <div className={styles.terminalBar}>
        <i /><i /><i />
        {label && <span className={styles.terminalLabel}>{label}</span>}
      </div>
      <pre
        className={styles.terminalPre}
        dangerouslySetInnerHTML={{ __html: lines }}
      />
    </div>
  );
}

function SectionHead({ num, label }: { num: string; label: string }) {
  return (
    <div className={styles.secHead}>
      <span className={styles.secNum}>{num}</span>
      {label}
    </div>
  );
}

function GitHubIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M12 .5C5.73.5.75 5.48.75 11.75c0 5 3.24 9.24 7.73 10.74.57.1.78-.25.78-.55v-1.93c-3.14.68-3.81-1.51-3.81-1.51-.51-1.3-1.25-1.65-1.25-1.65-1.02-.7.08-.68.08-.68 1.13.08 1.72 1.16 1.72 1.16 1 1.72 2.63 1.22 3.27.93.1-.73.39-1.22.71-1.5-2.51-.29-5.15-1.25-5.15-5.57 0-1.23.44-2.23 1.16-3.02-.12-.29-.5-1.43.11-2.98 0 0 .95-.3 3.1 1.15a10.8 10.8 0 0 1 5.64 0c2.15-1.45 3.1-1.15 3.1-1.15.61 1.55.23 2.69.11 2.98.72.79 1.16 1.79 1.16 3.02 0 4.33-2.64 5.28-5.16 5.56.4.35.76 1.04.76 2.1v3.11c0 .3.2.66.79.55 4.49-1.5 7.73-5.74 7.73-10.74C23.25 5.48 18.27.5 12 .5Z" />
    </svg>
  );
}

// ─── Content ──────────────────────────────────────────────────────────────────

const HERO_SETUP = `<span class="k-dim">// Register with your existing DbContext</span>
<span class="k-blue">builder</span>.Services.<span class="k-green">AddWarp</span>&lt;AppDbContext&gt;(opt =&gt; {
    opt.<span class="k-green">UsePostgreSql</span>();
});

<span class="k-blue">builder</span>.Services.<span class="k-green">AddWarpServer</span>&lt;AppDbContext&gt;(opt =&gt; {
    opt.<span class="k-green">UsePostgreSql</span>();
    opt.WorkerCount = <span class="k-yellow">10</span>;
    opt.<span class="k-green">AddRetry</span>();
    opt.<span class="k-green">AddMutex</span>();
});

app.<span class="k-green">UseWarpUI</span>(); <span class="k-dim">// dashboard at /warp</span>`;

const HERO_USAGE = `<span class="k-dim">// Pub/sub — every handler becomes a job</span>
<span class="k-blue">await</span> publisher.<span class="k-green">Publish</span>(<span class="k-blue">new</span> <span class="k-purple">OrderCreated</span> { Id = orderId });
<span class="k-blue">await</span> publisher.<span class="k-green">SaveChangesAsync</span>(ct);

<span class="k-dim">// Job — single handler, retries, scheduling</span>
<span class="k-blue">await</span> publisher.<span class="k-green">Enqueue</span>(<span class="k-blue">new</span> <span class="k-purple">GenerateReport</span> { UserId = id });

<span class="k-dim">// Request — in-memory, typed response</span>
<span class="k-blue">var</span> user = <span class="k-blue">await</span> mediator.<span class="k-green">Send</span>(<span class="k-blue">new</span> <span class="k-purple">GetUser</span> { Id = id });`;

const INSTALL_PACKAGES = `<span class="k-dim">$</span> dotnet add package Moberg.Warp.Core
<span class="k-dim">$</span> dotnet add package Moberg.Warp.Provider.PostgreSql
<span class="k-dim">$</span> dotnet add package Moberg.Warp.Worker
<span class="k-dim">$</span> dotnet add package Moberg.Warp.UI

<span class="k-green">✓</span> DbContext auto-configured — no manual setup
<span class="k-green">✓</span> Outbox publisher registered (same transaction)
<span class="k-green">✓</span> Worker service ready, dashboard at /warp`;

// ─── Sections ────────────────────────────────────────────────────────────────

function Hero() {
  return (
    <section className={styles.hero}>
      <div className="container">
        <div className={styles.heroGrid}>
          <div className={styles.heroLeft}>
            <div className={styles.eyebrow}>
              <span className={styles.eyebrowDot} />
              Distributed job processing for .NET 10
            </div>
            <h1 className={styles.heroTitle}>
              Reliable background processing<br />
              for .NET production systems.
            </h1>
            <p className={styles.heroSub}>
              Warp is a distributed job processing and message queue for .NET&nbsp;10.
              Pub/sub messaging, orchestrated background jobs, and in-memory request
              dispatch — integrated with your existing EF&nbsp;Core DbContext using
              the transactional outbox pattern.
            </p>
            <div className={styles.heroActions}>
              <Link className={styles.btnPrimary} to="/docs/getting-started">
                View Documentation
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
                  <path d="M5 12h14M13 6l6 6-6 6" />
                </svg>
              </Link>
              <Link className={styles.btnSecondary} href="https://github.com/moberghr/warp">
                <GitHubIcon />
                GitHub
              </Link>
            </div>
            <div className={styles.heroStats}>
              <span>PostgreSQL</span>
              <span>SQL Server</span>
              <span>.NET 10</span>
              <span>MIT licensed</span>
              <span>1,024 tests</span>
            </div>
          </div>

          <div className={styles.heroRight}>
            <Terminal label="Program.cs" lines={HERO_SETUP} />
            <Terminal label="OrderService.cs" lines={HERO_USAGE} />
          </div>
        </div>
      </div>
    </section>
  );
}

function Marquee() {
  const items = [
    'PostgreSQL', 'SQL Server', '.NET 10', 'EF Core', 'ASP.NET Core',
    'Outbox Pattern', 'Pub/Sub', 'Worker Services', 'Distributed Locks',
    'Retries & Backoff', 'Cron Scheduling', 'Live Dashboard',
    'PostgreSQL', 'SQL Server', '.NET 10', 'EF Core', 'ASP.NET Core',
    'Outbox Pattern', 'Pub/Sub', 'Worker Services', 'Distributed Locks',
    'Retries & Backoff', 'Cron Scheduling', 'Live Dashboard',
  ];
  return (
    <div className={styles.marqueeWrap}>
      <div className="container">
        <div className={styles.marqueeInner}>
          <span className={styles.marqueeLabel}>Built for</span>
          <div className={styles.marqueeViewport}>
            <div className={styles.marqueeTrack}>
              {items.map((item, i) => <span key={i}>{item}</span>)}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

const PATTERNS = [
  {
    num: '01',
    name: 'Messages',
    iface: 'IMessage',
    desc: 'Pub/sub queue. Multiple handlers per message — each becomes an independent job. Fan out to any number of subscribers in the same transaction.',
    code: 'publisher.Publish(\n  new OrderCreated { Id = orderId });',
  },
  {
    num: '02',
    name: 'Jobs',
    iface: 'IJob',
    desc: 'Orchestrated background work. Single handler with scheduling, configurable retries, continuations, batches, named queues, and mutex control.',
    code: 'publisher.Enqueue(\n  new GenerateReport(),\n  queue: "reports");',
  },
  {
    num: '03',
    name: 'Requests',
    iface: 'IRequest<T>',
    desc: 'In-memory request/response. Single handler, no persistence, returns a typed response immediately. Goes through the same pipeline behavior chain.',
    code: 'var user = await mediator\n  .Send(new GetUser { Id = id });',
  },
  {
    num: '04',
    name: 'Streams',
    iface: 'IStreamRequest<T>',
    desc: 'In-memory streaming. Returns IAsyncEnumerable<T> via CreateStream(). Request-level and enumeration-level pipeline behaviors, no persistence.',
    code: 'await foreach (var item in\n  mediator.CreateStream(\n    new GetItems()));',
  },
];

function Patterns() {
  return (
    <section className={styles.section} id="patterns">
      <div className="container">
        <SectionHead num="01" label="Four patterns" />
        <h2 className={styles.h2}>Four processing patterns. One unified library.</h2>
        <p className={styles.lede}>
          Messages for fan-out delivery, Jobs for durable orchestrated execution,
          Requests for in-process query dispatch, and Streams for asynchronous
          sequences — all running through a shared pipeline, dashboard, and worker runtime.
        </p>
        <div className={styles.patternsGrid}>
          {PATTERNS.map((p) => (
            <div key={p.num} className={styles.patternCard}>
              <span className={styles.patternNum}>{p.num}</span>
              <h3 className={styles.patternName}>{p.name}</h3>
              <code className={styles.patternIface}>{p.iface}</code>
              <p className={styles.patternDesc}>{p.desc}</p>
              <div className={styles.patternCode}>
                <pre>{p.code}</pre>
              </div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

const FEATURES = [
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/></svg>,
    title: 'Outbox pattern built in',
    desc: 'Jobs and messages are created inside your EF Core transaction. Eliminates lost events and dual-write inconsistencies. The DbContext transaction is the consistency boundary.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>,
    title: 'Real-time dashboard',
    desc: 'Built-in operations dashboard with live throughput graphs, job detail view, exception traces, handler log output, batch progress, and recurring job history — served at /warp with no additional configuration.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M12 2v4M12 18v4M4.93 4.93l2.83 2.83M16.24 16.24l2.83 2.83M2 12h4M18 12h4M4.93 19.07l2.83-2.83M16.24 7.76l2.83-2.83"/></svg>,
    title: 'Crash recovery',
    desc: 'Per-job keep-alive heartbeat with automatic requeue on worker crash. Configurable sliding invisibility timeout prevents duplicate execution.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M3 12h4l3-9 4 18 3-9h4"/></svg>,
    title: 'Pipeline behaviors',
    desc: 'Middleware chain applied to every handler. Retry, mutex, logging, metrics, and authorization concerns are registered once and applied uniformly across all jobs, messages, and requests.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><circle cx="12" cy="12" r="10"/><path d="M12 8v4l3 3"/></svg>,
    title: 'Cron scheduling',
    desc: 'Recurring jobs defined with cron expressions. The registration API is idempotent and safe to call on every application start. The scheduler creates jobs at the configured time.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 2L3 7v6c0 5 4 9 9 10 5-1 9-5 9-10V7l-9-5z"/></svg>,
    title: 'Distributed mutex',
    desc: 'Opt-in concurrency control via [Mutex("key")] attribute or .WithMutex("key") at publish time. Enforces single-execution per key across all worker instances.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2z"/><path d="M22 6l-10 7L2 6"/></svg>,
    title: 'DB push notifications',
    desc: 'Opt-in push wake-up via PostgreSQL LISTEN/NOTIFY or SQL Server Service Broker. Workers receive push notifications for new jobs, eliminating unnecessary polling latency.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M21 12c0 1.66-4.03 3-9 3S3 13.66 3 12"/><path d="M3 5v14c0 1.66 4.03 3 9 3s9-1.34 9-3V5"/></svg>,
    title: 'Two databases, one API',
    desc: 'PostgreSQL and SQL Server supported out of the box. Provider packages handle all provider-specific configuration — call opt.UsePostgreSql() or opt.UseSqlServer() to switch.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/></svg>,
    title: 'Batches & continuations',
    desc: 'Group related jobs into batches with configurable child activation on failure. Chain continuation jobs that execute after all batch members reach a terminal state.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M18 20V10M12 20V4M6 20v-6"/></svg>,
    title: 'OpenTelemetry built in',
    desc: 'Native OTel integration with Activity traces, four metrics instruments (duration, active, completed, enqueued), and span attributes conforming to OTEL messaging semantic conventions.',
  },
  {
    icon: <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M18 8h1a4 4 0 0 1 0 8h-1"/><path d="M2 8h16v9a4 4 0 0 1-4 4H6a4 4 0 0 1-4-4V8z"/><line x1="6" y1="1" x2="6" y2="4"/><line x1="10" y1="1" x2="10" y2="4"/><line x1="14" y1="1" x2="14" y2="4"/></svg>,
    title: 'Circuit breaker',
    desc: 'Opt-in circuit breaker prevents repeated calls to a failing downstream dependency. Closed → Open → HalfOpen state machine with configurable thresholds. Jobs are rescheduled and retain their retry budget.',
  },
];

function Features() {
  return (
    <section className={`${styles.section} ${styles.sectionAlt}`} id="features">
      <div className="container">
        <SectionHead num="02" label="Capabilities" />
        <h2 className={styles.h2}>Comprehensive background processing capabilities.</h2>
        <p className={styles.lede}>
          Warp addresses the full range of background processing requirements for
          production .NET applications, with no additional infrastructure or manual
          configuration.
        </p>
        <div className={styles.featuresGrid}>
          {FEATURES.map((f, i) => (
            <div key={i} className={styles.featureCard}>
              <div className={styles.featureIcon}>{f.icon}</div>
              <h3 className={styles.featureTitle}>{f.title}</h3>
              <p className={styles.featureText}>{f.desc}</p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function Dashboard() {
  return (
    <section className={styles.section} id="dashboard">
      <div className="container">
        <SectionHead num="03" label="Dashboard" />
        <h2 className={styles.h2}>Operational visibility into your job processing pipeline.</h2>
        <p className={styles.lede}>
          Built-in operations dashboard with live throughput graphs, job detail view,
          exception traces, handler log output, batch progress bars, worker health status,
          and recurring job history. Served at <code>/warp</code> with no additional configuration.
        </p>
        <div className={styles.screenshotWrap}>
          <img
            src="/warp/img/screenshots/01-dashboard.png"
            alt="Warp dashboard — job overview with live graphs"
            className={styles.screenshot}
            data-theme-target="light"
          />
          <img
            src="/warp/img/screenshots/01-dashboard-dark.png"
            alt="Warp dashboard — dark mode"
            className={styles.screenshot}
            data-theme-target="dark"
          />
        </div>
      </div>
    </section>
  );
}

function Install() {
  return (
    <section className={`${styles.section} ${styles.sectionAlt}`} id="install">
      <div className="container">
        <SectionHead num="04" label="Get started" />
        <h2 className={styles.h2}>Minimal setup. No infrastructure changes required.</h2>
        <p className={styles.lede}>
          Register your existing DbContext as usual — Warp wraps it automatically.
          No manual schema migrations, no special context configuration, and no
          secondary registration.
        </p>
        <div className={styles.installGrid}>
          <div>
            <div className={styles.installHead}>
              <span className={styles.installLabel}>1 · Add packages</span>
              <span className={styles.installMeta}>dotnet CLI</span>
            </div>
            <Terminal lines={INSTALL_PACKAGES} />
          </div>
          <div>
            <div className={styles.installHead}>
              <span className={styles.installLabel}>2 · Register &amp; publish</span>
              <span className={styles.installMeta}>Program.cs → OrderService.cs</span>
            </div>
            <Terminal label="Program.cs" lines={HERO_SETUP} />
          </div>
        </div>
        <div className={styles.installActions}>
          <Link className={styles.btnPrimary} to="/docs/getting-started">
            Read the docs →
          </Link>
          <Link className={styles.btnSecondary} href="https://github.com/moberghr/warp">
            <GitHubIcon />
            View on GitHub
          </Link>
        </div>
      </div>
    </section>
  );
}

function Callout() {
  return (
    <section className={styles.section}>
      <div className="container">
        <div className={styles.callout}>
          <h2>A structured approach to background processing.</h2>
          <p>
            Warp provides a structured, production-grade job processor built on your
            existing EF Core stack. Consistent patterns for messaging, scheduling,
            retries, and observability — without introducing additional infrastructure
            dependencies.
          </p>
          <div className={styles.calloutActions}>
            <Link className={styles.calloutBtnPrimary} to="/docs/getting-started">
              View Documentation
            </Link>
            <Link className={styles.calloutBtnSecondary} href="https://github.com/moberghr/warp">
              View on GitHub
            </Link>
          </div>
        </div>
      </div>
    </section>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export default function Home(): JSX.Element {
  return (
    <Layout description="Distributed job processing and message queue for .NET 10. Integrated with EF Core — pub/sub messaging, orchestrated background jobs, configurable retries, cron scheduling, and a real-time operations dashboard.">
      <main>
        <Hero />
        <Marquee />
        <Patterns />
        <Features />
        <Dashboard />
        <Install />
        <Callout />
      </main>
    </Layout>
  );
}
