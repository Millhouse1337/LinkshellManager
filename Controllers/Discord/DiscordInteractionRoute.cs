using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// The three kinds of Discord interaction the bot routes by an id.
public enum DiscordInteractionKind
{
    Component, // a button or select; routed by its custom_id
    Modal,     // a modal submit; routed by the modal's custom_id
    Command,   // a slash command; routed by the command's name
}

// One id the Discord bot answers, and the code that answers it.
//
//   Component / Modal: `Id` is a custom_id PREFIX ("evt:pssignup:" answers "evt:pssignup:42"), or,
//                      with Exact, the whole custom_id.
//   Command:           `Id` is the command's name, matched ignoring case.
//
// `Handler` names the method the route reaches. It is only for people (and the routing snapshot
// test); routing never reads it.
public sealed record DiscordInteractionRoute(
    DiscordInteractionKind Kind,
    string Id,
    string Handler,
    Func<CancellationToken, Task<IActionResult>> HandleAsync)
{
    public bool Exact { get; init; }

    // A button answered BEFORE the clicker is looked up: no Discord id check and no database
    // query. Only the launch card's Join button needs nothing more.
    public bool BeforeIdentity { get; init; }

    // A modal that needs the submitter's linked LSM account. The dispatcher looks the account up
    // first and answers "sign in" when there is none, so the handler can read
    // DiscordInteractionContext.LinkedAccount.
    public bool NeedsLinkedAccount { get; init; }

    public bool Matches(string id) => Kind == DiscordInteractionKind.Command
        ? string.Equals(id, Id, StringComparison.OrdinalIgnoreCase)
        : Exact
            ? string.Equals(id, Id, StringComparison.Ordinal)
            : id.StartsWith(Id, StringComparison.Ordinal);
}

// A class that answers some of the Discord bot's interactions. Each one lists the ids it owns;
// DiscordInteractionDispatcher collects every registered handler's routes into one table.
public interface IDiscordInteractionHandler
{
    IReadOnlyList<DiscordInteractionRoute> Routes { get; }
}
