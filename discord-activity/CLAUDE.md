# Discord Activity — styling guide (READ BEFORE ANY UI WORK)

This Angular SPA uses the same blueish **"dark-crystal / FFXI"** theme as the web app
(`wwwroot/css/lsm-theme.css`). The whole look is driven by CSS custom-property **tokens**
defined once in [`src/styles/_tokens.scss`](src/styles/_tokens.scss). Change a token's value
there and it re-skins the entire app.

## Golden rule

**Never hardcode a color.** No raw `#hex` / `rgba()` in component or partial CSS. Use a token.
If you need a color that doesn't exist yet, **add a token to `_tokens.scss`** — don't inline it.
Don't write `var(--accent, #4f7cff)` fallbacks either; every token is always defined, so the
fallback is just a second place the palette can drift. Write `var(--accent)`.

This applies to **inline `styles: [\`…\`]` blocks inside component `.ts` files too**, not just
`.scss` — those read the same `:root` tokens. (They're easy to miss in a sweep: grep `.ts` as
well as `.scss`.) Exception: the theme-preset swatch data in `configurations-tab.component.ts`
(`{ key:'Crystal', bg:'#…', accent:'#…' }`) is intentional selectable-theme data — leave it.

This is enforceable — the working tree should contain **zero** raw color literals outside the
two allowed places below. Verify with the greps at the bottom.

## The tokens (single source of truth)

Surfaces (dark→light ladder): `--bg #0d1117` · `--bg-elev #161b22` · `--surface #1f2630`
· `--surface-2 #202833` · `--surface-3 #273241`. Body has the blue/purple/gold **aurora**
radial-gradient over `--bg` (`background-attachment: fixed`).

Borders (translucent cool blue-gray — they layer over any surface): `--border` · `--border-2`
· `--border-hot` (hover). **Use these for hover borders**, not `rgba(255,255,255,.22)`.

Text: `--fg #e6edf3` (primary) · `--fg-1` · `--fg-2 #9ca3af` (muted) · `--fg-3` · `--fg-4` ·
`--muted` (= `--fg-3`, for labels/placeholders).

Accent (blue): `--accent #4f7cff` · `--accent-hover` · `--accent-weak` (fills/washes) ·
`--accent-strong #1f3bb3` (gradient bottoms) · `--accent-glow` (the **3px** focus ring:
`box-shadow: 0 0 0 3px var(--accent-glow)`).

Status: `--success #43d17a` · `--warning #f59e0b` · `--danger #ff6b6b` (+ each `-weak`).

Secondary hues: `--gold #d6a93f` · `--gold-2` · `--gold-dark` · `--gold-weak` ·
`--gold-ink #1f1605` (dark text ON gold) · `--cyan #38bdf8` · `--purple #8b5cf6`.

Multi-element palettes (keep variety, lead with blue — see convention below):
avatars `--av-a-1/-2 … --av-e-1/-2`, charts `--chart-a … --chart-f`,
dots `--dot-crafting | --dot-loot | --dot-drop | --feed-loot | --feed-claim`.

Radii: `--r-sm 6` · `--r-md 8` · `--r-lg 10` · `--r-xl 14`. Cards use `--r-xl` + a
`0 12px 30px rgba(0,0,0,.4)` shadow. Fonts: `--font-sans` (Geist) · `--font-mono`.

## Patterns

- **Form controls** are themed globally in `_tokens.scss` (text `input`/`textarea`/`select`
  get bg/border/radius/focus-ring + an SVG chevron). Just write a plain `<select>`/`<input>`;
  it matches. Checkboxes/radios are excluded — use the existing `.check` pattern.
- **Buttons** — `class="btn primary | ghost | success | danger-outline"` (`+ sm`), defined
  under `.panel-tab .btn` in `_tabs-shell.scss`. Standalone overlays must sit inside a
  `.panel-tab` container.
- **Cards** — `.card > .card-head ( .card-title + .tag accent ) + .card-body`.
- **Avatars/charts** — these intentionally keep multiple distinct hues for identity/legibility.
  Pair/segment **A is the blue accent**; the rest use the web's exact secondary hues. When you
  add a new one, add an `--av-*` / `--chart-*` token; don't inline a one-off color.

## Allowed exceptions (the ONLY raw colors permitted)

1. **Token definitions** in `_tokens.scss` — this is where the literals live.
2. **`.relic-flame`** in [`src/styles/_jobs.scss`](src/styles/_jobs.scss) — a *synced pair* with
   the web's `.lsm-page .tag.relic-flame` in `wwwroot/css/lsm-theme.css`. Its blue→purple→magenta
   aurora is deliberate. **Edit both files or neither**; don't tokenize it.
3. **Dark ink on bright fills** — text/checkmarks on accent/gold/success/warning buttons must
   stay dark: use `var(--bg)` (≈#0d1117) or `var(--gold-ink)`. Never recolor these light.
4. **`#fff` / white-alpha** for toggle/checkbox knobs, text-on-accent, button top-highlights
   (`inset 0 1px 0 rgba(255,255,255,.18)`), and hero-image timer text — keep white.

## Build & verify

```bash
cd discord-activity && npm run build      # → ../wwwroot/discord-activity (baseHref /discord-activity/)
# iterate: npm start  →  http://localhost:4200/discord-activity/
```

After any styling change, prove no stray literals slipped in (expect **zero** matches):

```bash
cd discord-activity && grep -rniE \
  '#818cf8|#6366f1|#7c5cff|#a855f7|#8d91ff|#8d92ff|#6268ff|#c6a8ff|#9b6cff|#c084fc|#10b981|#ef4444|#60a5fa|#f87171|#e05a5a|rgba\(\s*124,\s*92,\s*255|rgba\(\s*99,\s*102,\s*241|rgba\(\s*129,\s*140,\s*248|rgba\(\s*139,\s*92,\s*246' \
  src --include='*.scss' --include='*.ts' | grep -vE '_jobs.scss|_tokens.scss|configurations-tab.component.ts'
```
