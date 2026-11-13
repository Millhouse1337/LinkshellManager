# Website and Discord Activity: one UI, two hosts

This used to be a parity checklist: the website (Razor MVC) and the Discord Activity (Angular) were
two front ends for the same data, and this file tracked where they had drifted apart. Since
October 2026 there is one front end. The Angular app is the Discord Activity inside Discord and the
website in a normal browser, so data, actions, permissions and outcomes are the same by construction.

What remains is a short list of **intentional** differences between the two hosts.

## What differs by host

| | Website (browser) | Discord Activity (iframe) |
|---|---|---|
| Sign-in | Site cookie, via `/auth/discord/login` (Discord OAuth, `identify`). Sign-in screen when signed out, Sign out button. | Discord Embedded App SDK `authorize` → `/auth/discord/exchange`; bearer token on every call. |
| Linkshell "Lock to this server" | Not applied: members always see their linkshells. Claiming a placeholder still respects it. | Applied: a locked linkshell only opens from its server, for its members. |
| URL | One URL per tab and sub-view (`/discord-activity/events/past`), `?ls=<id>` switches linkshell; reload, Back and deep links work. | Never changed; the tab lives in memory (a stateful iframe URL interacts badly with the SDK's URL mapping). |
| Website-only | Super-admin Admin and Data Admin tabs, Privacy/Terms/launcher footer. (Reviewing members' ToD submissions on the ToDs tab and /lsm now captures under Event System → Current is on both hosts.) | Hidden (`isWebsite`, `canOpenTab`). |
| Downloads | DKP Sheet Export to Excel and the Import DKP template download directly. | Can't save a file: each opens in the member's browser through a one-time link (`DownloadLinkService`, `ActivityOverviewController.Downloads`). Import DKP uploads as base64 JSON, the same on both. |
| Discord-only | Hidden. | Connected-participants list and "connected now" invite shortcuts, "Set to this server" from the launching guild. |
| Public pages | `/` landing (signed out), `/features`, `/addon`, Privacy, Terms and the sign-in page are server-rendered (`_PublicLayout`). | Not used. |

## Permissions

One set of rules everywhere: the role permission flags, checked by `ActivityDataController.CanAsync`
(admin override first, then the Leader always passes, then the role's flag). The old website checked
the Leader/Officer **rank** in about a dozen places, so moving it onto the app changed three things
for the default Officer role, which doesn't carry these flags:

- changing members' rank or status (`CanManageMembers`)
- manual DKP adjustments (`CanAuditDkp`)
- Customize linkshell, including DKP import (`CanCustomizeLinkshell`)

A leader grants them on Configurations → Permissions if their officers should keep them.

The game addon has its own gate for acting on the linkshell's behalf: **Manage attendance with the
addon** (`CanManageAttendanceWithAddon`), checked by `AddonApiController.TokenIssuerCanModerateAsync`.
It replaced `CanModerateLiveEvent` there (the migration copied each role's value across), so a member
can keep attendance in game without officer rights in the app. Attendance is no longer queued for
approval; only a `/lsm now` capture from someone without it waits, as a Pending snapshot.

## Adding a feature

Build it once, in the Angular app. If it only makes sense in a browser (a file upload or download, a
super-admin tool), gate it with `DiscordActivityService.isWebsite`, and a whole tab with
`ActivityHomeComponent.canOpenTab`. Don't add Razor pages for app features.

## The old website

The old Razor pages were deleted in October 2026. Their GET addresses redirect permanently (301) into
the app (`Utils/LegacyUrlRedirects.cs`). That includes Data Admin: `/data-admin/...` goes to the app's
Data Admin tab, which draws the same schema diagram from the shared script and stylesheet.
