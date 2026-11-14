using System.Text.Json;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// The shared base of the Discord bot's handler classes and shared services: the per-request
// interaction (who clicked what), and the small reply/parse helpers they all use. It plays the
// part ControllerBase played while all of this lived in DiscordInteractionsController — Ok(...)
// builds the same OkObjectResult — so the moved code reads the same as it did there.
//
// The helpers are static and internal so the controller can use them too.
public abstract class DiscordInteractionHandlerBase
{
    // Discord interaction response types.
    internal const int ResponseChannelMessage = 4; // ephemeral replies
    internal const int ResponseDeferredUpdate = 6;
    internal const int ResponseUpdateMessage = 7;
    internal const int ResponseModal = 9;
    internal const int ResponseLaunchActivity = 12; // launch the Activity (no public card)
    internal const int EphemeralFlag = 64;

    protected DiscordInteractionHandlerBase(DiscordInteractionContext interaction)
    {
        Interaction = interaction;
    }

    // This request's interaction: who clicked, what, and the HTTP request it came on.
    protected DiscordInteractionContext Interaction { get; }

    protected HttpContext HttpContext => Interaction.HttpContext;

    protected HttpRequest Request => HttpContext.Request;

    // The same result ControllerBase.Ok(value) returns.
    internal static OkObjectResult Ok(object? value) => new(value);

    internal static IActionResult Ephemeral(string message) =>
        Ok(new { type = ResponseChannelMessage, data = new { content = message, flags = EphemeralFlag } });

    internal static object[] JobSelectRow(string prefix, string idTail, string placeholder, IReadOnlyList<string> jobs)
    {
        return SelectRow(prefix, idTail, placeholder, jobs.Select(j => (object)new { label = j, value = j }).ToArray());
    }

