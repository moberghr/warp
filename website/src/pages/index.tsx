import type { ReactNode } from "react";
import Layout from "@theme/Layout";
import Link from "@docusaurus/Link";
import CodeBlock from "@theme/CodeBlock";
import useBaseUrl from "@docusaurus/useBaseUrl";
import styles from "./index.module.css";

// ─── Primitives ──────────────────────────────────────────────────────────────

// A dashboard capture in a browser frame. Both themes are rendered and CSS shows one
// (custom.css `img[data-theme-target]`), so the right image is in the server-rendered HTML.
function Shot({
  name,
  alt,
  eager,
}: {
  name: string;
  alt: string;
  eager?: boolean;
}) {
  const light = useBaseUrl(`/img/showcase/${name}-light.png`);
  const dark = useBaseUrl(`/img/showcase/${name}-dark.png`);
  const loading = eager ? "eager" : "lazy";

  return (
    <div className={styles.frame}>
      <div className={styles.frameBar} aria-hidden="true">
        <i />
        <i />
        <i />
        <span>localhost:5000/warp</span>
      </div>
      <img
        src={light}
        alt={alt}
        width={2880}
        height={1800}
        loading={loading}
        data-theme-target="light"
      />
      <img
        src={dark}
        alt={alt}
        width={2880}
        height={1800}
        loading={loading}
        data-theme-target="dark"
      />
    </div>
  );
}

function GitHubIcon() {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 24 24"
      fill="currentColor"
      aria-hidden="true"
    >
      <path d="M12 .5C5.73.5.75 5.48.75 11.75c0 5 3.24 9.24 7.73 10.74.57.1.78-.25.78-.55v-1.93c-3.14.68-3.81-1.51-3.81-1.51-.51-1.3-1.25-1.65-1.25-1.65-1.02-.7.08-.68.08-.68 1.13.08 1.72 1.16 1.72 1.16 1 1.72 2.63 1.22 3.27.93.1-.73.39-1.22.71-1.5-2.51-.29-5.15-1.25-5.15-5.57 0-1.23.44-2.23 1.16-3.02-.12-.29-.5-1.43.11-2.98 0 0 .95-.3 3.1 1.15a10.8 10.8 0 0 1 5.64 0c2.15-1.45 3.1-1.15 3.1-1.15.61 1.55.23 2.69.11 2.98.72.79 1.16 1.79 1.16 3.02 0 4.33-2.64 5.28-5.16 5.56.4.35.76 1.04.76 2.1v3.11c0 .3.2.66.79.55 4.49-1.5 7.73-5.74 7.73-10.74C23.25 5.48 18.27.5 12 .5Z" />
    </svg>
  );
}

// ─── Content ──────────────────────────────────────────────────────────────────

const OUTBOX_CODE = `public async Task PlaceOrder(Order order, CancellationToken ct)
{
    db.Orders.Add(order);

    // Saved in the same transaction as the order.
    await publisher.Enqueue(
        new SendConfirmationEmail { OrderId = order.Id });

    await db.SaveChangesAsync(ct);
}

public class SendConfirmationEmailHandler(IEmailSender email)
    : IJobHandler<SendConfirmationEmail>
{
    public Task HandleAsync(
        SendConfirmationEmail job, CancellationToken ct) =>
        email.SendConfirmation(job.OrderId, ct);
}`;

const PATTERNS_CODE = `// Job: one handler, stored, retried
await publisher.Enqueue(new GenerateReport { Month = 3 });
await publisher.Schedule(new GenerateReport { Month = 4 }, tomorrow);

// Message: every handler runs as its own job
await publisher.Publish(new OrderCreated { OrderId = order.Id });

// Request: in-process, typed response, no database
var user = await mediator.Send(new GetUser { UserId = 1 });

// Stream: in-process IAsyncEnumerable
await foreach (var item in mediator.CreateStream(new GetUsers { Role = "Admin" })) { }`;

const INSTALL_CODE = `dotnet add package Moberg.Warp.Core
dotnet add package Moberg.Warp.Worker
dotnet add package Moberg.Warp.Provider.PostgreSql   # or .SqlServer
dotnet add package Moberg.Warp.Dashboard`;

const REGISTER_CODE = `builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();      // or opt.UseSqlServer()
    opt.AddRetry(r => r.MaxRetries = 3);
});

var app = builder.Build();
app.MapWarpDashboard("/warp");
app.Run();`;

const MIGRATE_CODE = `dotnet ef migrations add AddWarp
dotnet ef database update`;

// ─── Sections ────────────────────────────────────────────────────────────────

