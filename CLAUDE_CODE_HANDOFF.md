# LinkshellManager — UI/UX Overhaul Handoff

## Project overview
This is a hybrid ASP.NET Core MVC + Angular app for managing Final Fantasy XI (FFXI) Linkshell communities.
It handles events, DKP, Time of Death (ToD) tracking, auctions, loot, announcements, and member management.
It also runs as a Discord Activity (embedded iframe) using an Angular SPA in `discord-activity/src/`.

## What was decided
A full UI/UX overhaul was designed and approved through several mockup iterations.
The approved design is called **v3** and lives at `mockup-dashboard-v3.html` in the project root.
The monster artwork used in the mockup is already in `wwwroot/ffxi_assets/`.

## Design system (from the approved mockup)

### Fonts
- **Geist** for all UI text (headings, labels, body)
- **Geist Mono** for all numeric data (DKP values, timers, timestamps, counts)
- Load both from Google Fonts:
  `https://fonts.googleapis.com/css2?family=Geist:wght@300;400;500;600;700&family=Geist+Mono:wght@400;500;600&display=swap`

### CSS variables (copy these exactly into site.css)
```css
:root {
  /* Surfaces */
  --bg:        #0a0a0b;
  --bg-elev:   #101012;
  --surface:   #141417;
  --surface-2: #1a1a1e;
  --surface-3: #212126;

  /* Borders */
  --border:     rgba(255, 255, 255, 0.06);
  --border-2:   rgba(255, 255, 255, 0.10);
  --border-hot: rgba(255, 255, 255, 0.18);

  /* Text */
  --fg:       #fafafa;
  --fg-1:     #e4e4e7;
  --fg-2:     #a1a1aa;
  --fg-3:     #71717a;
  --fg-4:     #52525b;

  /* Accent — single indigo */
  --accent:       #818cf8;
  --accent-hover: #a5acf9;
  --accent-weak:  rgba(129, 140, 248, 0.12);

  /* Semantic */
  --success:      #4ade80;
  --success-weak: rgba(74, 222, 128, 0.12);
  --warning:      #f59e0b;
  --warning-weak: rgba(245, 158, 11, 0.12);
  --danger:       #f87171;
  --danger-weak:  rgba(248, 113, 113, 0.12);

  /* Rarity — for loot items */
  --r-common:   #a1a1aa;
  --r-uncommon: #4ade80;
  --r-rare:     #60a5fa;
  --r-epic:     #c084fc;
  --r-mythic:   #fb923c;

  /* Typography */
  --font-sans: 'Geist', system-ui, sans-serif;
  --font-mono: 'Geist Mono', ui-monospace, monospace;

  /* Border radius */
  --r-sm: 4px;
  --r-md: 6px;
  --r-lg: 8px;
  --r-xl: 12px;
}
```

### Key UI patterns to apply everywhere
- `font-family: var(--font-mono)` on ALL numeric values (DKP, times, counts, money)
- `background: var(--bg)` on body; `background: var(--bg-elev)` on sidebar and cards
- Cards: `border: 1px solid var(--border); border-radius: var(--r-lg); background: var(--bg-elev);`
- Status tags: `.tag.success`, `.tag.warning`, `.tag.danger`, `.tag.accent` — see mockup for styles
- Rarity left-rails on loot rows (2px wide, colored by rarity)
- Monster portraits from `~/ffxi_assets/HNM/`, `~/ffxi_assets/Sky/`, `~/ffxi_assets/Sea/`

---

## Layout structure
Replace the current horizontal top navbar with a **persistent left sidebar** layout.

### App shell grid
```css
.app {
  display: grid;
  grid-template-columns: 248px 1fr;
  min-height: 100vh;
}
```

### Sidebar structure (top to bottom)
1. Brand mark + "LinkshellManager" wordmark
2. Linkshell switcher pill (shows active linkshell name + user role)
3. Nav sections with collapsible groups:

**No section label:**
- Dashboard
- Announcements ▸ View Announcements / + Create Announcement
- Rules ▸ View Rules / + Create Rule
- Missions ▸ Rise of the Zilart / Chains of Promathia / Treasures of Aht Urhgan

