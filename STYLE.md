# LSManager UI style guide

Read this before touching any UI. Its purpose is to stop CSS inconsistencies
(e.g. native-looking `<select>`/`<input>` next to themed ones). There are **two**
front-ends; use the tokens/classes of whichever you're in.

## Golden rules

1. **Never hardcode colors.** Always use the design tokens (CSS variables) below.
   No raw hex like `#1a1a1e` or `rgba(255,255,255,.06)` in markup or component CSS.
2. **Never assume an `<input>`/`<select>` "looks right" on its own** — but you no
   longer have to: both apps now theme text inputs/selects/textareas **globally by
   default** (see "Form controls"). Don't re-style them per component; just use a
   plain `<select>`/`<input>`. Only override via an existing wrapper class.
3. **Use the existing button classes**, never bare `<button>` styling.
4. **Match the surrounding card/section markup** — don't invent new card chrome.

## Angular Discord Activity (`discord-activity/`)

**Read [`discord-activity/CLAUDE.md`](discord-activity/CLAUDE.md) first** — it is the full,
authoritative theme guide (auto-loaded when working in that folder). The Activity now uses the
same blueish dark-crystal palette as the web (`--bg #0d1117`, `--accent #4f7cff`, blue/purple/gold
aurora background). Summary below.

**Tokens** — `src/styles/_tokens.scss` (values match the web `lsm-theme.css`):
surfaces `--bg #0d1117 --bg-elev #161b22 --surface #1f2630 --surface-2 --surface-3` ·
translucent blue-gray borders `--border --border-2 --border-hot` · text `--fg #e6edf3 …
--fg-4`, `--muted` · `--accent #4f7cff --accent-hover --accent-weak --accent-strong #1f3bb3
--accent-glow` (3px focus ring) · `--success #43d17a --warning --danger #ff6b6b` (+ `-weak`) ·
`--gold --gold-2 --gold-ink --cyan --purple` · avatar `--av-*` / chart `--chart-*` / dot
`--dot-*` palettes · radii `--r-sm(6) --r-md(8) --r-lg(10) --r-xl(14)`. Cards use `--r-xl` + a
`0 12px 30px rgba(0,0,0,.4)` shadow. **Never inline a color or a `var(--x, #fallback)`** — add
a token. Exceptions: `_tokens.scss` defs, the synced `.relic-flame` in `_jobs.scss`, dark ink on
bright fills (`var(--bg)`/`--gold-ink`), and white knobs/highlights.

**Form controls** — themed globally in `_tokens.scss` (text-like `input`, `textarea`,
`select` get bg/border/radius/focus-ring + an SVG chevron for selects). Just write a
plain `<select>`/`<input>`; it will match. Checkboxes/radios are **excluded** — for the
custom accent checkbox use the `.check` pattern (see `configurations-tab.component.scss`).

**Buttons** — `class="btn primary | ghost | warn | danger-outline"`, add `sm` for compact.
Defined under `.panel-tab .btn` in `src/styles/_tabs-shell.scss` (works inside a
`.panel-tab`; standalone overlays must be wrapped in a `.panel-tab` container).

**Cards** — `<div class="card"><div class="card-head"><div class="card-title">…</div>
<span class="tag accent">…</span></div><div class="card-body">…</div></div>`.
Pills/badges: `.tag`, `.tag accent`, `.tag success`, or the `.pill is-success/is-muted`
pattern in `configurations-tab.component.scss`.

## Web MVC (Razor, `Views/…`, `wwwroot/css/lsm-theme.css`)

Pages set `ViewData["BodyClass"]="lsm-page"`; styling lives in `wwwroot/css/lsm-theme.css`
(re-themes `.card .btn .table .input .tag` over `site.css`). Use `class="btn primary | ghost
| danger | gold"`, `class="card"`, `class="input"`/`class="form-select"` for controls,
tokens `var(--fg) var(--fg-3) var(--border) var(--surface) var(--accent)`. Match the
existing card markup on the page; don't paste Activity classes into Razor.

## When adding a new control/section
- Reuse an existing component/partial that already looks right; copy its classes.
- If a control still looks native, the fix belongs in the **base layer** (`_tokens.scss`
  / `lsm-theme.css`), not a one-off inline style — so every future control benefits.
- Inline `style="…"` is fine for **layout** (flex/grid/gap/margins), not for **theming**
  (colors/borders/control chrome).