function Hero() {
  return (
    <header className={styles.hero}>
      <div className={styles.wrap}>
        <div className={styles.heroGrid}>
          <div>
            <h1 className={styles.heroTitle}>
              Background jobs for .NET, in the database you already run.
            </h1>
            <p className={styles.heroSub}>
              Warp runs jobs, pub/sub messages and in-process requests on
              PostgreSQL or SQL&nbsp;Server through your own EF&nbsp;Core
              DbContext. A job is saved in the same transaction as the data it
              belongs to, and there is no broker to operate.
            </p>
            <div className={styles.actions}>
              <Link className={styles.btnPrimary} to="/docs/getting-started">
                Get started
              </Link>
              <Link className={styles.btnQuiet} to="/docs/why-warp">
                Why Warp
              </Link>
              <Link
                className={styles.btnQuiet}
                href="https://github.com/moberghr/warp"
              >
                <GitHubIcon />
                GitHub
              </Link>
            </div>
            <p className={styles.heroMeta}>
              .NET 10. PostgreSQL 17 and 18, SQL Server 2022 and 2025. MIT
              licensed.
            </p>
          </div>
          <div className={styles.heroCode}>
            <CodeBlock language="csharp" title="OrderService.cs">
              {OUTBOX_CODE}
            </CodeBlock>
          </div>
        </div>
        <div className={styles.heroShot}>
          <Shot
            name="dashboard"
            alt="The Warp dashboard: job counts by state and live throughput"
            eager
          />
        </div>
      </div>
    </header>
  );
}

const REASONS = [
  {
    title: "Jobs commit with your data",
    body: "Warp writes through your DbContext, so an order and the email job it needs are saved together or not at all. No outbox table to build, no dual write to reconcile.",
    to: "/docs/features/outbox-pattern",
    link: "How the outbox works",
  },
  {
    title: "Light on the database",
    body: "Jobs are claimed in batches and completed in batches. An empty job costs about 0.24 database statements, so the queue leaves the server to the rest of your application.",
    to: "/docs/performance",
    link: "Performance",
  },
  {
    title: "Nothing to register",
    body: "A source generator finds your handlers at build time and dispatches to them without reflection. Write the handler class and it runs.",
    to: "/docs/getting-started",
    link: "Getting started",
  },
];

