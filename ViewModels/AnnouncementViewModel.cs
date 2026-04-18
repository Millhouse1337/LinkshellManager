using LinkshellManager.Models;

namespace LinkshellManager.ViewModels
{
    public class AnnouncementViewModel
    {
        public int Id { get; set; }
        public List<Linkshell>? Linkshells { get; set; }
        public int LinkshellId { get; set; }
        public string? LinkshellName { get; set; }
        public string AnnouncementTitle { get; set; }
        public string AnnouncementDetails { get; set; }

        public AnnouncementViewModel()
        {
            Linkshells = new List<Linkshell>();
        }
    }
}