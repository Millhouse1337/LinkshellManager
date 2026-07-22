using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

// The "review before commit" screen for a DKP import: the classified preview rows
// plus the parsed rows serialized as JSON, round-tripped into the confirm POST so
// the officer doesn't have to re-upload the file. The commit re-classifies these
// rows server-side against the current roster, so the JSON is only trusted for the
// raw parsed values (which the officer could set by hand anyway).
public sealed class DkpImportPreviewViewModel
{
    public int LinkshellId { get; set; }
    public string LinkshellName { get; set; } = string.Empty;
    public DkpImportPreview Preview { get; set; } = default!;
    public string RowsJson { get; set; } = string.Empty;
}
