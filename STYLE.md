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

**Tokens** — `src/styles/_tokens.scss`:
`--bg --bg-elev --surface --surface-2 --surface-3` · borders `--border --border-2 --border-hot`
· text `--fg --fg-1 --fg-2 --fg-3 --fg-4` · `--accent --accent-hover --accent-weak`
· `--success --success-weak --danger --danger-weak` · radii `--r-sm(4) --r-md(6)`.

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
