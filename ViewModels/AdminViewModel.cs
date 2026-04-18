using LinkshellManager.Models;

namespace LinkshellManager.ViewModels
{
    public class AdminViewModel
    {
        public int Id { get; set; }
        public string? AppUserId { get; set; }

        public IEnumerable<AppUser> Users { get; set; }
    }
}
