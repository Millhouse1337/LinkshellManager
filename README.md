# LinkshellManager Discord Activity

## Architecture

- Root app: ASP.NET Core + Identity + EF Core + PostgreSQL (JSON APIs, plus a few server-rendered pages)
- Frontend: Angular app in `discord-activity`, the **one UI for both hosts**
- Angular build output: `wwwroot/discord-activity/browser`
- App route: ASP.NET serves the Angular SPA at `/discord-activity`
- Discord auth exchange endpoint: `POST /auth/discord/exchange`
- Local identity store: `DiscordActivityUsers` linked to `AspNetUsers` (`AppUser`)

One UI, two hosts. Inside Discord the Angular app runs as the Discord Activity; in a normal browser
the same app is the website (see [Website mode](#website-mode)). The ASP.NET app remains the host,
security boundary, and configuration source; the Angular app is a separate frontend project built
into `wwwroot`.

## Discord Activity flow

1. Discord launches the app inside an iframe through the Activity URL mapping.
2. ASP.NET serves the Angular SPA from `/discord-activity`.
3. Angular initializes one `DiscordSDK` instance through `DiscordActivityService`.
4. The frontend calls `discordSdk.commands.authorize({ scope: ['identify', 'guilds', 'applications.commands'] })`.
5. Angular posts the returned code to `POST /auth/discord/exchange`.
6. ASP.NET exchanges the code for an OAuth access token with Discord, looks up `/users/@me`, and creates or updates a local `DiscordActivityUsers` row.
7. Angular calls `discordSdk.commands.authenticate({ access_token })`.
8. Angular calls `GET /api/me` with the Discord bearer token to retrieve the local app user record from the database.
9. The UI renders the authenticated Discord session, local app user, and activity context details.

The authenticated session inside Discord can include additional scopes such as `guilds.members.read` or `rpc.voice.read`. The app does not explicitly request those in Angular; it requests the minimum set it currently relies on for the embedded flow.

## Website mode

In a normal browser (`window.parent === window`) the same Angular app is the website. It skips the
Discord SDK entirely and uses the site's own cookie login:

- **Sign in:** `/auth/discord/login?returnUrl=...` (the `DiscordWebsite` OAuth scheme, scope `identify`)
  sets the Identity cookie. `GET /api/web/session` tells the app whether it is signed in; signed out,
  `app.html` shows a sign-in screen instead of the app. Any API 401 flips back to it. **Sign out** is
  `POST /api/web/sign-out`.
- **Same API:** every `/api/activity/*` endpoint accepts the cookie as well as the Discord bearer token
  (`ResolveAppUserAsync`). Cookie mutations need the `X-XSRF-TOKEN` header, which
  `ActivityHttpClient.sendActivityRequest` adds.
- **Discord server lock:** a linkshell's "Lock to this server" governs the Discord Activity only. The
  website always shows a linkshell to its members (`GuildLockPolicy`); claiming a placeholder still
  respects the lock.
- **A URL per tab:** on the website the tab and sub-view live in the URL (`/discord-activity/events/past`,
  `/dkp/history`, `/charts/sea`; slugs in `src/app/home/tab-routes.ts`), so reload, Back and deep links
  work. `?ls=<id>` switches the active linkshell. Inside Discord the iframe's URL is never touched; the
  tab lives in memory there.
- **Website-only features**, hidden inside Discord: the super-admin **Admin** (`/api/web/admin`) and
  **Data Admin** (`/api/data-admin`) tabs. `ActivityHomeComponent.canOpenTab` gates them. (DKP Import
  and Export to Excel work in Discord too: the import uploads as base64 JSON, and inside Discord a
  download opens in the member's browser through a one-time link, `ActivityDataController.Downloads`.
  Reviewing members' ToD submissions (ToDs tab) and /lsm now captures (Event System → Current) is on both hosts as well.)
- `/` shows the public landing page signed out and redirects into the app signed in.

## Server-rendered pages

Razor now renders only what can't be the app: the public landing page, `/features`, `/addon`,
Privacy, Terms, Error and the Identity sign-in (error) page, all on `Views/Shared/_PublicLayout.cshtml`.
Data Admin is the app's super-admin **Data Admin** tab (`/api/data-admin`); its schema diagram is the
shared `wwwroot/js/data-admin-schema.js` + `wwwroot/css/data-admin-schema.css`, mounted by the tab.

The old Razor website was deleted in October 2026. Its GET addresses still redirect permanently (301)
into the app (`Utils/LegacyUrlRedirects.cs`, e.g. `/Event` → `/discord-activity/events`), so bookmarks
and old Discord posts keep working.

## Auth flow

- The frontend does not own the Discord client secret.
- The backend exchanges the auth code with `https://discord.com/api/oauth2/token`.
- The backend uses the returned access token to fetch `https://discord.com/api/users/@me`.
- The backend creates or updates a local `DiscordActivityUsers` row keyed to the Discord user id.
- The backend returns the access token payload needed for the embedded `authenticate` step and the local user summary.
- The frontend then authenticates with the Embedded App SDK and uses `GET /api/me` for a database-backed user record.

## Local user model

The app now has two linked user layers:

1. Discord Activity identity
   - stored in `DiscordActivityUsers`
   - keyed by `DiscordUserId`
   - updated on each embedded Activity launch

2. Internal app identity
   - stored in `AspNetUsers` using the custom `AppUser` type
   - the account the website's cookie login signs into, and what every linkshell, membership and event hangs off
   - linked from `DiscordActivityUsers.IdentityUserId`

Current `AppUser` fields include:

- `Id`
- `UserName`
- `CharacterName`
- `AltCharacterName1`
- `AltCharacterName2`
- `TimeZone`
- `PrimaryLinkshellId` (its name is read from the `Linkshells` row)

`AltCharacterName1` and `AltCharacterName2` capture the user's alternate in-game character names. They are optional, capped at 64 characters, and validated alongside the primary character name during profile updates.

Current `DiscordActivityUsers` fields:

- `Id`
- `DiscordUserId`
- `Username`
- `GlobalName`
- `Avatar`
- `IdentityUserId`
- `CreatedAtUtc`

Provisioning behavior:

- First Discord launch creates or updates the `DiscordActivityUsers` row.
- If no linked `AppUser` exists yet, the backend creates one and stores its id in `IdentityUserId`.
- `GET /api/me` returns both the Discord-linked local user record and the linked `AppUser` summary (Discord only; the website uses `GET /api/web/session`).
- Linkshells, memberships, live events, completed event history, and notifications are all keyed from the `AppUser` layer.

Important: for the embedded Activity flow, the backend does not send `redirect_uri` during token exchange. Discord's current Activity tutorial shows the embedded flow without `redirect_uri`; the Redirect URI still needs to exist in the Developer Portal, but it is a portal requirement rather than a request parameter used by this app flow.

Official references used:
- Embedded App SDK reference: https://docs.discord.com/developers/developer-tools/embedded-app-sdk
- Building an Activity: https://docs.discord.com/developers/activities/building-an-activity
- Networking guide: https://docs.discord.com/developers/activities/development-guides/networking

## Local development

### Prerequisites

- .NET 8 SDK
- Node.js and npm
- PostgreSQL 15+ instance
- A public HTTPS tunnel for Discord testing, such as `cloudflared` or `ngrok`

### Configure secrets for local development

The repo no longer stores the Discord client secret in `appsettings.json`.

Set the local Discord secret with user secrets:

```bash
dotnet user-secrets set "Discord:ClientSecret" "<your-discord-client-secret>"
```

Optional if you also want to override the client id locally:

```bash
dotnet user-secrets set "Discord:ClientId" "<your-discord-client-id>"
```

The PostgreSQL connection string is required and is **not** stored in `appsettings.json`. Set it via user secrets for local development, pointing at a **local** database (the Docker one from [Testing against a throwaway database](#testing-against-a-throwaway-database)):

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=55432;Database=lsm_test;Username=postgres;Password=<pw>"
```

The app will fail to start if `ConnectionStrings:DefaultConnection` is not configured.

**Never put the production connection string in user-secrets.** `dotnet run` and every `dotnet ef`
command read them, so one routine command could migrate, roll back, or run background jobs against the
live database. Keep the production string only in the `LSM_PROD_CS` environment variable, set for a
single shell when you need it; only `tools/DbInspector` uses it, and read-only (see CONTRIBUTING.md).

For production or hosted environments, set environment variables instead:

```bash
Discord__ClientId=<your-discord-client-id>
Discord__ClientSecret=<your-discord-client-secret>
ConnectionStrings__DefaultConnection=Host=<host>;Port=5432;Database=linkshell_manager_discord_app;Username=<user>;Password=<password>
```

If the current Discord secret was ever committed to source control, treat it as compromised and rotate it in the Discord Developer Portal.

### Build the Angular Activity

```bash
cd discord-activity
npm install
npm run build
```

This writes the built SPA to `wwwroot/discord-activity/browser`.

### PostgreSQL setup

Create the local database and apply the EF migrations (this uses the user-secrets connection string, so
check it points at `localhost` first with `dotnet user-secrets list`):

```bash
"$env:USERPROFILE\\.dotnet\\tools\\dotnet-ef.exe" database update
```

`database update` applies every migration under `Migrations/` in order. The initial schema is
`20260512035741_InitialSchema`; later migrations add per-linkshell job levels, Google Sheet config,
attendance snapshots, window events, party setup, and more. Run `dotnet ef migrations list` to see the
full, current set.

The recommended local connection string targets:

- host: `localhost`
- port: `55432` (the Docker container below)
- database: `lsm_test`
- username: `postgres`

### Run the ASP.NET app

From the repo root:

```bash
dotnet run --launch-profile https
```

Use the `https` profile. The Activity route should be available at:

- `https://localhost:7051/discord-activity`

### Local route checks

- Open `/discord-activity` in a normal browser: signed out you get the sign-in screen, signed in the app.
- Open a deep route such as `/discord-activity/events/past` and confirm it opens that tab, and that reload and Back keep you there.
- Open an old address such as `/Event` and confirm it redirects to `/discord-activity/events`.
- Rebuild Angular after frontend changes before retesting through ASP.NET.

### Testing against a throwaway database

Your user secrets should already point at this local database (see above). Passing the connection string
explicitly as well, as below, keeps a run safe even if they don't (environment variables override user
secrets). Give it a dummy bot token so the startup jobs that configure the Discord application
(`DiscordEntryPointConfigurator`) can't reach the real one:

```bash
docker run -d --name lsm-test-db -e POSTGRES_PASSWORD=<pw> -e POSTGRES_DB=lsm_test -p 55432:5432 postgres:16
dotnet ef database update --connection "Host=localhost;Port=55432;Database=lsm_test;Username=postgres;Password=<pw>"
ConnectionStrings__DefaultConnection="Host=localhost;Port=55432;Database=lsm_test;Username=postgres;Password=<pw>" \
Discord__BotToken=disabled dotnet run --launch-profile https
```

Development doesn't migrate on startup, hence the explicit `database update`.

### Postgres tests (need Docker Desktop running)

`tests/LinkshellManager.Tests` runs on EF's in-memory database: fast, but it ignores unique indexes,
foreign-key delete rules, transactions and row locks. `tests/LinkshellManager.PostgresTests` covers those.
It starts a throwaway `postgres:18` container (Testcontainers), applies every migration once, and wipes
the tables before each test. It never touches your user-secrets database.

```bash
dotnet test tests/LinkshellManager.PostgresTests/LinkshellManager.PostgresTests.csproj
```

Add a test here whenever a change relies on a database rule. Put `[Collection("postgres")]` on the class,
take `PostgresFixture` in the constructor, call `ResetAsync()` at the start of each test, and use
`CreateContext()` (twice for a two-request race). CI runs these too (`dotnet test` on the solution).

## Deployment notes

- Build the Angular app before publishing the ASP.NET app, or add that build step to your deployment pipeline.
- The published site must be reachable over HTTPS.
- If the app sits behind a proxy or tunnel, forwarded headers must preserve `X-Forwarded-Proto`.
- Identity cookies are configured with `SameSite=None` and `Secure` because Discord runs the app in a third-party iframe. Without that, browser cookie delivery is unreliable for authenticated iframe traffic.
- CSP and `frame-ancestors` are tightened for Discord origins, Discord proxy origins, localhost, and common tunnel hosts used during development.

## Discord Developer Portal steps

1. Enable Developer Mode in your Discord client for testing.
2. Create or open your Discord application.
3. Under Installation, enable both `User Install` and `Guild Install`.
4. Under Installation, keep `applications.commands` in the default install scopes.
5. Under OAuth2, add this Redirect URI placeholder exactly: `https://127.0.0.1`
6. Under Activities > Settings, enable Activities.
7. Under Activities > URL Mappings, configure:
   - Prefix: `/`
   - Target: your current public HTTPS host only, with no scheme and no path
8. Make sure the mapped host serves this app, and that `/` redirects to `/discord-activity`.
9. If you use a temporary tunnel hostname such as `trycloudflare.com`, update the URL Mapping each time the hostname changes.
10. Rotate the client secret if it was previously exposed in the repo.

## Known risks and Discord-side testing notes

- `discordSdk.ready()` will fail or time out if the Activity URL mapping or client id is wrong.
- `authorize` can fail if the app is not properly configured as an Activity in the Developer Portal.
- Third-party external requests can still fail in Discord with CSP or proxy restrictions if new external domains are introduced later.
- Cookie-based authenticated API calls from inside Discord depend on HTTPS and `SameSite=None; Secure`.
- Tunnel hostnames are included for development, but production CSP should be reviewed again once the final hostname is fixed.
- In a browser the app is the website (cookie login), not the Activity: Discord-only bits such as the connected-participants invite shortcuts stay hidden there.

## Quick start commands

Run these from the repo root in three separate terminals when testing the embedded Discord flow:

```powershell
cd discord-activity; npm run build
dotnet run --launch-profile https
.\cloudflared.exe tunnel --url http://localhost:5012
```

Update the Discord Developer Portal URL Mapping with the tunnel hostname each time it changes.
