using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// A linkshell's dashboard banner image, kept in its OWN table (1:1 with
// Linkshell, shared primary key) so the image bytes never load on the hot
// dashboard/overview paths — those only need to know a banner exists + its
// version. The bytes are read only when the banner endpoint serves them.
public class LinkshellBanner
{
    [Key]
    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public byte[] ImageData { get; set; } = Array.Empty<byte>();

    [MaxLength(64)]
    public string ContentType { get; set; } = "image/png";

    // Bumped on each upload; used to cache-bust the banner URL.
    public DateTime UpdatedAt { get; set; }
}
