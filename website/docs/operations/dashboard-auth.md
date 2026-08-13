---
sidebar_position: 3
---

# Dashboard Authorization

The dashboard is a set of routed endpoints, so you gate it the way you gate any other part of your app — with ASP.NET Core authorization. `MapWarpUI` returns an endpoint builder covering the SPA shell, the REST API and the SignalR hub, so one call protects all three:

```csharp
app.MapWarpUI("/warp").RequireAuthorization("WarpDashboard");
```

By default (no convention applied) the dashboard is open to everyone.

Handing the decision to ASP.NET is what makes the two kinds of "no" behave differently: a signed-out visitor is **challenged**, so they reach your sign-in page, while a signed-in visitor who lacks the permission is **forbidden** (403, or your `AccessDeniedPath`). Your authorization requirements are also `async`, so a permission check that reads the database is an ordinary `await`.

## Choosing a shape

| Your situation | What to write |
|---|---|
| Local development, no gate | `app.MapWarpUI("/warp");` |
| The app already has an identity system | `app.MapWarpUI("/warp").RequireAuthorization("YourPolicy");` |
| No identity system — want a login page | `AddWarpDashboard().AddBuiltInLogin<T>()` + `.RequireWarpDashboardLogin()` |
| Localhost only | `AddWarpDashboard()` + `.RequireLocalRequests()` |

## Gating on your own policy

Nothing from Warp is needed — the dashboard endpoints take your policy like any other endpoint:

```csharp
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/denied";
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("WarpDashboard", policy => policy.RequireRole("Admin"));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapWarpUI("/warp").RequireAuthorization("WarpDashboard");
```

A permission that lives in your database becomes an authorization handler — natively async, unit-testable, and identical to how the rest of your app is gated:

```csharp
public class WarpDashboardHandler : AuthorizationHandler<WarpDashboardRequirement>
{
    private readonly IPermissionService _permissions;

    public WarpDashboardHandler(IPermissionService permissions) => _permissions = permissions;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WarpDashboardRequirement requirement)
    {
        if (await _permissions.HasAsync(context.User, "warp.dashboard"))
        {
            context.Succeed(requirement);
        }
    }
}
```

:::important Pipeline order
`UseAuthentication()` and `UseAuthorization()` must be in the pipeline. If you call neither explicitly, `WebApplication` adds them for you when the matching services are registered — but it inserts them ahead of your own middleware, so call them yourself if any middleware of yours needs to run first.
:::

### XHR and expired sessions

The dashboard is a single-page app that talks to `/warp/api/...` over XHR. A challenge answered with a redirect to an identity provider is not something a `fetch` can follow, so configure your scheme to answer API paths with a status code — the standard pattern for an app that serves both pages and APIs:

```csharp
.AddCookie(options =>
{
    options.LoginPath = "/login";
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/warp/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
```

The dashboard handles the rest. Every API response carries an `X-Warp-Api` header, so the SPA can tell "the
Warp API answered" from "something intercepted this call and returned its own 200" — a sign-in redirect the
browser transparently followed. On that, on a 401, or on a request killed outright by CORS (which is how a
cross-origin challenge presents), it reloads **once**, turning the dead XHR back into a navigation your
challenge can act on. A 403 deliberately does not trigger it: the caller is signed in and forbidden, so a
re-challenge would return the same answer and loop.

## Built-in login

Warp can serve its own login page — no identity system needed. It registers a real cookie authentication scheme, so sessions, expiry and sign-out are ASP.NET's, not a bespoke cookie.

```csharp
builder.Services.AddWarpDashboard().AddBuiltInLogin<MyCredentialValidator>();

var app = builder.Build();

app.MapWarpUI("/warp").RequireWarpDashboardLogin();
```

Registering the built-in login **gates the dashboard on its own** — `RequireWarpDashboardLogin()` is
explicit rather than required, and applying it twice is harmless. Otherwise the half-configured shape
(services registered, convention forgotten) would compile, render a login page, and serve every API route
anonymously.

One consequence: adding `.RequireAuthorization("YourPolicy")` on top means callers must satisfy **both** the
Warp cookie and your policy. If your own policy is the gate you want, don't register the built-in login.

`AddBuiltInLogin<T>` registers your validator as **Scoped**, so it can inject a `DbContext`:

```csharp
public class MyCredentialValidator : IWarpCredentialValidator
{
    private readonly AppDbContext _db;

    public MyCredentialValidator(AppDbContext db) => _db = db;

    public async Task<bool> ValidateAsync(string username, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        return user != null && BCrypt.Verify(password, user.PasswordHash);
    }
}
```

Session lifetime is configurable:

```csharp
builder.Services.AddWarpDashboard().AddBuiltInLogin<MyCredentialValidator>(options =>
{
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.CookiePath = "/warp";   // defaults to the dashboard's route prefix
});
```

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/11-login.png" dark="/img/screenshots/11-login-dark.png" alt="Login" />