    internal static object[] SelectRow(string prefix, string idTail, string placeholder, object[] options)
    {
        return new object[]
        {
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        custom_id = $"{prefix}{idTail}",
                        placeholder,
                        min_values = 1,
                        max_values = 1,
                        options,
                    }
                }
            }
        };
    }

    // Ephemeral UPDATE_MESSAGE that morphs the wizard message to the next step
    // (or a final confirmation when components is empty).
    internal static IActionResult WizardStep(string content, object[] components) =>
        Ok(new { type = ResponseUpdateMessage, data = new { content, components } });

    // A picker/select step in a signup flow. When the click came from an ephemeral
    // picker (an earlier step), MORPH that message in place so the chain never piles
    // up stale "Which character…" messages; otherwise (a board click)
    // send a fresh ephemeral. The terminal step deletes the single message.
    protected IActionResult PickerResponse(string content, object[] components) =>
        Interaction.IsEphemeralSource
            ? Ok(new { type = ResponseUpdateMessage, data = new { content, components } })
            : Ok(new { type = ResponseChannelMessage, data = new { content, components, flags = EphemeralFlag } });

    // Prefixes a picker/wizard line with the event's name. Discord always shows the
    // ephemeral picker at the BOTTOM of the channel (its position can't be anchored
    // to the board the button is on), so when there are several boards this is how the
    // member tells which event the picker belongs to.
    internal static string EventHeading(string? eventName, string body)
        => string.IsNullOrWhiteSpace(eventName) ? body : $"**{eventName.Trim()}**\n{body}";

    internal static string? SelectedValue(JsonElement data) =>
        data.TryGetProperty("values", out var values)
        && values.ValueKind == JsonValueKind.Array
        && values.GetArrayLength() > 0
            ? values[0].GetString()
            : null;

    // EVERY value from a multi-select, in the order Discord sent them. Empty when the member
    // submitted nothing — which is a real answer on a min_values:0 select ("none of these"),
    // not a missing one, so this must never collapse to null the way SelectedValue does.
    internal static IReadOnlyList<string> SelectedValues(JsonElement data)
    {
        if (!data.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var picked = new List<string>(values.GetArrayLength());
        foreach (var value in values.EnumerateArray())
        {
            if (value.GetString() is { Length: > 0 } text)
            {
                picked.Add(text);
            }
        }
        return picked;
    }

    // The slot id from a picker select (value = slotId), or 0 if none/unparsable.
    internal static int SelectedSlotId(JsonElement data) =>
        int.TryParse(SelectedValue(data), out var slotId) ? slotId : 0;

    internal static string? NormalizeWizardValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "-"
        || value == DiscordEventMessageBuilder.PartyWizardNoSub
        || value == DiscordEventMessageBuilder.PartyWizardNoRole
            ? null
            : value;

    // Pulls a submitted value out of a MODAL_SUBMIT payload by its custom_id. Handles both shapes a
    // modal can render: a text input inside an action row's `components` array, and a Label-wrapped
    // string select whose child sits under the singular `component` property. Returns null when the
    // modal never rendered the field at all — callers lean on that to spot an in-flight submission
    // from an older version of a form.
    internal static string? ExtractModalValue(JsonElement data, string fieldId)
    {
        if (!data.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var row in rows.EnumerateArray())
        {
            // Action row → components[] (text inputs). Label → component (a single select).
            if (row.TryGetProperty("components", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var input in inputs.EnumerateArray())
                {
                    if (TryReadModalField(input, fieldId, out var nested)) return nested;
                }
            }
            if (row.TryGetProperty("component", out var single) && TryReadModalField(single, fieldId, out var labeled))
            {
                return labeled;
            }
            // Belt and braces: some payload shapes hoist the component straight into the row.
            if (TryReadModalField(row, fieldId, out var direct)) return direct;
        }
        return null;
    }

    // One submitted component → its value, if its custom_id matches. Text inputs carry a scalar
    // `value`; string selects carry a `values` array, which is empty when nothing was picked — that
    // comes back as "" (present but unanswered), which every caller's parse treats as its default.
    internal static bool TryReadModalField(JsonElement component, string fieldId, out string? value)
    {
        value = null;
        if (component.ValueKind != JsonValueKind.Object
            || !component.TryGetProperty("custom_id", out var cid)
            || cid.GetString() != fieldId)
        {
            return false;
        }
        if (component.TryGetProperty("value", out var scalar) && scalar.ValueKind == JsonValueKind.String)
        {
            value = scalar.GetString();
            return true;
        }
        if (component.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            value = values.GetArrayLength() > 0 ? values[0].GetString() : string.Empty;
            return true;
        }
        return false;
    }

    internal static string? ResolveDiscordUserId(JsonElement root)
    {
        // Guild interactions carry member.user.id; DM interactions carry user.id.
        if (root.TryGetProperty("member", out var member)
            && member.TryGetProperty("user", out var memberUser)
            && memberUser.TryGetProperty("id", out var memberUserId))
        {
            return memberUserId.GetString();
        }
        if (root.TryGetProperty("user", out var user) && user.TryGetProperty("id", out var userId))
        {
            return userId.GetString();
        }
        return null;
    }

    internal static int ParseTrailingId(string customId, string prefix)
    {
        var tail = customId[prefix.Length..];
        // Some components append a ":suffix" after the id to stay unique within a
        // message (e.g. the per-alliance slot pickers add the alliance index so each
        // select's custom_id differs). Parse just the leading id; the suffix, if any,
        // carries no routing meaning here.
        var colon = tail.IndexOf(':');
        if (colon >= 0) { tail = tail[..colon]; }
        return int.TryParse(tail, out var id) ? id : 0;
    }

    // The clicker's Discord display name (guild nick → global name → username), for
    // prefilling the outside-signup name modal. Not stored as the signup name.
    internal static string? ResolveDiscordDisplayName(JsonElement root)
    {
        if (root.TryGetProperty("member", out var member))
        {
            if (member.TryGetProperty("nick", out var nick) && nick.ValueKind == JsonValueKind.String
                && nick.GetString() is { Length: > 0 } nickName)
            {
                return nickName;
            }
            if (member.TryGetProperty("user", out var memberUser))
            {
                return DisplayNameFromUser(memberUser);
            }
        }
        if (root.TryGetProperty("user", out var user))
        {
            return DisplayNameFromUser(user);
        }
        return null;

        static string? DisplayNameFromUser(JsonElement user)
        {
            if (user.TryGetProperty("global_name", out var global) && global.ValueKind == JsonValueKind.String
                && global.GetString() is { Length: > 0 } globalName)
            {
                return globalName;
            }
            if (user.TryGetProperty("username", out var username) && username.GetString() is { Length: > 0 } userName)
            {
                return userName;
            }
            return null;
        }
    }
}
