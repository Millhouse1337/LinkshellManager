namespace LinkshellManagerDiscordApp.ViewModels;

public class DkpHistoryViewModel
{
    public const int DefaultPageSize = 15;

    public int? SelectedLinkshellId { get; set; }
    public string? SelectedLinkshellName { get; set; }
    public string? SelectedAppUserId { get; set; }
    public string? SelectedMemberName { get; set; }
    public double CurrentBalance { get; set; }
    // Selected member's DKP spent on loot in still-live events, not yet committed. Shown as
    // a pending deduction; already removed from biddable power.
    public double SelectedPendingLootSpend { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = DefaultPageSize;
    public int TotalEntryCount { get; set; }
    public int TotalPages => TotalEntryCount == 0
        ? 1
        : (int)Math.Ceiling(TotalEntryCount / (double)Math.Max(1, PageSize));
    public List<DkpHistoryLinkshellOptionViewModel> Linkshells { get; set; } = new();
    public List<DkpHistoryMemberOptionViewModel> Members { get; set; } = new();
    public List<DkpHistoryEntryViewModel> Entries { get; set; } = new();

    // What the selected member has EARNED, broken down by the event type that earned it — the
    // "15 Sky, 20 Sea, 5 Dynamis" view.
    //
    // These are LIFETIME EARNINGS (positive ledger amounts), NOT spendable balances: a pool balance
    // is earned minus spent. They coincide only while nothing has been spent, which is exactly why
    // they're labelled separately in the view — conflating them is the easy mistake here.
    public List<DkpEarnedByEventTypeViewModel> EarnedByEventType { get; set; } = new();

    // The selected member's SPENDABLE balance per pool. Empty when the linkshell has a single pool.
    public List<DkpPoolBalanceViewModel> PoolBalances { get; set; } = new();
    public bool HasMultiplePools => PoolBalances.Count > 1;
}

public class DkpEarnedByEventTypeViewModel
{
    public string EventType { get; set; } = string.Empty;
    public double Earned { get; set; }
    // The pool this event type currently earns into. Null when the linkshell has a single pool.
    public string? PoolName { get; set; }
}

public class DkpPoolBalanceViewModel
{
    public int PoolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Accent { get; set; } = "Neutral";
    public double Balance { get; set; }
}

public class DkpHistoryLinkshellOptionViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class DkpHistoryMemberOptionViewModel
{
    public string AppUserId { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public double CurrentBalance { get; set; }
    public double PendingLootSpend { get; set; }
}

public class DkpHistoryEntryViewModel
{
    public int Id { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public double Amount { get; set; }
    public double RunningBalance { get; set; }
    public DateTime? OccurredAt { get; set; }
    public string? EventName { get; set; }
    public string? EventType { get; set; }
    public string? EventLocation { get; set; }
    public DateTime? EventStartTime { get; set; }
    public DateTime? EventEndTime { get; set; }
    public string? ItemName { get; set; }
    public string? Details { get; set; }
    public string? EditReason { get; set; }

    // The DKP pool this row landed in. Null when the linkshell has a single pool.
    public string? DkpPoolName { get; set; }
}
