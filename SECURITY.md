# Security policy

## Reporting a vulnerability

Please do not report a security problem in a public issue, discussion or pull request.

Report it privately through GitHub: open the repository's **Security** tab and choose [**Report a vulnerability**](https://github.com/moberghr/warp/security/advisories/new). Only the maintainers can see the report.

It helps to include:

- the Warp version, the database (PostgreSQL or SQL Server) and its version;
- which package is affected (for example `Moberg.Warp.Dashboard` or `Moberg.Warp.Http`);
- the steps to reproduce it, or a proof of concept;
- what an attacker could do with it.

## Supported versions

Security fixes are released for the latest major version. Upgrade to the newest release before you report, in case it is already fixed; the [release notes](https://moberghr.github.io/warp/docs/releases) list every version.

## Things to know when you deploy Warp

These are documented behaviours, not vulnerabilities, but they matter for a secure setup:

- **The dashboard is open by default.** Gate it before you expose it: see [Dashboard auth](https://moberghr.github.io/warp/docs/operations/dashboard-auth).
- **Job payloads are stored in your database** as written. Do not put secrets in a job, and treat the Warp tables with the same care as the rest of your data.
- **Keys for mutexes, semaphores and rate limits appear in logs and in the dashboard.** Use an opaque identifier, not personal data.