**Endgame:**
- End Game ▸ Sky / Sea / HNM / Dynamis / Limbus
- Linkshell Auction (count badge)
- Event System ▸ View Events / + Create Event / Event History
- ToDs ▸ View ToDs / + Add ToD (warning-colored count badge when windows open)

**Management:**
- Manage Team ▸ View Team / + Add Members / View Invites
- Manage Items ▸ View Items / + Add Item
- Manage Revenue ▸ View Income / + Add Income
- Configurations ▸ View Linkshells / + Create Linkshell / Customize Linkshell

**Pages:**
- Profile
- Messages (accent-colored unread count badge)

### Create/Add sub-items
Items that create something use a `+` glyph prefix and turn indigo on hover.
View/history items use the standard bullet dot.

### Topbar (sticky, 48px tall)
- Left: breadcrumb (`Linkshell Name / Page Name`)
- Center: search bar with ⌘K hint
- Right: notification bell (with accent dot when unread), user avatar pill

---

## Implementation plan — do these in order

### Phase 1 — Design tokens
- Add Google Fonts import to `Views/Shared/_Layout.cshtml` `<head>`
- Add all CSS variables above to `wwwroot/css/site.css`
- Set `body { font-family: var(--font-sans); background: var(--bg); color: var(--fg-1); }`

### Phase 2 — Shared shell
- Rewrite `Views/Shared/_Layout.cshtml` — remove Bootstrap navbar, add sidebar grid shell
- Rewrite `Views/Shared/_Sidebar.cshtml` — implement full nav structure above
- The `_Layout.cshtml` should:
  - Wrap everything in `<div class="app">`
  - Include `<partial name="_Sidebar" />` for authenticated users
  - Render a `<div class="main">` containing topbar + `<div class="content">@RenderBody()</div>`

### Phase 3 — Page views (in this order)
1. `Views/Dashboard/Index.cshtml`
2. `Views/ToD/Index.cshtml`
3. `Views/Event/` — all views
4. `Views/Auction/` + `Views/AuctionHistory/`
5. `Views/DkpHistory/`
6. `Views/EventHistory/`
7. `Views/Linkshell/`
8. `Views/ManageTeam/`, `Views/ManageItem/`, `Views/ManageRevenue/`
9. `Views/Announcement/`, `Views/Rule/`
10. `Views/Account/` — Profile, Settings, Login, Register
11. `Views/Home/Index.cshtml` — landing page
12. `Views/Admin/`

### Phase 4 — Discord Activity parity
- Port CSS variables into `discord-activity/src/styles.scss`
- The Angular SPA lives at `/discord-activity` and is an embedded iframe inside Discord
- It should use the same color tokens, fonts, and component patterns as the web app
- Key constraint: it runs in a narrow iframe — design for ~480px width minimum

---

## Important constraints
- The app uses Bootstrap 5 (`~/lib/bootstrap/`) — keep Bootstrap for grid/utilities but override
  all visual styles (colors, borders, shadows, radius) with the new design tokens
- Do NOT remove Bootstrap JS — modals and dropdowns depend on it
- The `wwwroot/bootstrap_template/` folder is a legacy template — do not use it
- Identity cookies use `SameSite=None; Secure` for Discord iframe compatibility — don't touch auth
- CSP headers are configured for Discord — don't add new external CDN domains without checking `Program.cs`
- Geist font is already cleared for use (it's served by Google Fonts which is in the CSP allowlist)

---

## Reference
- Approved mockup: `mockup-dashboard-v3.html` (in project root — open this in a browser to see the target)
- Monster art: `wwwroot/ffxi_assets/HNM/`, `wwwroot/ffxi_assets/Sky/`, `wwwroot/ffxi_assets/Sea/`, `wwwroot/ffxi_assets/Other/`
- Current layout: `Views/Shared/_Layout.cshtml`
- Current sidebar: `Views/Shared/_Sidebar.cshtml`
