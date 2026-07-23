// Aspire orchestrator for the Warp adapters + webhooks demo. Runs, together with one `dotnet run`:
//   • a Postgres container (provisions the DB and injects its connection string),
//   • the partner service (an external vendor API + webhook receiver),
//   • the Warp dashboard app — a NON-server publisher: hosts /warp, the seed/trigger endpoints, the
//     inbound HTTP endpoints, and stages jobs + webhook sends, but runs NO worker,
//   • the Warp worker — the demo's sole server: executes every job, calls the outbound adapters, and
//     delivers the durable webhooks (shares the same DB; drives the dashboard via DB-push).
var builder = DistributedApplication.CreateBuilder(args);

// Postgres, provisioned by Aspire. The database resource is named "TestContext" so its injected
// connection string lands under ConnectionStrings:TestContext — exactly what the demo apps read.
var database = builder.AddPostgres("postgres")
    .AddDatabase("TestContext");

// The external service the demo calls through the outbound adapters and delivers webhooks to.
// Non-proxied http endpoint so it is reachable directly on its fixed port (http://localhost:5230/partner).
var partner = builder.AddProject<Projects.Warp_Demo_PartnerApi>("partner-api")
    .WithEndpoint("http", e => e.IsProxied = false);

// The Warp dashboard app (NON-server): hosts /warp, the seed endpoints, and the single-shot /trigger/*
// endpoints, and stages jobs + webhook sends. It creates the schema on startup (EnsureCreated) before
// Kestrel signals "running". It keeps PartnerApi__BaseUrl because it still builds webhook target URLs
// from it, but it no longer references the partner service directly (the adapters that CALL partner now
// live on the worker). Non-proxied so /warp is directly reachable (http://localhost:5104/warp).
var app = builder.AddProject<Projects.Warp_Test_App>("warp-app")
    .WithEndpoint("http", e => e.IsProxied = false)
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("PartnerApi__BaseUrl", partner.GetEndpoint("http"));

// The Warp worker — the demo's sole server. It executes every job, so it owns the outbound adapters and
// therefore references partner-api (the service those adapters call) and gets PartnerApi__BaseUrl (the
// adapter base address + webhook targets it reads from config). WaitFor(app) so it starts only after the
// app has created the schema; PRESERVE_DB stops it wiping it.
builder.AddProject<Projects.Warp_Test_Worker>("warp-worker")
    .WithReference(database)
    .WithReference(partner)
    .WithEnvironment("PartnerApi__BaseUrl", partner.GetEndpoint("http"))
    .WaitFor(app)
    .WithEnvironment("WARP_DEMO_PRESERVE_DB", "1");

await builder.Build().RunAsync();
