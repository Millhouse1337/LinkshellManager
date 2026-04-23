# LinkshellManager Discord Activity

## Architecture

- Root app: ASP.NET Core MVC + Razor Pages + Identity + EF Core + PostgreSQL
- Activity frontend: Angular app in `discord-activity`
- Angular build output: `wwwroot/discord-activity/browser`
- Activity route: ASP.NET serves the Angular SPA at `/discord-activity`
- Discord auth exchange endpoint: `POST /auth/discord/exchange`
- Local identity store: `DiscordActivityUsers` linked to `AspNetUsers` (`AppUser`) for the restored MVC slice

The architecture is intentionally hybrid. The ASP.NET app remains the host, security boundary, and configuration source. The Angular app remains a separate frontend project that is built into `wwwroot`.

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

Outside Discord, the Angular app intentionally falls back to a standalone preview mode so `/discord-activity` can still be validated locally in a normal browser.

## Auth flow

- The frontend does not own the Discord client secret.
- The backend exchanges the auth code with `https://discord.com/api/oauth2/token`.
- The backend uses the returned access token to fetch `https://discord.com/api/users/@me`.
- The backend creates or updates a local `DiscordActivityUsers` row keyed to the Discord user id.
- The backend returns the access token payload needed for the embedded `authenticate` step and the local user summary.
- The frontend then authenticates with the Embedded App SDK and uses `GET /api/me` for a database-backed user record.

## MVC first slice

The working Discord Activity shell now sits beside a restored first-slice MVC app.

Restored server-rendered areas in this pass:

- `Dashboard`
- `Linkshell`
- `Event`
- `EventHistory`
- `Account/Profile`
- `Account/Settings`

Design constraints for this restoration:

- `AppUser` is now the primary ASP.NET Identity user type.
- Discord launch/auth stays on the existing `POST /auth/discord/exchange` and `GET /api/me` flow.
- The MVC slice uses the same host, same cookies, same PostgreSQL database, and same CSP/frame policy as the Discord Activity host.
- Non-priority legacy modules such as Auctions, TOD, Admin, Contact/Messaging, Rule management, and revenue/item management are intentionally excluded from compilation in this pass.

## Local user model

The app now has two linked user layers:

1. Discord Activity identity
   - stored in `DiscordActivityUsers`
   - keyed by `DiscordUserId`
   - updated on each embedded Activity launch

2. Internal app identity
   - stored in `AspNetUsers` using the custom `AppUser` type
   - used by the restored MVC linkshell/event flows
   - linked from `DiscordActivityUsers.IdentityUserId`

Current `AppUser` fields used by the restored slice:

- `Id`
- `UserName`
- `CharacterName`
- `TimeZone`
- `PrimaryLinkshellId`
- `PrimaryLinkshellName`
- `ProfileImage`

Current `DiscordActivityUsers` fields:

- `Id`
- `DiscordUserId`
- `Username`
- `Discriminator`
- `GlobalName`
- `Avatar`
- `IdentityUserId`
- `CreatedAtUtc`
- `LastSeenAtUtc`

Provisioning behavior:

- First Discord launch creates or updates the `DiscordActivityUsers` row.
- If no linked `AppUser` exists yet, the backend creates one and stores its id in `IdentityUserId`.
- `GET /api/me` now returns both the Discord-linked local user record and the linked `AppUser` summary needed by the restored website features.
- Linkshells, memberships, live events, completed event history, and notifications are all keyed from the `AppUser` layer.

Important: for the embedded Activity flow, the backend does not send `redirect_uri` during token exchange. Discord�s current Activity tutorial shows the embedded flow without `redirect_uri`; the Redirect URI still needs to exist in the Developer Portal, but it is a portal requirement rather than a request parameter used by this app flow.

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

Set the PostgreSQL connection string with user secrets if your local credentials differ from the sample in `appsettings.json`:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=linkshell_manager_discord_app;Username=postgres;Password=<your-password>"
```

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

Create the database and apply the EF migration:

```bash
"$env:USERPROFILE\\.dotnet\\tools\\dotnet-ef.exe" database update
```

The local Activity user table is added by migration:

- `20260416225941_AddDiscordActivityUsers`

The default repo connection string targets:

- host: `localhost`
- port: `5432`
- database: `linkshell_manager_discord_app`
- username: `postgres`

### Run the ASP.NET app

From the repo root:

```bash
dotnet run --launch-profile https
```

Use the `https` profile. The Activity route should be available at:

- `https://localhost:7051/discord-activity`

### Local route checks

- Open `/discord-activity` in a normal browser and confirm the standalone preview shell loads.
- Open a deep route such as `/discord-activity/test-route` and confirm the SPA fallback returns the Angular app.
- Rebuild Angular after frontend changes before retesting through ASP.NET.

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
- The standalone browser preview is intentionally not a full Discord-authenticated experience.

cd discord-activity npm run build
dotnet run
./cloudflared.exe tunnel --url http://localhost:5012
