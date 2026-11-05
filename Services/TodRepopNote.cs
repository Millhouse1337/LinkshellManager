namespace LinkshellManagerDiscordApp.Services;

// How a repop was worked out, in words, for the surfaces that tell an officer why a board starts at
// 11:00:16 rather than 11:00:00. The Angular ToD form and the addon print the same formula.
//
// Null when the monster has no Additional Seconds: every surface then says nothing, because a repop
// that is simply ToD + cooldown has nothing to explain.
public static class TodRepopNote
{
    // "ToD + 22 Hour + 16 additional seconds"
    public static string? Formula(string? cooldown, int additionalSeconds)
    {
        if (additionalSeconds <= 0)
        {
            return null;
        }

        var seconds = additionalSeconds == 1
            ? "1 additional second"
            : $"{additionalSeconds} additional seconds";
        return string.IsNullOrWhiteSpace(cooldown)
            ? $"ToD + cooldown + {seconds}"
            : $"ToD + {cooldown.Trim()} + {seconds}";
    }
}
