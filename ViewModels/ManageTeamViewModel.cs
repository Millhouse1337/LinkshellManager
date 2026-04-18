using LinkshellManager.Models;
using System.Collections.Generic;

namespace LinkshellManager.ViewModels
{
    public class ManageTeamViewModel
    {
        public string SearchTerm { get; set; }
        public List<AppUser> Players { get; set; }
        public List<Invite> PendingInvites { get; set; }
        public List<Invite> SentInvites { get; set; }
        public List<Linkshell> Linkshells { get; set; }
        public int SelectedLinkshellId { get; set; }
        public List<AppUserLinkshell> Members { get; set; } // Change this line
        public List<Invite> Invites { get; set; }
    }
}