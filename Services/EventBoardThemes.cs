namespace LinkshellManagerDiscordApp.Services;

// The single source of truth for the event-board image's colour themes. Each
// theme is purely a palette swap: only the per-theme CSS custom properties
// differ — every structural rule in EventBoardHtmlBuilder is shared. A theme's
// RootPalette is dropped verbatim into the card's <style> :root block.
//
// The fixed role colours (--tank/--heal/--supp/--dps), the fonts, and the
// neutral --any are theme-INDEPENDENT and live in the builder, NOT here.
//
// Keys are stored on Linkshell.EventBoardTheme. Resolve() guarantees a render
// can never break on an unknown/blank value (it falls back to Default), so both
// the builder and the settings validators route through it.
public sealed record EventBoardTheme(
    string Key,
    string DisplayName,
    string SwatchBg,
    string SwatchAccent,
    string RootPalette);

public static class EventBoardThemes
{
    public const string Default = "Crystal";

    // Order is the order shown in the picker. Palettes copied verbatim from
    // themes/FFXI All Themes.html (Crystal = the base :root; the rest = the
    // body[data-theme="..."] override blocks).
    public static readonly IReadOnlyList<EventBoardTheme> All = new[]
    {
        new EventBoardTheme("Crystal", "Crystal", "#0b0e1a", "#d8b86a", """
    --page-bg:#05070d;
    --bg-grad:radial-gradient(130% 90% at 50% -10%,#1a2138 0%,#0b0e1a 55%,#080a12 100%);
    --accent:#d8b86a;--accent-bright:#f0d488;--accent-deep:#c9a554;
    --eyebrow:#6fb7ff;--glow:rgba(111,183,255,.5);--title-shadow:0 2px 18px rgba(111,183,255,.25);
    --txt:#f3ecda;--soft:#e9e2cf;--muted:#aeb6c9;--dim:#8a8f9e;--vacant:#6a6f82;--name:#c9d2e6;
    --line:rgba(216,184,106,.12);--tint:rgba(216,184,106,.03);--slot-line:rgba(216,184,106,.06);
"""),
        new EventBoardTheme("Abyss", "Abyss", "#041c24", "#3fd9e6", """
    --page-bg:#01070a;
    --bg-grad:radial-gradient(130% 90% at 50% -10%,#063843 0%,#041c24 55%,#020a0d 100%);
    --accent:#3fd9e6;--accent-bright:#7fe9f2;--accent-deep:#1fa8b8;
    --eyebrow:#3fd9e6;--glow:rgba(63,217,230,.5);--title-shadow:0 2px 18px rgba(63,217,230,.25);
    --txt:#e3f7fa;--soft:#cdeef2;--muted:#8fb6bd;--dim:#5f8088;--vacant:#456066;--name:#bfeaf0;
    --line:rgba(63,217,230,.14);--tint:rgba(63,217,230,.035);--slot-line:rgba(63,217,230,.07);
"""),
        new EventBoardTheme("Ember", "Ember", "#170a05", "#f0883e", """
    --page-bg:#0a0503;
    --bg-grad:radial-gradient(130% 90% at 50% -10%,#2c1308 0%,#170a05 55%,#0a0503 100%);
    --accent:#f0883e;--accent-bright:#ffb066;--accent-deep:#d4661f;
    --eyebrow:#ff8a3c;--glow:rgba(240,136,62,.55);--title-shadow:0 2px 18px rgba(240,136,62,.3);
    --txt:#f9e9d9;--soft:#f0dcc8;--muted:#c9a890;--dim:#9c7a60;--vacant:#6e5340;--name:#f0dcc8;
    --line:rgba(240,136,62,.14);--tint:rgba(240,136,62,.04);--slot-line:rgba(240,136,62,.07);
"""),
        new EventBoardTheme("Verdant", "Verdant", "#07150f", "#cda86a", """
    --page-bg:#030a07;
    --bg-grad:radial-gradient(130% 90% at 50% -10%,#0f2c23 0%,#07150f 55%,#030a07 100%);
    --accent:#cda86a;--accent-bright:#e8c98c;--accent-deep:#b08f52;
    --eyebrow:#6fd8a8;--glow:rgba(111,216,168,.45);--title-shadow:0 2px 18px rgba(111,216,168,.22);
    --txt:#e9f1e5;--soft:#d6e6d0;--muted:#9fb8a4;--dim:#6f8a74;--vacant:#4f6a54;--name:#cfe6d6;
    --line:rgba(205,168,106,.14);--tint:rgba(205,168,106,.04);--slot-line:rgba(205,168,106,.07);
"""),
        new EventBoardTheme("Royal", "Royal", "#150a24", "#c9a8f0", """
    --page-bg:#0a0514;
    --bg-grad:radial-gradient(130% 90% at 50% -10%,#251636 0%,#150a24 55%,#0a0514 100%);
    --accent:#c9a8f0;--accent-bright:#e3d2f7;--accent-deep:#a87fd6;
    --eyebrow:#c9a8f0;--glow:rgba(201,168,240,.5);--title-shadow:0 2px 18px rgba(201,168,240,.25);
    --txt:#efe7f8;--soft:#e2d6f2;--muted:#b3a6c9;--dim:#857a9c;--vacant:#665c7c;--name:#e2d6f2;
    --line:rgba(201,168,240,.14);--tint:rgba(201,168,240,.035);--slot-line:rgba(201,168,240,.07);
"""),
        new EventBoardTheme("Tome", "Tome", "#e8d6ad", "#8a3522", """
    --page-bg:#241c10;
    --bg-grad:radial-gradient(130% 90% at 50% -10%,#f4e8cd 0%,#e8d6ad 58%,#dbc492 100%);
    --accent:#8a3522;--accent-bright:#b04a2f;--accent-deep:#7a2a1a;
    --eyebrow:#9a4326;--glow:rgba(138,53,34,.35);--title-shadow:none;
    --txt:#3a2c1a;--soft:#4a3826;--muted:#6b5740;--dim:#9a8662;--vacant:#b09a78;--name:#5a4226;
    --line:rgba(122,46,34,.2);--tint:rgba(122,46,34,.05);--slot-line:rgba(122,46,34,.12);
"""),
    };

    // Normalises a stored/posted key to a known theme key (case-insensitive),
    // falling back to Default for null/blank/unknown values.
    public static string Resolve(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var match = All.FirstOrDefault(t => string.Equals(t.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Key;
            }
        }
        return Default;
    }

    // The :root palette CSS for a (resolved) theme key.
    public static string PaletteFor(string? key)
    {
        var resolved = Resolve(key);
        return All.First(t => t.Key == resolved).RootPalette;
    }
}
