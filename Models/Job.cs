using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Job
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    public string? JobName { get; set; }

    public string? SubJobName { get; set; }

    public string? JobType { get; set; }

    public int? Quantity { get; set; }

    public int? SignedUp { get; set; }

    public List<string> Enlisted { get; set; } = new();

    public string? Details { get; set; }
}
