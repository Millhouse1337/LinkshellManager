namespace LinkshellManager.Models
{
    public class AppUserJob
    {
        public int Id { get; set; }
        public string? AppUserId { get; set; }
        public int JobId { get; set; }
        public AppUser? AppUser { get; set; }
        public Job? Job { get; set; }
    }
}
