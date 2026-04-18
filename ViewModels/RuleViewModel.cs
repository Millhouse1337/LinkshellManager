using LinkshellManager.Models;

namespace LinkshellManager.ViewModels
{
    public class RuleViewModel
    {
        public int Id { get; set; }
        public List<Linkshell>? Linkshells { get; set; }
        public int LinkshellId { get; set; }
        public string? LinkshellName { get; set; }
        public string RuleTitle { get; set; }
        public string RuleDetails { get; set; }

        public RuleViewModel()
        {
            Linkshells = new List<Linkshell>();
        }
    }
}