# Contributing / Developer Guide

This is the practical build/run/maintenance guide for LinkshellManager. See [README.md](README.md) for
the architecture overview and the Discord Activity / auth flow.

## Prerequisites

- .NET 8 SDK
- Node.js + npm (for the Angular Discord Activity SPA)
- PostgreSQL 15+
- An HTTPS tunnel (e.g. `cloudflared` or `ngrok`) only when testing the embedded Discord flow

## One-time local setup

> **Your dev machine never points at production.** User-secrets are what `dotnet run`,
> `dotnet ef database update` and `dotnet ef migrations ...` read by default, so they must always hold a
> **local** database. One routine command against production can apply an unreleased migration while the
> live app is running, or roll one back. The production connection string lives only in the `LSM_PROD_CS`
> environment variable, which you set for a single shell when you mean it, and which only
> `tools/DbInspector` (read-only) uses.

Start a local Postgres in Docker (one time):

```bash
docker run -d --name lsm-test-db -e POSTGRES_PASSWORD=<pw> -e POSTGRES_DB=lsm_test -p 55432:5432 postgres:16
```

Secrets are **not** stored in `appsettings.json`. Configure them with user-secrets, pointing at that local
database:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=55432;Database=lsm_test;Username=postgres;Password=<pw>"
dotnet user-secrets set "Discord:ClientSecret" "<your-discord-client-secret>"
```

Check it with `dotnet user-secrets list` (the connection string must say `Host=localhost`).

Apply the database schema to the local database:

```bash
dotnet ef database update
```

## Build & run

```bash
# 1. Build the Angular SPA into wwwroot/discord-activity/browser
cd discord-activity
npm install
npm run build
cd ..

# 2. Run the ASP.NET host (serves the SPA at /discord-activity)
dotnet run --launch-profile https
```

Rebuild the Angular app after any frontend change before retesting through ASP.NET.

## Database migrations

```bash
dotnet ef migrations add <Name>     # create a migration after changing entities
dotnet ef database update           # apply pending migrations
dotnet ef migrations list           # show the full applied/pending set
```

In non-development environments the app applies pending migrations automatically on startup.

## Solution layout

| Project | Purpose |
|---|---|
| `LinkshellManagerDiscordApp.csproj` | The host: JSON APIs + EF Core, a few public Razor pages, and the Angular app |
| `discord-activity/` | The Angular SPA: the Discord Activity inside Discord and the website in a browser (built into `wwwroot/`) |
| `DbWipe/` | **Destructive** dev utility — truncates all tables to reset a local DB |
| `tools/DbInspector/` | Read-only DB inspection (+ a guarded `reset-public`) |

The two utility projects are intentionally separate console apps, run by hand. Both refuse destructive
operations unless you pass the connected database name as confirmation — never point them at production.

DbInspector reads its connection string only from an environment variable you set for that run; it never
falls back to user-secrets. Set exactly one:

- `APP_CS` — a local database. Every mode works, including `reset-public <database-name>`.
- `LSM_PROD_CS` — production. The session is opened read-only and `reset-public` is refused.

```bash
LSM_PROD_CS="<production connection string>" dotnet run --project tools/DbInspector -- history
```

## Code style

- C# nullable reference types are enabled; keep new code warning-clean.
- Style is enforced in the build via `.editorconfig` + analyzers (`dotnet format` to auto-fix).
- The Angular project uses ESLint (`npm run lint` in `discord-activity`).
- Data Admin (`/data-admin`) discovers every table from the EF model, so a new entity appears on its
  schema diagram, with a records page, automatically. What it may do with a table lives in
  `Services/DataAdminPolicy.cs`: add an entry there to make a table read-only, block create/delete,
  hide credential columns, or turn a string column into a dropdown of its const class.
  `DataAdminPolicyTests` fails on any misspelled column.


## Tests

```bash
dotnet test                          # backend unit tests
node --test tests/js/data-admin-schema-layout.test.js   # Data Admin diagram JS tests (Node 20+, nothing to install)
cd discord-activity && npm run lint && npm run build    # the Angular app has no unit tests yet; lint + build is the check
```

A new feature goes into the Angular app, for both hosts. One that only makes sense in a browser
(file upload/download, super-admin tools) is gated with `DiscordActivityService.isWebsite` and, for a
whole tab, `ActivityHomeComponent.canOpenTab`. Don't add new Razor pages for app features.
