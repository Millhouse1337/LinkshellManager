using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The day cycle of the three NQ/HQ families -- Fafnir/Nidhogg, Adamantoise/Aspidochelone,
// Behemoth/King Behemoth. Each NQ kill moves the next board on a day; the HQ kill starts the
// cycle over at Day 1 (HnmConfig.NextDayNumber holds that rule). This class answers the two
// questions around it: which day a ToD records, and whether it was the HQ.
public static class HnmDayCycle
{
    // Was this ToD the HQ? The toggle, OR a name that is the stronger half on its own: the addon
    // posts the name off the kill message ("Nidhogg"), and has no toggle to set. Reading only the
    // toggle missed every HQ posted from the game, and the cycle never went back to Day 1.
    public static bool WasHq(Tod tod) =>
        HnmConfig.ResolveClaimHalf(tod.MonsterName, tod.Hq).IsHq;

    // The day the NEXT board carries after this ToD. A ToD that doesn't say its day (left blank on
    // the ToD form) counts as the day the board itself was on, so the board still moves on a day
    // instead of losing its day altogether. boardDay is null when there is no board yet.
    public static int? NextDay(Tod tod, int? boardDay) =>
        HnmConfig.NextDayNumber(tod.DayNumber ?? boardDay, WasHq(tod));

    // The day a parked board comes back on: what an officer typed on it, else the next in the cycle.
    public static int? NextDayForBoard(Event board, Tod tod) =>
        board.HnmNextDayOverride ?? NextDay(tod, board.DayNumber);

    // The day an ADDON-posted ToD records. The live camp for the spawn wins, because that is where
    // the day lives -- an officer set it on the board, and the board is what moves it on. The
    // addon's ToD Capture panel sends the PREVIOUS ToD's day, carried forward, so every kill of a
    // cycle was recorded as the same day and the board never got past it.
    //
    // No live camp for the spawn, or one without a day: what the addon sent.
    public static async Task<int?> DayForAddonTodAsync(
        ApplicationDbContext db, int linkshellId, string monsterName, int? requestedDay,
        CancellationToken cancellationToken)
    {
        if (!HnmConfig.HasHqVariant(monsterName))
        {
            return requestedDay;
        }

        var names = HnmConfig.MonsterMatchNamesLower(monsterName);
        var campDay = await db.Events
            .Where(e => e.LinkshellId == linkshellId
                && e.EndTime == null
                && e.CommencementStartTime != null
                && e.HnmDefeatedAt == null
                && e.DayNumber != null
                && e.DayNumber > 0
                && e.AssignedMonsterName != null
                && names.Contains(e.AssignedMonsterName.ToLower()))
            .OrderByDescending(e => e.CommencementStartTime)
            .Select(e => e.DayNumber)
            .FirstOrDefaultAsync(cancellationToken);

        return campDay ?? requestedDay;
    }
}
