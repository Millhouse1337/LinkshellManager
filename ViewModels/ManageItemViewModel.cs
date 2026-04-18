using System.Collections.Generic;
using LinkshellManager.Models;

namespace LinkshellManager.ViewModels
{
    public class ManageItemViewModel
    {
        public int LinkshellId { get; set; }
        public List<Linkshell> Linkshells { get; set; }
        public Item Item { get; set; }
    }
}