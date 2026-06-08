using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellSheetViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public string? SpreadsheetId { get; set; }
    // True when SpreadsheetId is a sheet the app created (the dedicated "LSM DKP"
    // sheet). False for legacy externally-pasted ids — the UI nudges those to
    // create a dedicated sheet so the app needs only per-file Google access.
    public bool GoogleSheetAppCreated { get; set; }
    public bool IsOAuthConfigured { get; set; }
    public bool IsOAuthConnected { get; set; }
    public string? ConnectedGoogleEmail { get; set; }
    // ConnectedAt is stored UTC; ConnectedAtUserLocal is the same instant
    // converted to the current viewer's profile timezone (Profile -> Time
    // zone). ConnectedAtTimeZoneLabel is the short tag we render next to
    // the formatted timestamp ("EDT", "UTC", ...). Both populated by the
    // controller so the view doesn't have to know about TimeZoneInfo.
    public DateTime? ConnectedAt { get; set; }
    public DateTime? ConnectedAtUserLocal { get; set; }
    public string? ConnectedAtTimeZoneLabel { get; set; }

    // The tab the generic DKP template exports to / imports from (default "LSM DKP").
    public string? DkpTemplateTabName { get; set; }

    // Live sync (push-only): when on, the LSM DKP tab auto-refreshes on every
    // DKP change. Import (sheet → app) stays manual.
    public bool LiveSyncEnabled { get; set; }

    // Populated by Import preview: each template row matched against the roster.
    public DkpTemplatePreview? TemplatePreview { get; set; }
}