### How it works

1. The REST API and the hub are protected; the SPA shell stays anonymous — the shell *is* the login page, so gating it would challenge the page that collects the credentials
2. The SPA posts credentials to `/warp/api/auth/login`, which calls your validator and signs the user in
3. Warp sets an HTTP-only, `SameSite=Strict` cookie whose expiry is **enforced server-side**
4. API requests carry the cookie; a 401 sends the SPA back to the login form
5. `POST /warp/api/auth/logout` signs the user out

## Localhost only

```csharp
builder.Services.AddWarpDashboard();

var app = builder.Build();

app.MapWarpUI("/warp").RequireLocalRequests();
```

A remote caller gets 403 — signing in cannot change the answer, so there is nothing to challenge. This covers the shell, the API and the hub.

The same mechanism is available for any rule that signing in cannot satisfy — an API-key check, an allowlist — even in a host with no identity provider at all. Pin `WarpDashboardDefaults.DenyScheme` on the policy and its denials render as 403 instead of failing on a challenge that has no scheme to use:

```csharp
builder.Services.AddWarpDashboard();   // registers the deny scheme

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("WarpApiKey", policy => policy
        .AddAuthenticationSchemes(WarpDashboardDefaults.DenyScheme)
        .RequireAssertion(context => context.Resource is HttpContext http
            && http.Request.Headers["X-Api-Key"] == "…"));

app.MapWarpUI("/warp").RequireAuthorization("WarpApiKey");
```

## What is gated

Everything under the route prefix. Conventions apply to the SPA shell (`/warp`, `/warp/{**path}`), the SPA's static assets (`/warp/assets/...`), dashboard extension JS (`/warp/_ext/...`), every REST endpoint under `/warp/api/...`, and the SignalR hub — including its negotiate request and WebSocket upgrade.

The dashboard's own endpoints serve its embedded assets, rather than static-file middleware, because `StaticFileMiddleware` stands down once routing has matched an endpoint and the shell's catch-all route matches every asset path. Conditional requests still work — assets answer `If-None-Match` / `If-Modified-Since` with a 304.

The built-in login is the one deliberate exception: it gates the API and hub but leaves the shell and its
assets anonymous, because the shell renders the login form and needs its own JavaScript to do so.

### Warp owns every path under the prefix

Because the shell's catch-all route claims `{prefix}/{**path}`, **content your own app serves under the
dashboard's prefix is no longer reachable** — in 3.x the dashboard was middleware and unmatched paths
continued down the pipeline. If you were serving, say, `wwwroot/warp/logo.png` and pointing `LogoUrl` at it,
move it outside the prefix. Host *endpoints* with literal routes still win over the catch-all; only
middleware-served content is affected.

### Route prefix

The prefix is normalized to a leading slash with no trailing slash, so `"warp"`, `"/warp"` and `"/warp/"`
are equivalent. Mounting at the application root (`"/"`) throws — the catch-all would swallow every request
in your app.

## Upgrading from 3.x

| 3.x | 4.0 |
|---|---|
| `app.UseWarpUI(...)` | `app.MapWarpUI(...)` — returns an endpoint builder |
| `options.Authorization = new MyFilter()` | an authorization policy + `.RequireAuthorization("...")` |
| `options.UnauthorizedRedirectUrl = "/login"` | your scheme's `LoginPath` (ASP.NET adds `returnUrl` and an access-denied path) |
| `options.UseBuiltInLogin<T>()` | `services.AddWarpDashboard().AddBuiltInLogin<T>()` + `.RequireWarpDashboardLogin()` |
| `new LocalRequestsOnlyAuthorizationFilter()` | `.RequireLocalRequests()` |
| `using Warp.UI.UIMiddleware;` | `using Warp.UI;` |

Two smaller behaviour changes to know about:

- **`GET {prefix}/api/auth/status` only exists with the built-in login.** It is anonymous by necessity (it is
  the pre-login probe), so mapping it unconditionally would bypass whatever gate you applied and answer a
  constant `true` — wrong under a host policy, and a recon signal on a dashboard meant to be loopback-only.
- **Mapping two dashboards while the built-in login is registered now throws.** One cookie scheme has one
  cookie name and one path, so sign-in silently failed on all but the last one mapped.

`IWarpAuthorizationFilter` and `LocalRequestsOnlyAuthorizationFilter` are gone. A bool-returning filter could not distinguish "not signed in" from "not permitted", so both produced a bare 401, and `UnauthorizedRedirectUrl` — which redirected on every denial — bounced an authenticated-but-unpermitted user between the dashboard and the sign-in page forever. Being synchronous, it also forced a blocking `.GetAwaiter().GetResult()` on any permission check that read a database. Authorization policies fix all three.

`AddDataProtection()` is no longer needed for the dashboard — the cookie handler brings it.
