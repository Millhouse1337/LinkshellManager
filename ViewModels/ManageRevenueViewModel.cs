using System.Collections.Generic;
using LinkshellManager.Models;

namespace LinkshellManager.ViewModels
{
    public class ManageRevenueViewModel
    {
        public int LinkshellId { get; set; }
        public List<Linkshell> Linkshells { get; set; }
        public Income Income { get; set; }
    }
}