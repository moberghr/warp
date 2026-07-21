// Aspire orchestrator for the Warp adapters + webhooks demo. Runs, together with one `dotnet run`:
//   • a Postgres container (provisions the DB and injects its connection string),
//   • the partner service (an external vendor API + webhook receiver),
//   • the Warp dashboard app (registers the outbound adapters + webhooks, seeds the demo workload),
//   • a second Warp worker (shares the same DB, shows multi-host job processing).
var builder = DistributedApplication.CreateBuilder(args);

// Postgres, provisioned by Aspire. The database resource is named "TestContext" so its injected
// connection string lands under ConnectionStrings:TestContext — exactly what the demo apps read.
var database = builder.AddPostgres("postgres")
    .AddDatabase("TestContext");

// The external service the demo calls through the outbound adapters and delivers webhooks to.
// Non-proxied http endpoint so it is reachable directly on its fixed port (http://localhost:5230/partner).
var partner = builder.AddProject<Projects.Warp_Demo_PartnerApi>("partner-api")
    .WithEndpoint("http", e => e.IsProxied = false);

// The Warp dashboard app: registers the adapters/webhooks, runs the worker, hosts /warp, the seed
// endpoints, and the single-shot /trigger/* endpoints. It creates the schema on startup (EnsureCreated)
// before Kestrel signals "running". Non-proxied so /warp is directly reachable (http://localhost:5104/warp).
var app = builder.AddProject<Projects.Warp_Test_App>("warp-app")
    .WithEndpoint("http", e => e.IsProxied = false)
    .WithReference(database)
    .WaitFor(database)
    .WithReference(partner)
    .WithEnvironment("PartnerApi__BaseUrl", partner.GetEndpoint("http"));

// A second Warp server sharing the same database — demonstrates multi-host job processing and DB-push.
// WaitFor(app) so it starts only after the app has created the schema; PRESERVE_DB stops it wiping it.
builder.AddProject<Projects.Warp_Test_Worker>("warp-worker")
    .WithReference(database)
    .WaitFor(app)
    .WithEnvironment("WARP_DEMO_PRESERVE_DB", "1");

await builder.Build().RunAsync();