function Reasons() {
  return (
    <section className={styles.section}>
      <div className={styles.wrap}>
        <div className={styles.reasons}>
          {REASONS.map((r) => (
            <div key={r.title}>
              <h2 className={styles.reasonTitle}>{r.title}</h2>
              <p className={styles.reasonBody}>{r.body}</p>
              <Link to={r.to}>{r.link}</Link>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

const TOUR: {
  shot: string;
  alt: string;
  title: string;
  body: ReactNode;
  to: string;
  link: string;
}[] = [
  {
    shot: "job-failed",
    alt: "A failed job: payload, metadata and the history of every attempt",
    title: "Every attempt, with the reason it failed",
    body: "A job page shows the payload, the metadata, and each state the job passed through with its duration and exception. Requeue or delete it from the same page.",
    to: "/docs/dashboard/jobs",
    link: "Jobs in the dashboard",
  },
  {
    shot: "job-running",
    alt: "A running job with reported progress and live handler output",
    title: "Handler logs while the job is still running",
    body: (
      <>
        What the handler writes to <code>ILogger</code> appears on the job page
        about a second later, next to the progress it reports. You do not need
        to wait for the job to finish, or go to another tool, to see what it is
        doing.
      </>
    ),
    to: "/docs/dashboard/jobs",
    link: "Live output and progress",
  },
  {
    shot: "trace",
    alt: "A trace from a browser request through an endpoint to the jobs it started",
    title: "One trace from the request to the last job",
    body: "Follow a checkout from the browser, through the endpoint, into the job it enqueued, the outbound call that job made and the jobs that followed. The job graph shows batches, messages and continuations.",
    to: "/docs/features/tracing",
    link: "Tracing across jobs",
  },
  {
    shot: "issues",
    alt: "Errors grouped into issues with event counts and status",
    title: "Four thousand failures, one issue",
    body: "Errors from jobs, endpoints, outbound calls and the browser are grouped by fingerprint. Mark an issue resolved and Warp flags it as regressed if it comes back.",
    to: "/docs/features/error-grouping",
    link: "Error grouping",
  },
];

function Tour() {
  return (
    <section className={styles.section}>
      <div className={styles.wrap}>
        <h2 className={styles.h2}>A dashboard that explains what happened</h2>
        <p className={styles.lede}>
          Served at <code>/warp</code> from your own application, behind your
          own authorization. These are captures of the dashboard running its
          demo data.
        </p>
        {TOUR.map((t) => (
          <div key={t.shot} className={styles.tourRow}>
            <div className={styles.tourText}>
              <h3 className={styles.h3}>{t.title}</h3>
              <p>{t.body}</p>
              <Link to={t.to}>{t.link}</Link>
            </div>
            <div className={styles.tourShot}>
              <Shot name={t.shot} alt={t.alt} />
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}

const PERF_BARS = [
  {
    title: "Drain 50,000 empty jobs",
    rows: [
      { name: "Warp", value: "7.2–10.6 s", pct: 4.3, warp: true },
      { name: "Hangfire", value: "228–244 s", pct: 100, warp: false },
    ],
  },
  {
    title: "Drain 20,000 jobs that read and update a row",
    rows: [
      { name: "Warp", value: "14.0–17.7 s", pct: 11.4, warp: true },
      { name: "Hangfire", value: "151–155 s", pct: 100, warp: false },
    ],
  },
];

const PERF_ROWS = [
  {
    metric: "Database statements per empty job",
    warp: "0.23–0.24",
    other: "42.8–43.0",
  },
  {
    metric: "PostgreSQL CPU per empty job",
    warp: "0.11–0.20 ms",
    other: "4.4–4.8 ms",
  },
  {
    metric: "Memory allocated per empty job",
    warp: "13.7 KB",
    other: "79.7 KB",
  },
];

function Performance() {
  return (
    <section className={`${styles.section} ${styles.tinted}`} id="performance">
      <div className={styles.wrap}>
        <h2 className={styles.h2}>
          The queue shares your database. It should not crowd it.
        </h2>
        <p className={styles.lede}>
          The same jobs on the same PostgreSQL server, Warp&nbsp;8.0 against
          Hangfire.
        </p>
        <div className={styles.perf}>
          <div>
            {PERF_BARS.map((group) => (
              <div key={group.title} className={styles.perfGroup}>
                <div className={styles.perfGroupTitle}>{group.title}</div>
                {group.rows.map((row) => (
                  <div
                    key={row.name}
                    className={`${styles.perfRow} ${row.warp ? styles.perfRowWarp : ""}`}
                  >
                    <span>{row.name}</span>
                    <span className={styles.perfTrack}>
                      <span
                        className={styles.perfFill}
                        style={{ width: `${row.pct}%` }}
                      />
                    </span>
                    <span className={styles.perfValue}>{row.value}</span>
                  </div>
                ))}
              </div>
            ))}
          </div>
          <table className={styles.perfTable}>
            <thead>
              <tr>
                <th scope="col">
                  <span className={styles.srOnly}>Measurement</span>
                </th>
                <th scope="col">Warp</th>
                <th scope="col">Hangfire</th>
              </tr>
            </thead>
            <tbody>
              {PERF_ROWS.map((r) => (
                <tr key={r.metric}>
                  <th scope="row">{r.metric}</th>
                  <td className={styles.perfWarpCell}>{r.warp}</td>
                  <td>{r.other}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className={styles.note}>
          Shorter is better. Bars show the slowest run of each range.
          Preliminary figures for the unreleased 8.0, measured in November 2026.{" "}
          <Link to="/docs/performance/warp-vs-hangfire">
            Every measured row, the method and the caveats
          </Link>
          .
        </p>
      </div>
    </section>
  );
}

const PATTERNS = [
  {
    name: "Jobs",
    iface: "IJob",
    desc: "One handler. Stored, retried, scheduled, grouped into batches.",
    to: "/docs/patterns/jobs",
  },
  {
    name: "Messages",
    iface: "IMessage",
    desc: "Any number of handlers. Each one runs as its own job.",
    to: "/docs/patterns/messages",
  },
  {
    name: "Requests",
    iface: "IRequest<T>",
    desc: "In-process, typed response. Nothing is stored.",
    to: "/docs/patterns/requests",
  },
  {
    name: "Streams",
    iface: "IStreamRequest<T>",
    desc: "In-process IAsyncEnumerable. Nothing is stored.",
    to: "/docs/patterns/requests#streams",
  },
];

function Patterns() {
  return (
    <section className={styles.section} id="patterns">
      <div className={styles.wrap}>
        <h2 className={styles.h2}>Four kinds of work, one handler model</h2>
        <p className={styles.lede}>
          Durable work and in-process calls share one pipeline, so a behavior
          you write once, such as logging or validation, wraps all four.
        </p>
        <div className={styles.patterns}>
          <dl className={styles.patternList}>
            {PATTERNS.map((p) => (
              <div key={p.name}>
                <dt>
                  <Link to={p.to}>{p.name}</Link>
                  <code>{p.iface}</code>
                </dt>
                <dd>{p.desc}</dd>
              </div>
            ))}
          </dl>
          <CodeBlock language="csharp">{PATTERNS_CODE}</CodeBlock>
        </div>
      </div>
    </section>
  );
}

const CAPABILITIES = [
  {
    group: "Jobs and workflows",
    items: [
      ["Transactional outbox", "/docs/features/outbox-pattern"],
      ["Recurring jobs", "/docs/features/recurring-jobs"],
      ["Batches and continuations", "/docs/patterns/jobs#batches"],
      [
        "Queues and worker groups",
        "/docs/operations/configuration#worker-groups",
      ],
      ["Sagas", "/docs/features/sagas"],
      ["Background services", "/docs/features/background-services"],
      ["Cancellation", "/docs/features/cancellation"],
      ["Pause and resume", "/docs/features/pause"],
    ],
  },
  {
    group: "Flow control",
    items: [
      ["Retry", "/docs/patterns/jobs#retries"],
      ["Mutex", "/docs/features/mutex"],
      ["Semaphore", "/docs/features/semaphore"],
      ["Rate limit", "/docs/features/rate-limit"],
      ["Timeout", "/docs/features/timeout"],
      ["Circuit breaker", "/docs/features/circuit-breaker"],
    ],
  },
  {
    group: "Throughput and recovery",
    items: [
      ["Batched completions", "/docs/features/batched-completions"],
      ["Database push", "/docs/features/db-push"],
      ["Crash recovery", "/docs/operations/crash-recovery"],
    ],
  },
  {
    group: "HTTP and integrations",
    items: [
      ["HTTP endpoints from handlers", "/docs/features/http"],
      ["Outbound call adapters", "/docs/features/adapters"],
      ["Durable webhooks", "/docs/features/webhooks"],
    ],
  },
  {
    group: "Observability",
    items: [
      ["Dashboard", "/docs/dashboard/overview"],
      ["Tracing across jobs", "/docs/features/tracing"],
      ["Error grouping", "/docs/features/error-grouping"],
      ["Queue wait and backlog", "/docs/features/queue-metrics"],
      ["SLOs and error budgets", "/docs/features/slo-error-budget"],
      ["Custom metrics", "/docs/features/custom-metrics"],
      ["Operational notifications", "/docs/features/operational-notifications"],
    ],
  },
];

function Capabilities() {
  return (
    <section className={`${styles.section} ${styles.tinted}`} id="features">
      <div className={styles.wrap}>
        <h2 className={styles.h2}>What is in the box</h2>
        <p className={styles.lede}>
          Most of these are one builder call or one attribute. Each links to its
          page.
        </p>
        <div className={styles.caps}>
          {CAPABILITIES.map((c) => (
            <div key={c.group}>
              <h3 className={styles.capGroup}>{c.group}</h3>
              <ul className={styles.capList}>
                {c.items.map(([label, to]) => (
                  <li key={label}>
                    <Link to={to}>{label}</Link>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function Install() {
  return (
    <section className={styles.section} id="install">
      <div className={styles.wrap}>
        <h2 className={styles.h2}>Run your first job</h2>
        <p className={styles.lede}>
          Four packages and the database you have. Warp adds its tables to your
          model, so your normal migrations create them.
        </p>
        <ol className={styles.steps}>
          <li>
            <h3 className={styles.stepTitle}>Add the packages</h3>
            <CodeBlock language="bash">{INSTALL_CODE}</CodeBlock>
          </li>
          <li>
            <h3 className={styles.stepTitle}>
              Register Warp on your DbContext
            </h3>
            <CodeBlock language="csharp" title="Program.cs">
              {REGISTER_CODE}
            </CodeBlock>
          </li>
          <li>
            <h3 className={styles.stepTitle}>Create the tables</h3>
            <CodeBlock language="bash">{MIGRATE_CODE}</CodeBlock>
          </li>
        </ol>
        <div className={styles.actions}>
          <Link className={styles.btnPrimary} to="/docs/getting-started">
            Read the getting started guide
          </Link>
          <Link
            className={styles.btnQuiet}
            to="/docs/operations/migrating-from-hangfire"
          >
            Migrating from Hangfire
          </Link>
          <Link
            className={styles.btnQuiet}
            to="/docs/operations/migrating-from-wolverine"
          >
            Migrating from Wolverine
          </Link>
        </div>
      </div>
    </section>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export default function Home(): ReactNode {
  return (
    <Layout description="Background jobs, pub/sub messages and an in-process mediator for .NET 10, stored in your PostgreSQL or SQL Server database through EF Core. About 0.24 database statements per job, with a real-time operations dashboard.">
      <main className={styles.page}>
        <Hero />
        <Reasons />
        <Tour />
        <Performance />
        <Patterns />
        <Capabilities />
        <Install />
      </main>
    </Layout>
  );
}
