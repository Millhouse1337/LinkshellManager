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
| Website-only | Pending Submissions review tab, super-admin Admin tab, DKP Import, DKP Sheet Export to Excel, Privacy/Terms/launcher footer. | Hidden (`isWebsite`, `canOpenTab`). The DKP rounding card points to the website for importing. |
| Discord-only | Hidden. | Connected-participants list and "connected now" invite shortcuts, "Set to this server" from the launching guild. |
| Public pages | `/` landing (signed out), Privacy, Terms are server-rendered (`_PublicLayout`). | Not used. |

## Permissions

One set of rules everywhere: the role permission flags, checked by `ActivityDataController.CanAsync`
(admin override first, then the Leader always passes, then the role's flag). The old website checked
the Leader/Officer **rank** in about a dozen places, so moving it onto the app changed three things
for the default Officer role, which doesn't carry these flags:

- changing members' rank or status (`CanManageMembers`)
- manual DKP adjustments (`CanAuditDkp`)
- Customize linkshell, including DKP import (`CanCustomizeLinkshell`)

A leader grants them on the Permissions tab if their officers should keep them.

## Adding a feature

Build it once, in the Angular app. If it only makes sense in a browser (a file upload or download, a
super-admin tool), gate it with `DiscordActivityService.isWebsite`, and a whole tab with
`ActivityHomeComponent.canOpenTab`. Don't add Razor pages for app features.

## Retiring the old website

The old Razor pages' GET addresses redirect into the app (`Utils/LegacyUrlRedirects.cs`, 302 for
now). Their controllers and views stay until a cleanup pass deletes them and switches the redirects to
301. Data Admin (`/data-admin`) is still a Razor page, linked from the Admin tab, until it is rebuilt
in the app.
