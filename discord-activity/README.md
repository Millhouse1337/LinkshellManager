# LinkshellManager Frontend

The Angular app lives in this folder and builds into `../wwwroot/discord-activity`. It is the only
UI: inside Discord it runs as the Discord Activity, and in a normal browser it is the website
(`DiscordActivityService.isWebsite`; website-only tabs are gated by `ActivityHomeComponent.canOpenTab`).

Primary workflow:

```bash
npm install
npm run lint
npm run build
```

The built files are served by the ASP.NET Core host at `/discord-activity`.

See the repo root [README](../README.md) for the full architecture, website mode, auth flow, local
setup, Discord Developer Portal steps, and deployment notes, and [CLAUDE.md](CLAUDE.md) for the
styling rules (colour tokens only).
