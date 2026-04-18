using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using LinkshellManager.Models;

namespace LinkshellManager.ViewModels
{
    public class ContactViewModel
    {
        [Required]
        public int SelectedLinkshellId { get; set; }

        public List<Linkshell> Linkshells { get; set; } = new List<Linkshell>();

        [Required]
        public string ReceiverCharacterName { get; set; }

        public List<string> Members { get; set; } = new List<string>();

        [Required]
        public string MessageDetails { get; set; }

        public List<AppUserMessage> ReceivedMessages { get; set; } = new List<AppUserMessage>();
        public List<AppUserMessage> SentMessages { get; set; } = new List<AppUserMessage>();
        public Dictionary<int, List<string>> LinkshellMembers { get; set; } = new Dictionary<int, List<string>>();
        public string CharacterNameSender { get; set; }
        public string AppUserId { get; set; }
    }
}