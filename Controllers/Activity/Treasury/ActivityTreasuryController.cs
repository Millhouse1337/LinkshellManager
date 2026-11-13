using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Treasury: the gil ledger (balances, entries, drafts, confirm, reverse, fix) and settling what
// members owe or are owed. Split out of ActivityDataController; the endpoints, URLs and rules are
// unchanged.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityTreasuryController : ActivityControllerBase
{
    private readonly TreasuryBalanceService _treasury;
    private readonly TreasuryJournalWriter _treasuryJournal;
    private readonly TreasurySettlementService _treasurySettlements;
    private readonly LedgerAccountProvisioner _ledgerAccounts;

    public ActivityTreasuryController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        ILogger<ActivityTreasuryController> logger,
        TreasuryBalanceService treasury,
        TreasuryJournalWriter treasuryJournal,
        TreasurySettlementService treasurySettlements,
        LedgerAccountProvisioner ledgerAccounts,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _treasury = treasury;
        _treasuryJournal = treasuryJournal;
        _treasurySettlements = treasurySettlements;
        _ledgerAccounts = ledgerAccounts;
    }
}
