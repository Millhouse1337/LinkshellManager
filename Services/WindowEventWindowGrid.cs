using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The one place a WindowEvent's spawn grid is read.
//
// A camp captures its grid at creation (WindowEvent.WindowCount / WindowMinutes) from the
// linkshell's monster setup; rows that predate those columns — and monsters with no grid at all —
// fall through to HnmConfig, which is exactly how this behaved before setups were configurable.
//
// It lives in one helper because three call sites derive a window number from the same grid
// (snapshot ingestion, the window cap on an explicitly chosen window, and the display-side
// AttendanceSectionsBuilder). If they resolved it independently, a camp's history could be
// numbered one way on the way in and another way on the way out.
public static class WindowEventWindowGrid
{
    // Minutes per window, or 0 when this camp runs no timed grid.
    public static int Minutes(WindowEvent windowEvent) =>
        windowEvent.WindowMinutes is > 0
            ? windowEvent.WindowMinutes.Value
            : HnmConfig.DefaultWindowCadence(windowEvent.Name)?.Minutes ?? 0;

    // How many windows the camp runs. Always at least 1.
    public static int WindowCount(WindowEvent windowEvent) =>
        Math.Clamp(
            windowEvent.WindowCount
                ?? HnmConfig.DefaultWindowCadence(windowEvent.Name)?.Windows
                ?? HnmConfig.GetWindowCount(windowEvent.Name),
            1, HnmConfig.MaxWindow);

    // The window a capture belongs to, or null when the camp has no grid at all. Null is different
    // from window 1: it means "this camp has no windows", so the UI shows no window tag rather than
    // claiming everything happened in the first one.
    public static int? SnapshotWindowNumber(WindowEvent windowEvent, DateTime capturedAtUtc)
    {
        var minutes = Minutes(windowEvent);
        if (minutes <= 0)
        {
            return null;
        }
        return HnmConfig.WindowNumberAt(
            windowEvent.WindowGridAnchorUtc, capturedAtUtc, minutes, WindowCount(windowEvent));
    }

    // How close two posts must be to count as one capture of the same roster, scaled to this
    // camp's own window length rather than to the monster's built-in one.
    public static TimeSpan MergeWindow(WindowEvent windowEvent) =>
        HnmConfig.SnapshotMergeWindow(Minutes(windowEvent));
}
