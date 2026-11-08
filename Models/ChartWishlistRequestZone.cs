using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One zone (card) an item request names. A request names none ("anywhere on this board"), one, or
/// several - an item can drop in more than one zone, and the request belongs on each of their cards.
///
/// Its own table rather than a delimited string on the request, so "how many requests does this
/// card have" stays a count of rows.
/// </summary>
public class ChartWishlistRequestZone
{
    [Key]
    public int Id { get; set; }

    public int RequestId { get; set; }

    [ForeignKey(nameof(RequestId))]
    public ChartWishlistRequest? Request { get; set; }

    /// <summary>The card's name as the board catalog spells it ("Xarcabard", "NW").</summary>
    [MaxLength(64)]
    public string Boss { get; set; } = string.Empty;
}
