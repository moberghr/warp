// Aspire orchestrator for the Warp adapters + webhooks demo. Runs, together with one `dotnet run`:
//   • a Postgres container (provisions the DB and injects its connection string),
//   • the migrator — a ONE-SHOT that creates the schema (EnsureCreated) + seeds the product catalog and
//     runs to COMPLETION before anything else starts, so the web + worker never race a missing table,
//   • the partner service (an external vendor API + webhook receiver),
//   • the Warp dashboard app — a NON-server publisher: hosts /warp, the seed/trigger endpoints, the
//     inbound HTTP endpoints, and stages jobs + webhook sends, but runs NO worker,
//   • the Warp worker — the demo's sole server: executes every job, calls the outbound adapters, and
//     delivers the durable webhooks (shares the same DB; drives the dashboard via DB-push).
var builder = DistributedApplication.CreateBuilder(args);

// Postgres, provisioned by Aspire. The database resource is named "TestContext" so its injected
// connection string lands under ConnectionStrings:TestContext — exactly what the demo apps read.
// Port and credentials pinned so the container is always published on host port 5442 — the port the
// standalone appsettings point at. 5442 is clear of 5432-5434 and of Docker's ephemeral published-port range.
// Demo-only, non-secret defaults (§1.1).
var postgresUser = builder.AddParameter("postgres-user", "postgres");
var postgresPassword = builder.AddParameter("postgres-password", "admin", secret: true);

var database = builder.AddPostgres("postgres", postgresUser, postgresPassword)
    .WithHostPort(5442)
    .AddDatabase("TestContext");

// The one-shot schema provisioner. It wipes + recreates a fresh schema (EnsureCreated) and seeds the
// product catalog, then exits 0. No WARP_DEMO_PRESERVE_DB, so every run starts clean. The web + worker
// WaitForCompletion(migrator) below, so they only start once the schema exists — no startup race.
var migrator = builder.AddProject<Projects.Warp_Demo_Migrator>("migrator")
    .WithReference(database)
    .WaitFor(database);

// The external service the demo calls through the outbound adapters and delivers webhooks to.
// Non-proxied http endpoint so it is reachable directly on its fixed port (http://localhost:5230/partner).
var partner = builder.AddProject<Projects.Warp_Demo_PartnerApi>("partner-api")
    .WithEndpoint("http", e => e.IsProxied = false);

// The Warp dashboard app (NON-server): hosts /warp, the seed endpoints, and the single-shot /trigger/*
// endpoints, and stages jobs + webhook sends. The migrator owns schema creation now, so this only
// registers recurring-job definitions against the already-provisioned schema. It keeps PartnerApi__BaseUrl
// because it still builds webhook target URLs from it, but it no longer references the partner service
// directly (the adapters that CALL partner now live on the worker). WaitForCompletion(migrator) so it
// starts only after the schema exists. Non-proxied so /warp is directly reachable (http://localhost:5104/warp).
builder.AddProject<Projects.Warp_Test_App>("warp-app")
    .WithEndpoint("http", e => e.IsProxied = false)

    // Relative URL follows the endpoint's port. The Func overload ADDS a link; Action would replace it.
    .WithUrlForEndpoint("http", _ => new() { Url = "/warp", DisplayText = "Warp dashboard" })
    .WithReference(database)
    .WaitForCompletion(migrator)
    .WithEnvironment("PartnerApi__BaseUrl", partner.GetEndpoint("http"));

// The Warp worker — the demo's sole server. It executes every job, so it owns the outbound adapters and
// therefore references partner-api (the service those adapters call) and gets PartnerApi__BaseUrl (the
// adapter base address + webhook targets it reads from config). WaitForCompletion(migrator) so it starts
// only after the schema exists (it no longer needs to wait for the app); PRESERVE_DB stops it wiping it.
builder.AddProject<Projects.Warp_Test_Worker>("warp-worker")
    .WithReference(database)
    .WithReference(partner)
    .WithEnvironment("PartnerApi__BaseUrl", partner.GetEndpoint("http"))
    .WaitForCompletion(migrator)
    .WithEnvironment("WARP_DEMO_PRESERVE_DB", "1");

await builder.Build().RunAsync();
